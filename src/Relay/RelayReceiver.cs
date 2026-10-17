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
    // with a 6-digit code (also shown on the game PC) and waits up to 60 s for 허용/거절.
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

        // userAction: true when the user just pressed "받기 켜기", so asking for UAC is expected.
        public void SetEnabled(bool on, bool userAction)
        {
            lock (settings)
            {
                settings.Receive = on;
                settings.Save();
            }
            if (!on) Log.Info("  투컴 받기를 껐어요.");
            Restart(userAction);
        }

        // "공용에서도 받기": also accept game PCs while every network is "Public". The firewall rules
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
                ? "  공용 네트워크에서도 게임 PC 연결을 받도록 바꿨어요 (KbmOverlay만, 포트 " + port + "만, 같은 네트워크에서만)."
                : "  공용 네트워크에서는 게임 PC 연결을 받지 않도록 바꿨어요.");
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
                            Log.Warn("  [!] 이 PC의 네트워크가 '공용'이라서 게임 PC가 접속할 수 없어요. 둘 중 하나를 해 주세요.");
                            Log.Warn("      - 설정 페이지에서 '공용에서도 받기'를 눌러요 (KbmOverlay만 허용).");
                            Log.Warn("      - Windows 설정 > 네트워크 및 인터넷에서 연결된 네트워크를 '개인'으로 바꿔요.");
                        }
                        Thread.Sleep(5000); // start as soon as it becomes private
                        continue;
                    }

                    if (manageFirewall)
                    {
                        Firewall.Status status = Firewall.Check(exePath, port);
                        bool tooNarrow = !status.Covers(required);
                        // Turning "공용에서도 받기" off should also take Public out of our rules.
                        bool tooWide = !allowPublic && (status.OwnRuleProfiles & Firewall.ProfilePublic) != 0;
                        if (tooNarrow || (tooWide && userAction))
                        {
                            if (!userAction)
                            {
                                SetProblem(myGeneration, "firewall-needed");
                                Log.Warn("  [!] 투컴 받기에 필요한 방화벽 허용이 없어요. 설정 페이지에서 '게임 PC 입력 받기'를 다시 눌러 주세요.");
                                return;
                            }
                            Log.Info("  방화벽 규칙을 바꾸는 중이에요. Windows가 관리자 권한을 물으면 '예'를 눌러 주세요.");
                            string error = Firewall.RunElevatedSetup(exePath, port, allowPublic);
                            if (error != null)
                            {
                                SetProblem(myGeneration, "firewall-failed");
                                Log.Error("  " + error + " 투컴 받기를 켜지 못했어요.");
                                return;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is COMException || e is InvalidCastException || e is TypeLoadException ||
                                          e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    Log.Warn("  방화벽 상태를 확인하지 못했어요 (" + e.Message + "). 그대로 진행할게요.");
                }
                break;
            }

            lock (gate)
            {
                if (myGeneration != generation) return;
                try
                {
                    listener = new TcpListener(bindAddress, port);
                    listener.ExclusiveAddressUse = true;
                    listener.Start();
                    responder = Discovery.Respond(bindAddress, port, settings.Id, DiscoveryAnswer);
                }
                catch (SocketException e)
                {
                    StopLocked();
                    Problem = "port-in-use";
                    Log.Error("  투컴용 포트 " + port + "를 열 수 없어요: " + e.Message);
                    changed();
                    return;
                }
                Listening = true;
                Problem = null;
                TcpListener current = listener;
                var thread = new Thread(() => AcceptLoop(current));
                thread.IsBackground = true;
                thread.Name = "RelayAccept";
                thread.Start();
            }
            Log.Good("  투컴 받기 켜짐: 게임 PC에서 KbmOverlay를 실행하면 이 PC(" + NetInfo.MachineName + ")를 찾아요.");
            changed();
        }

        private bool SetProblem(int myGeneration, string problem)
        {
            lock (gate)
            {
                if (myGeneration != generation) return false;
                bool differs = Problem != problem;
                Problem = problem;
                if (differs) changed();
                return true;
            }
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
                Log.Error("게임 PC 연결을 처리하다가 오류가 났어요: " + e.Message);
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
            Log.Warn("  게임 PC '" + name + "'(" + address + ")가 이 PC로 키보드·마우스 입력을 보내려고 해요.");
            Log.Warn("  확인 숫자: " + code + "  (게임 PC 화면의 숫자와 같으면 허용하세요)");
            Log.Warn("  허용하려면 Y, 거절하려면 N을 누르세요. 설정 페이지에서도 고를 수 있어요.");
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
                Log.Good("  '" + name + "'를 허용했어요. 다음부터는 묻지 않고 연결돼요.");
            }
            else
            {
                Log.Info("  '" + name + "'의 연결 요청을 " + (request.Decided.IsSet ? "거절했어요." : "받지 않았어요 (시간 초과)."));
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
            Log.Good("  게임 PC '" + name + "'(" + address + ") 연결됨. 이제 오버레이에 게임 PC 입력이 보여요.");
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
                    Log.Warn("  게임 PC '" + session.Name + "' 연결이 끊겼어요. 다시 연결될 때까지 이 PC 입력을 보여줘요.");
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
