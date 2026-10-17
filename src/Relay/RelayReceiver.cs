using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KbmOverlay.Relay
{
    // Streaming PC side: accepts game PCs on the LAN.
    //
    // Connection: hello exchange (plain), then everything encrypted. A paired game PC proves it knows
    // the pairing secret and is shown right away. A new one asks for pairing; this PC shows a prompt
    // with a 6-digit code (also shown on the game PC) and waits up to 60 s for Allow/Deny.
    // Only one game PC is shown at a time.
    internal sealed class RelayReceiver
    {
        private const int HandshakeLimit = 4096;
        private const int ApprovalTimeoutMs = 60000;
        private const int IdleTimeoutMs = 15000;
        private const int DenyCooldownMs = 60000;

        public sealed class Pending
        {
            public string RequestId;
            public string PeerId;
            public string Name;
            public string Address;
            public string Code;
            public bool Approved;
            public readonly ManualResetEventSlim Decided = new ManualResetEventSlim();
        }

        private sealed class Session
        {
            public string Id;
            public string PeerId;
            public string Name;
            public string Address;
            public TcpClient Tcp;
        }

        private readonly Settings settings;
        private readonly InputHub hub;
        private readonly int port;
        private readonly IPAddress bindAddress;
        private readonly string exePath;
        private readonly bool manageFirewall;
        private readonly Func<bool> onlyPublicNetwork;
        private readonly Action changed;

        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> deniedUntil = new Dictionary<string, DateTime>();
        private TcpListener listener;
        private UdpClient responder;
        private Pending pending;
        private Session active;
        private int generation; // bumps on every enable/disable so stale start attempts give up

        public string Problem { get; private set; }
        public bool Listening { get; private set; }

        // onlyPublicNetwork: Firewall.OnlyPublicNetwork, or a stand-in for development instances.
        public RelayReceiver(Settings settings, InputHub hub, int port, IPAddress bindAddress, string exePath, bool manageFirewall,
                             Func<bool> onlyPublicNetwork, Action changed)
        {
            this.settings = settings;
            this.hub = hub;
            this.port = port;
            this.bindAddress = bindAddress;
            this.exePath = exePath;
            this.manageFirewall = manageFirewall;
            this.onlyPublicNetwork = onlyPublicNetwork;
            this.changed = changed;
        }

        public int Port => port;

        // userAction: true when the user just pressed "Receive from game PCs", so asking for UAC is expected.
        public void SetEnabled(bool on, bool userAction)
        {
            lock (settings)
            {
                settings.Receive = on;
                settings.Save();
            }
            if (!on) Log.Info(I18n.T("receive.off"));
            Restart(userAction);
        }

        // "Also receive on Public networks": also accept game PCs while every network is "Public". The firewall rules
        // get the Public profile too (still this exe, this port, local subnet only). Always a user action.
        public void SetAllowPublic(bool on)
        {
            bool receive;
            lock (settings)
            {
                settings.AllowPublic = on;
                settings.Save();
                receive = settings.Receive;
            }
            Log.Info(on
                ? I18n.T("receive.publicOn", ("port", port))
                : I18n.T("receive.publicOff"));
            if (receive) Restart(true);
            else changed();
        }

        private void Restart(bool userAction)
        {
            bool on;
            int myGeneration;
            lock (settings) on = settings.Receive;
            lock (gate)
            {
                myGeneration = ++generation;
                StopLocked();
                Problem = null;
            }
            if (on)
            {
                var thread = new Thread(() => StartListening(myGeneration, userAction));
                thread.IsBackground = true;
                thread.Name = "RelayStart";
                thread.Start();
            }
            changed();
        }

        private void StartListening(int myGeneration, bool userAction)
        {
            bool allowPublic;
            lock (settings) allowPublic = settings.AllowPublic;
            int required = Firewall.ProfileDomain | Firewall.ProfilePrivate | (allowPublic ? Firewall.ProfilePublic : 0);

            bool announcedPublic = false;
            while (true)
            {
                try
                {
                    if (!allowPublic && onlyPublicNetwork())
                    {
                        if (!SetProblem(myGeneration, "public-network")) return;
                        if (!announcedPublic)
                        {
                            announcedPublic = true;
                            Log.Warn(I18n.T("receive.public1"));
                            Log.Warn(I18n.T("receive.public2"));
                            Log.Warn(I18n.T("receive.public3"));
                        }
                        Thread.Sleep(5000); // start as soon as it becomes private
                        continue;
                    }

                    if (manageFirewall)
                    {
                        Firewall.Status status = Firewall.Check(exePath, port);
                        bool tooNarrow = !status.Covers(required);
                        // Turning "Also receive on Public networks" off should also take Public out of our rules.
                        bool tooWide = !allowPublic && (status.OwnRuleProfiles & Firewall.ProfilePublic) != 0;
                        if (tooNarrow || (tooWide && userAction))
                        {
                            if (!userAction)
                            {
                                SetProblem(myGeneration, "firewall-needed");
                                Log.Warn(I18n.T("receive.firewallNeeded"));
                                return;
                            }
                            Log.Info(I18n.T("receive.firewallChanging"));
                            string error = Firewall.RunElevatedSetup(exePath, port, allowPublic);
                            if (error != null)
                            {
                                SetProblem(myGeneration, "firewall-failed");
                                Log.Error(I18n.T("receive.firewallError", ("error", error)));
                                return;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is COMException || e is InvalidCastException || e is TypeLoadException ||
                                          e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    Log.Warn(I18n.T("receive.firewallUnknown", ("error", e.Message)));
                }
                break;
            }

            bool started = false;
            lock (gate)
            {
                if (myGeneration != generation) return;
                try
                {
                    listener = new TcpListener(bindAddress, port);
                    listener.ExclusiveAddressUse = true;
                    listener.Start();
                    responder = Discovery.Respond(bindAddress, port, settings.Id, DiscoveryAnswer);
                    started = true;
                }
                catch (SocketException e)
                {
                    StopLocked();
                    Problem = "port-in-use";
                    Log.Error(I18n.T("receive.portFailed", ("port", port), ("error", e.Message)));
                }
                if (started)
                {
                    Listening = true;
                    Problem = null;
                    TcpListener current = listener;
                    var thread = new Thread(() => AcceptLoop(current));
                    thread.IsBackground = true;
                    thread.Name = "RelayAccept";
                    thread.Start();
                }
            }
            if (started) Log.Good(I18n.T("receive.on", ("machine", NetInfo.MachineName)));
            changed();
        }

        // changed() runs after leaving gate, never inside it: sending the status takes each settings page's
        // send lock, and a page that is just connecting holds that lock while Status() waits for gate.
        private bool SetProblem(int myGeneration, string problem)
        {
            bool differs;
            lock (gate)
            {
                if (myGeneration != generation) return false;
                differs = Problem != problem;
                Problem = problem;
            }
            if (differs) changed();
            return true;
        }

        private void StopLocked()
        {
            Listening = false;
            try { listener?.Stop(); } catch (SocketException) { }
            listener = null;
            responder?.Close();
            responder = null;
            active?.Tcp.Close();
            if (pending != null)
            {
                pending.Approved = false;
                pending.Decided.Set();
            }
        }

        private string DiscoveryAnswer()
        {
            return Json.Write(new Dictionary<string, object>
            {
                ["kbmo"] = 1,
                ["id"] = settings.Id,
                ["name"] = NetInfo.MachineName,
                ["port"] = port
            });
        }

        // ---- approval (from the console or the settings page) ----

        public Pending CurrentPending
        {
            get { lock (gate) return pending; }
        }

        public void Decide(string requestId, bool approve)
        {
            lock (gate)
            {
                if (pending == null || (requestId != null && pending.RequestId != requestId)) return;
                pending.Approved = approve;
                pending.Decided.Set();
            }
        }

        public void Forget(string peerId)
        {
            lock (settings)
            {
                settings.Senders.RemoveAll(p => p.Id == peerId);
                settings.Save();
            }
            lock (gate)
            {
                if (active != null && active.PeerId == peerId) active.Tcp.Close();
            }
            changed();
        }

        public Dictionary<string, object> Status()
        {
            var paired = new List<object>();
            lock (settings)
                foreach (Peer peer in settings.Senders)
                    paired.Add(new Dictionary<string, object> { ["id"] = peer.Id, ["name"] = peer.Name });
            lock (gate)
            {
                return new Dictionary<string, object>
                {
                    ["on"] = settings.Receive,
                    ["allowPublic"] = settings.AllowPublic,
                    ["listening"] = Listening,
                    ["problem"] = Problem,
                    ["port"] = port,
                    ["paired"] = paired,
                    ["active"] = active == null ? null : new Dictionary<string, object> { ["name"] = active.Name, ["address"] = active.Address },
                    ["pending"] = pending == null ? null : new Dictionary<string, object>
                    {
                        ["id"] = pending.RequestId,
                        ["name"] = pending.Name,
                        ["address"] = pending.Address,
                        ["code"] = pending.Code
                    }
                };
            }
        }

        // ---- connections ----

        private void AcceptLoop(TcpListener current)
        {
            while (true)
            {
                TcpClient tcp;
                try
                {
                    tcp = current.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    lock (gate) if (listener != current) return;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                var thread = new Thread(() => Handle(tcp));
                thread.IsBackground = true;
                thread.Name = "RelayIn";
                thread.Start();
            }
        }

        private void Handle(TcpClient tcp)
        {
            try
            {
                IPAddress remoteAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                if (!NetInfo.IsLan(remoteAddress)) return;
                string address = NetInfo.Show(remoteAddress);

                tcp.NoDelay = true;
                NetworkStream stream = tcp.GetStream();
                stream.ReadTimeout = 10000;
                stream.WriteTimeout = 5000;

                byte[] clientHello = Frames.Read(stream, HandshakeLimit);
                Dictionary<string, object> hello = Json.Parse(Encoding.UTF8.GetString(clientHello));
                string peerId = Json.Str(hello, "id");
                string name = NetInfo.CleanName(Json.Str(hello, "name"));
                byte[] peerKey = FromBase64(Json.Str(hello, "pub"));
                if (Json.Long(hello, "kbmo") != 1 || !Settings.IsId(peerId) || peerId == settings.Id || !RelayCrypto.IsPublicBlob(peerKey)) return;
                if (IsCoolingDown(peerId, address)) return;

                byte[] secret;
                lock (settings) secret = settings.FindSender(peerId)?.Secret;

                using (ECDiffieHellmanCng key = RelayCrypto.NewKey())
                {
                    byte[] serverHello = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                    {
                        ["kbmo"] = 1,
                        ["id"] = settings.Id,
                        ["name"] = NetInfo.MachineName,
                        ["pub"] = Convert.ToBase64String(RelayCrypto.PublicBlob(key)),
                        ["known"] = secret != null
                    }));
                    Frames.Write(stream, serverHello);

                    SessionKeys keys = RelayCrypto.Derive(key, peerKey, clientHello, serverHello, false);
                    var channel = new SecureChannel(stream, keys);
                    Dictionary<string, object> request = Json.Parse(channel.Receive(HandshakeLimit));

                    switch (Json.Str(request, "type"))
                    {
                        case "auth":
                            byte[] proof = FromBase64(Json.Str(request, "proof"));
                            if (secret == null || !RelayCrypto.SameBytes(proof, RelayCrypto.Proof(secret, "client", keys.Transcript)))
                            {
                                channel.Send("{\"type\":\"denied\",\"reason\":\"unknown\"}");
                                return;
                            }
                            if (!TryBegin(peerId, name, address, tcp, out Session session))
                            {
                                channel.Send("{\"type\":\"busy\"}");
                                return;
                            }
                            channel.Send(Json.Write(new Dictionary<string, object>
                            {
                                ["type"] = "ok",
                                ["proof"] = Convert.ToBase64String(RelayCrypto.Proof(secret, "server", keys.Transcript))
                            }));
                            Run(session, stream, channel);
                            break;

                        case "pair":
                            if (!AskUser(peerId, name, address, keys.Code, tcp))
                            {
                                channel.Send("{\"type\":\"denied\"}");
                                return;
                            }
                            lock (settings)
                            {
                                settings.Senders.RemoveAll(p => p.Id == peerId);
                                settings.Senders.Add(new Peer { Id = peerId, Name = name, Secret = keys.PairSecret });
                                settings.Save();
                            }
                            if (!TryBegin(peerId, name, address, tcp, out Session paired))
                            {
                                channel.Send("{\"type\":\"busy\"}");
                                return;
                            }
                            channel.Send("{\"type\":\"ok\"}");
                            Run(paired, stream, channel);
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException ||
                                      e is InvalidDataException || e is CryptographicException || e is ArgumentException ||
                                      e is FormatException)
            {
                // Dropped connection or something that is not a KbmOverlay game PC.
            }
            catch (Exception e)
            {
                Log.Error(I18n.T("receive.error", ("error", e.Message)));
            }
            finally
            {
                tcp.Close();
            }
        }

        // Shows the prompt and waits for the user. Returns true if approved.
        private bool AskUser(string peerId, string name, string address, string code, TcpClient tcp)
        {
            var request = new Pending
            {
                RequestId = Guid.NewGuid().ToString("N"),
                PeerId = peerId,
                Name = name,
                Address = address,
                Code = code
            };
            lock (gate)
            {
                if (pending != null || !Listening) return false;
                pending = request;
            }

            Log.Info("");
            Log.Warn(I18n.T("receive.request1", ("name", name), ("address", address)));
            Log.Warn(I18n.T("receive.request2", ("code", code)));
            Log.Warn(I18n.T("receive.request3"));
            Native.FlashConsole();
            changed();

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ApprovalTimeoutMs);
            while (!request.Decided.Wait(500))
            {
                // Stop waiting if the game PC gave up (closed the connection).
                bool closed = tcp.Client.Poll(0, SelectMode.SelectRead) && tcp.Client.Available == 0;
                if (closed || DateTime.UtcNow > deadline) break;
            }

            lock (gate)
            {
                if (pending == request) pending = null;
            }
            bool approved = request.Decided.IsSet && request.Approved;
            if (approved)
            {
                Log.Good(I18n.T("receive.allowed", ("name", name)));
            }
            else
            {
                Log.Info(I18n.T(request.Decided.IsSet ? "receive.denied" : "receive.expired", ("name", name)));
                lock (gate) deniedUntil[peerId + "@" + address] = DateTime.UtcNow.AddMilliseconds(DenyCooldownMs);
            }
            changed();
            return approved;
        }

        private bool IsCoolingDown(string peerId, string address)
        {
            lock (gate)
            {
                string key = peerId + "@" + address;
                if (!deniedUntil.TryGetValue(key, out DateTime until)) return false;
                if (DateTime.UtcNow < until) return true;
                deniedUntil.Remove(key);
                return false;
            }
        }

        private bool TryBegin(string peerId, string name, string address, TcpClient tcp, out Session session)
        {
            session = null;
            lock (gate)
            {
                if (!Listening) return false;
                if (active != null)
                {
                    if (active.PeerId != peerId) return false;
                    active.Tcp.Close(); // the same game PC reconnected; drop the old connection
                }
                session = new Session { Id = Guid.NewGuid().ToString("N"), PeerId = peerId, Name = name, Address = address, Tcp = tcp };
                active = session;
                hub.BeginRemote(session.Id);
            }
            Log.Good(I18n.T("receive.connected", ("name", name), ("address", address)));
            changed();
            return true;
        }

        private void Run(Session session, NetworkStream stream, SecureChannel channel)
        {
            try
            {
                stream.ReadTimeout = IdleTimeoutMs; // the game PC pings every 5 s
                while (true)
                {
                    Dictionary<string, object> message = Json.Parse(channel.Receive(16 * 1024));
                    if (message == null) continue;
                    if (Json.Str(message, "type") == "ping")
                        channel.Send("{\"type\":\"pong\"}");
                    else
                        hub.ApplyRemote(session.Id, message);
                }
            }
            finally
            {
                bool wasActive;
                lock (gate)
                {
                    wasActive = active == session;
                    if (wasActive) active = null;
                }
                hub.EndRemote(session.Id);
                if (wasActive)
                {
                    Log.Warn(I18n.T("receive.disconnected", ("name", session.Name)));
                    changed();
                }
            }
        }

        private static byte[] FromBase64(string text)
        {
            if (text == null || text.Length > 256) return null;
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
