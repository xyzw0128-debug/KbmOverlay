using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KbmOverlay.Relay
{
    // Game PC side: finds streaming PCs and sends this PC's input to the one it is paired with.
    //
    // Nothing is sent anywhere until the user picks a streaming PC here (S in the console, or the
    // settings page) AND approves it there. After that, this PC connects by itself whenever both run.
    internal sealed class RelaySender
    {
        private const int HandshakeLimit = 4096;
        private const int ConnectTimeoutMs = 3000;
        private const int RetryMs = 3000;
        private const int PingMs = 5000;
        private const int IdleTimeoutMs = 15000;
        private const int SearchEveryMs = 10000;
        private const int FoundExpiryMs = 35000;

        private sealed class Target
        {
            public string Id;
            public string Name;
            public string Address;
            public int Port;
        }

        private enum Outcome { Ended, Failed, Denied, Forgotten }

        private readonly Settings settings;
        private readonly InputHub hub;
        private readonly int defaultPort;
        private readonly bool broadcast;
        private readonly List<IPEndPoint> extraProbes;
        private readonly Action changed;

        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly AutoResetEvent searchNow = new AutoResetEvent(false);
        private readonly List<FoundReceiver> found = new List<FoundReceiver>();
        private readonly HashSet<string> announced = new HashSet<string>();

        private Target consent;       // streaming PC the user picked; only this one may be paired with
        private string forgottenId;   // paired streaming PC that no longer knows this PC
        private List<FoundReceiver> choosing; // list shown after S when several were found
        private TcpClient current;
        private string state = "idle";
        private string targetName;
        private string code;
        private string message;
        private string lastLogged;

        public RelaySender(Settings settings, InputHub hub, int defaultPort, bool broadcast, List<IPEndPoint> extraProbes, Action changed)
        {
            this.settings = settings;
            this.hub = hub;
            this.defaultPort = defaultPort;
            this.broadcast = broadcast;
            this.extraProbes = extraProbes;
            this.changed = changed;
        }

        public void Start()
        {
            Spawn(SearchLoop, "RelaySearch");
            Spawn(ConnectLoop, "RelayOut");
        }

        // ---- user choices ----

        public bool ChooseFound(string id)
        {
            FoundReceiver receiver;
            lock (gate)
            {
                receiver = found.Find(f => f.Id == id);
                if (receiver == null) return false;
                consent = new Target { Id = receiver.Id, Name = receiver.Name, Address = receiver.Address, Port = receiver.Port };
                forgottenId = null;
                choosing = null;
                message = null;
                current?.Close();
            }
            Log.Info("  방송 PC '" + receiver.Name + "'에 연결을 요청할게요.");
            wake.Set();
            changed();
            return true;
        }

        // "192.168.0.20" or "192.168.0.20:5521", for networks where searching does not work.
        public string ChooseAddress(string text)
        {
            text = (text ?? "").Trim();
            int port = defaultPort;
            int colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text.Substring(colon + 1), out int p) && p > 0 && p < 65536)
            {
                port = p;
                text = text.Substring(0, colon);
            }
            if (!IPAddress.TryParse(text, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork)
                return "IP 주소 형식이 아니에요 (예: 192.168.0.20).";
            if (!NetInfo.IsLan(address))
                return "같은 공유기(사설 네트워크) 안의 주소만 쓸 수 있어요.";
            lock (gate)
            {
                consent = new Target { Address = address.ToString(), Port = port, Name = address.ToString() };
                forgottenId = null;
                message = null;
                current?.Close();
            }
            Log.Info("  " + address + "에 연결을 요청할게요.");
            wake.Set();
            changed();
            return null;
        }

        public void StopSending()
        {
            lock (settings)
            {
                settings.Receiver = null;
                settings.Save();
            }
            lock (gate)
            {
                consent = null;
                forgottenId = null;
                announced.Clear();
                message = null;
                current?.Close();
            }
            Log.Info("  방송 PC로 보내기를 껐어요.");
            wake.Set();
            searchNow.Set();
            changed();
        }

        public void SearchNow() => searchNow.Set();

        // Console keys: S picks the streaming PC that was found, 1-9 picks from a list.
        public bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.S)
            {
                List<FoundReceiver> list;
                lock (gate) list = new List<FoundReceiver>(found);
                if (list.Count == 0)
                {
                    Log.Info("  아직 찾은 방송 PC가 없어요. 방송 PC의 KbmOverlay에서 '게임 PC 입력 받기'가 켜져 있는지 확인하세요.");
                    SearchNow();
                }
                else if (list.Count == 1)
                {
                    ChooseFound(list[0].Id);
                }
                else
                {
                    lock (gate) choosing = list.GetRange(0, Math.Min(9, list.Count));
                    Log.Info("  보낼 방송 PC의 번호를 누르세요:");
                    for (int i = 0; i < choosing.Count; i++)
                        Log.Info("    " + (i + 1) + ". " + choosing[i].Name + " (" + choosing[i].Address + ")");
                }
                return true;
            }
            int number = key.Key >= ConsoleKey.D1 && key.Key <= ConsoleKey.D9 ? key.Key - ConsoleKey.D1
                       : key.Key >= ConsoleKey.NumPad1 && key.Key <= ConsoleKey.NumPad9 ? key.Key - ConsoleKey.NumPad1 : -1;
            if (number >= 0)
            {
                FoundReceiver pick = null;
                lock (gate)
                {
                    int index = number;
                    if (choosing != null && index < choosing.Count) pick = choosing[index];
                }
                if (pick == null) return false;
                ChooseFound(pick.Id);
                return true;
            }
            return false;
        }

        public Dictionary<string, object> Status()
        {
            Peer receiver;
            lock (settings) receiver = settings.Receiver;
            lock (gate)
            {
                var list = new List<object>();
                foreach (FoundReceiver f in found)
                    list.Add(new Dictionary<string, object> { ["id"] = f.Id, ["name"] = f.Name, ["address"] = f.Address });
                return new Dictionary<string, object>
                {
                    ["state"] = state,
                    ["target"] = targetName,
                    ["code"] = code,
                    ["message"] = message,
                    ["receiver"] = receiver == null ? null : new Dictionary<string, object> { ["id"] = receiver.Id, ["name"] = receiver.Name, ["address"] = receiver.Address },
                    ["found"] = list
                };
            }
        }

        // ---- searching ----

        private void SearchLoop()
        {
            while (true)
            {
                try
                {
                    bool sending;
                    lock (gate) sending = state == "sending";
                    if (!sending) Search();
                }
                catch (Exception e)
                {
                    Log.Error("방송 PC를 찾다가 오류가 났어요: " + e.Message);
                }
                searchNow.WaitOne(SearchEveryMs);
            }
        }

        private void Search()
        {
            List<FoundReceiver> results = Discovery.Search(settings.Id, defaultPort, extraProbes, broadcast, 1200);
            long now = clock.ElapsedMilliseconds;
            var fresh = new List<FoundReceiver>();
            bool listChanged;
            Peer receiver;
            lock (settings) receiver = settings.Receiver;
            lock (gate)
            {
                int before = found.Count;
                foreach (FoundReceiver result in results)
                {
                    result.SeenAt = now;
                    int index = found.FindIndex(f => f.Id == result.Id);
                    if (index >= 0) found[index] = result;
                    else found.Add(result);
                    if (receiver == null && consent == null && announced.Add(result.Id)) fresh.Add(result);
                }
                found.RemoveAll(f => now - f.SeenAt > FoundExpiryMs);
                listChanged = found.Count != before || results.Count > 0;
            }

            foreach (FoundReceiver f in fresh)
            {
                Log.Info("");
                Log.Accent("  방송 PC '" + f.Name + "'(" + f.Address + ")를 찾았어요.");
                Log.Accent("  이 PC의 키보드·마우스 입력을 그쪽으로 보내려면 이 창에서 S를 누르세요 (설정 페이지에서도 돼요).");
            }
            if (results.Count > 0) wake.Set();
            if (listChanged) changed();
        }

        // ---- connecting ----

        private void ConnectLoop()
        {
            while (true)
            {
                try
                {
                    ConnectOnce();
                }
                catch (Exception e)
                {
                    Log.Error("방송 PC 연결 중 오류가 났어요: " + e.Message);
                    wake.WaitOne(RetryMs);
                }
            }
        }

        private void ConnectOnce()
        {
            Target target = PickTarget(out bool mayPair);
            if (target == null)
            {
                SetState(state == "denied" || state == "forgotten" ? state : "idle", null, null);
                wake.WaitOne();
                return;
            }

            lock (gate)
            {
                FoundReceiver seen = target.Id == null ? null : found.Find(f => f.Id == target.Id);
                if (seen != null)
                {
                    target.Address = seen.Address;
                    target.Port = seen.Port;
                }
            }
            if (target.Address == null)
            {
                SetState("searching", target.Name, null);
                wake.WaitOne(RetryMs);
                return;
            }

            Outcome outcome = Connect(target, mayPair, out string failure);
            switch (outcome)
            {
                case Outcome.Ended:
                    if (PickTarget(out _) != null) // not when the user just turned sending off
                        Log.Warn("  방송 PC '" + target.Name + "'와 연결이 끊겼어요. 다시 연결하는 중이에요.");
                    SetState("retrying", target.Name, null);
                    wake.WaitOne(1000);
                    break;
                case Outcome.Failed:
                    if (failure != lastLogged) Log.Warn("  " + failure);
                    lastLogged = failure;
                    lock (gate) message = failure;
                    SetState("retrying", target.Name, null);
                    wake.WaitOne(RetryMs);
                    break;
                case Outcome.Denied:
                    Log.Warn("  방송 PC에서 연결을 거절했어요.");
                    DropPairing(target.Id);
                    lock (gate)
                    {
                        consent = null;
                        message = "방송 PC에서 거절했어요.";
                    }
                    SetState("denied", target.Name, null);
                    break;
                case Outcome.Forgotten:
                    Log.Warn("  방송 PC '" + target.Name + "'가 이 PC를 기억하지 못해요. 다시 보내려면 S를 누르세요.");
                    DropPairing(target.Id);
                    lock (gate)
                    {
                        forgottenId = target.Id;
                        announced.Remove(target.Id ?? "");
                        message = "방송 PC가 이 PC를 기억하지 못해요. 다시 골라 주세요.";
                    }
                    SetState("forgotten", target.Name, null);
                    break;
            }
        }

        private Target PickTarget(out bool mayPair)
        {
            lock (gate)
            {
                mayPair = consent != null;
                if (consent != null) return new Target { Id = consent.Id, Name = consent.Name, Address = consent.Address, Port = consent.Port };
            }
            Peer receiver;
            lock (settings) receiver = settings.Receiver;
            lock (gate)
            {
                if (receiver == null || receiver.Id == forgottenId) return null;
                return new Target { Id = receiver.Id, Name = receiver.Name, Address = receiver.Address, Port = receiver.Port > 0 ? receiver.Port : defaultPort };
            }
        }

        private Outcome Connect(Target target, bool mayPair, out string failure)
        {
            failure = null;
            var tcp = new TcpClient(AddressFamily.InterNetwork);
            lock (gate) current = tcp;
            try
            {
                SetState("connecting", target.Name, null);
                IAsyncResult pendingConnect = tcp.BeginConnect(target.Address, target.Port, null, null);
                if (!pendingConnect.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    failure = "방송 PC(" + target.Address + ")에 연결할 수 없어요. 두 PC가 같은 공유기에 있는지, 방송 PC에서 받기가 켜져 있는지 확인하세요.";
                    return Outcome.Failed;
                }
                tcp.EndConnect(pendingConnect);
                tcp.NoDelay = true;
                NetworkStream stream = tcp.GetStream();
                stream.ReadTimeout = 10000;
                stream.WriteTimeout = 5000;

                using (ECDiffieHellmanCng key = RelayCrypto.NewKey())
                {
                    byte[] clientHello = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                    {
                        ["kbmo"] = 1,
                        ["id"] = settings.Id,
                        ["name"] = NetInfo.MachineName,
                        ["pub"] = Convert.ToBase64String(RelayCrypto.PublicBlob(key))
                    }));
                    Frames.Write(stream, clientHello);

                    byte[] serverHello = Frames.Read(stream, HandshakeLimit);
                    Dictionary<string, object> hello = Json.Parse(Encoding.UTF8.GetString(serverHello));
                    string serverId = Json.Str(hello, "id");
                    string serverName = NetInfo.CleanName(Json.Str(hello, "name"));
                    byte[] serverKey = Convert.FromBase64String(Json.Str(hello, "pub") ?? "");
                    if (Json.Long(hello, "kbmo") != 1 || !Settings.IsId(serverId) || serverId == settings.Id || !RelayCrypto.IsPublicBlob(serverKey))
                    {
                        failure = target.Address + "는 KbmOverlay 방송 PC가 아니에요.";
                        return Outcome.Failed;
                    }
                    if (target.Id != null && serverId != target.Id)
                    {
                        failure = target.Address + "에 다른 PC가 있어요. 방송 PC를 다시 찾는 중이에요.";
                        return Outcome.Failed;
                    }
                    target.Name = serverName;

                    SessionKeys keys = RelayCrypto.Derive(key, serverKey, clientHello, serverHello, true);
                    var channel = new SecureChannel(stream, keys);

                    byte[] secret;
                    lock (settings) secret = settings.Receiver != null && settings.Receiver.Id == serverId ? settings.Receiver.Secret : null;
                    bool known = Json.Bool(hello, "known") ?? false;

                    if (secret != null && known)
                    {
                        channel.Send(Json.Write(new Dictionary<string, object>
                        {
                            ["type"] = "auth",
                            ["proof"] = Convert.ToBase64String(RelayCrypto.Proof(secret, "client", keys.Transcript))
                        }));
                        Dictionary<string, object> reply = Json.Parse(channel.Receive(HandshakeLimit));
                        switch (Json.Str(reply, "type"))
                        {
                            case "ok":
                                byte[] proof = Convert.FromBase64String(Json.Str(reply, "proof") ?? "");
                                if (!RelayCrypto.SameBytes(proof, RelayCrypto.Proof(secret, "server", keys.Transcript)))
                                {
                                    failure = "방송 PC 확인에 실패했어요.";
                                    return Outcome.Failed;
                                }
                                Remember(serverId, serverName, target, secret);
                                return Send(tcp, stream, channel, serverName);
                            case "busy":
                                failure = "방송 PC에 다른 게임 PC가 연결돼 있어요.";
                                return Outcome.Failed;
                            default:
                                return Outcome.Forgotten;
                        }
                    }

                    if (!mayPair) return Outcome.Forgotten;

                    channel.Send("{\"type\":\"pair\"}");
                    lock (gate) message = null;
                    SetState("waiting", serverName, keys.Code);
                    Log.Info("");
                    Log.Accent("  방송 PC '" + serverName + "' 화면에서 '허용'을 눌러 주세요.");
                    Log.Accent("  확인 숫자: " + keys.Code + "  (방송 PC에 나온 숫자와 같아야 해요)");
                    stream.ReadTimeout = 75000;
                    Dictionary<string, object> answer = Json.Parse(channel.Receive(HandshakeLimit));
                    switch (Json.Str(answer, "type"))
                    {
                        case "ok":
                            Remember(serverId, serverName, target, keys.PairSecret);
                            return Send(tcp, stream, channel, serverName);
                        case "busy":
                            failure = "방송 PC가 다른 연결을 처리하고 있어요.";
                            return Outcome.Failed;
                        default:
                            return Outcome.Denied;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException ||
                                      e is InvalidDataException || e is CryptographicException || e is ArgumentException ||
                                      e is FormatException)
            {
                if (state == "waiting") return Outcome.Denied; // no answer in time, or the request was cancelled
                failure = failure ?? "방송 PC(" + target.Address + ")와 연결하지 못했어요.";
                return state == "sending" ? Outcome.Ended : Outcome.Failed;
            }
            finally
            {
                tcp.Close();
                lock (gate) if (current == tcp) current = null;
            }
        }

        // The streaming PC refused or forgot this PC, so the stored secret is useless. Pairing again
        // needs the user to pick it again; never re-pair on our own.
        private void DropPairing(string serverId)
        {
            if (serverId == null) return;
            lock (settings)
            {
                if (settings.Receiver == null || settings.Receiver.Id != serverId) return;
                settings.Receiver = null;
                settings.Save();
            }
        }

        private void Remember(string serverId, string serverName, Target target, byte[] secret)
        {
            lock (settings)
            {
                settings.Receiver = new Peer { Id = serverId, Name = serverName, Address = target.Address, Port = target.Port, Secret = secret };
                settings.Save();
            }
            lock (gate)
            {
                forgottenId = null;
                if (consent != null && (consent.Id == null || consent.Id == serverId)) consent = null;
            }
        }

        // Streams events until the connection ends.
        private Outcome Send(TcpClient tcp, NetworkStream stream, SecureChannel channel, string serverName)
        {
            var queue = new BlockingCollection<string>(10000);
            Action<string> sink = json => queue.TryAdd(json);
            long lastPong = clock.ElapsedMilliseconds;
            int dead = 0;

            string snapshot = hub.AttachSink(sink);
            lock (gate)
            {
                message = null;
                lastLogged = null;
            }
            SetState("sending", serverName, null);
            Log.Good("  방송 PC '" + serverName + "'로 입력을 보내는 중이에요.");

            var reader = new Thread(() =>
            {
                try
                {
                    stream.ReadTimeout = Timeout.Infinite;
                    while (true)
                    {
                        Dictionary<string, object> m = Json.Parse(channel.Receive(4096));
                        if (Json.Str(m, "type") == "pong") Interlocked.Exchange(ref lastPong, clock.ElapsedMilliseconds);
                    }
                }
                catch (Exception)
                {
                    Volatile.Write(ref dead, 1);
                }
            });
            reader.IsBackground = true;
            reader.Name = "RelayOutRead";
            reader.Start();

            try
            {
                channel.Send(snapshot);
                long lastPing = clock.ElapsedMilliseconds;
                while (Volatile.Read(ref dead) == 0)
                {
                    if (queue.TryTake(out string json, 500)) channel.Send(json);
                    long now = clock.ElapsedMilliseconds;
                    if (now - lastPing >= PingMs)
                    {
                        channel.Send("{\"type\":\"ping\"}");
                        lastPing = now;
                    }
                    if (now - Interlocked.Read(ref lastPong) > IdleTimeoutMs) break;
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
            {
            }
            finally
            {
                hub.DetachSink(sink);
                tcp.Close();
            }
            return Outcome.Ended;
        }

        private void SetState(string newState, string name, string newCode)
        {
            bool differs;
            lock (gate)
            {
                differs = state != newState || targetName != name || code != newCode;
                state = newState;
                targetName = name;
                code = newCode;
            }
            if (differs) changed();
        }

        private static void Spawn(ThreadStart body, string name)
        {
            var thread = new Thread(body);
            thread.IsBackground = true;
            thread.Name = name;
            thread.Start();
        }
    }
}
