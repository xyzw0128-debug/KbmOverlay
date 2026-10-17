using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace KbmOverlay
{
    [Flags]
    internal enum Category
    {
        None = 0,
        Key = 1,
        Button = 2,
        Wheel = 4,
        Move = 8,
        All = Key | Button | Wheel | Move,
        Relay = 16 // two-PC status, for the settings page only
    }

    // Keeps the pressed state, batches movement and sends events to connected pages.
    // Keys and buttons go out right away. Mouse movement and wheel deltas are summed and sent
    // at most every MotionIntervalMs, because OBS only draws ~60 frames a second anyway.
    //
    // Two PCs: on the game PC, a sink forwards the same events to the streaming PC. On the
    // streaming PC, while a game PC is connected its events replace this PC's own input.
    internal sealed class InputHub
    {
        private static readonly System.Text.RegularExpressions.Regex NamePattern =
            new System.Text.RegularExpressions.Regex("^[A-Za-z0-9]{1,32}$");

        private const int MotionIntervalMs = 8;
        private const int StuckCheckMs = 500;
        private const int StuckGraceMs = 300;
        private const int TapHoldMs = 150;

        private sealed class Held
        {
            public int Vk;
            public long Since;
            public int Misses;
        }

        private struct Outgoing
        {
            public Category Category;
            public string Json;
        }

        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Dictionary<string, Held> keys = new Dictionary<string, Held>();
        private readonly Dictionary<string, Held> buttons = new Dictionary<string, Held>();
        private readonly Dictionary<string, long> tapsUntil = new Dictionary<string, long>();
        private readonly List<WsClient> clients = new List<WsClient>();
        private readonly List<Action<string>> sinks = new List<Action<string>>();
        private List<Outgoing> queue = new List<Outgoing>();

        private int moveX, moveY, wheelX, wheelY;
        private bool motionSignaled;
        private long lastMotionFlush = -MotionIntervalMs;
        private long nextStuckCheck;
        private string remote; // session id of the game PC being shown, or null for this PC's input

        public event Action<int> ClientCountChanged;

        // Current two-PC status (JSON) for settings pages that just connected.
        public Func<string> RelayStatus;

        public void Start()
        {
            var thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "Sender";
            thread.Start();
        }

        // ---- called from the Raw Input thread ----

        public void Key(string name, int vk, bool down)
        {
            lock (gate)
            {
                if (remote != null || tapsUntil.ContainsKey(name)) return;
                if (!SetHeld(keys, name, vk, down)) return; // key repeat or duplicate release
                Enqueue(Category.Key, KeyJson("key", name, down));
            }
            wake.Set();
        }

        public void KeyTap(string name)
        {
            lock (gate)
            {
                if (remote != null) return;
                long now = clock.ElapsedMilliseconds;
                if (!tapsUntil.ContainsKey(name))
                {
                    SetHeld(keys, name, 0, true);
                    Enqueue(Category.Key, KeyJson("key", name, true));
                }
                tapsUntil[name] = now + TapHoldMs;
            }
            wake.Set();
        }

        public void Button(string name, int vk, bool down)
        {
            lock (gate)
            {
                if (remote != null || !SetHeld(buttons, name, vk, down)) return;
                Enqueue(Category.Button, KeyJson("button", name, down));
            }
            wake.Set();
        }

        public void Move(int dx, int dy)
        {
            bool signal;
            lock (gate)
            {
                if (remote != null) return;
                moveX += dx;
                moveY += dy;
                signal = !motionSignaled;
                motionSignaled = true;
            }
            if (signal) wake.Set();
        }

        public void Wheel(int dx, int dy)
        {
            bool signal;
            lock (gate)
            {
                if (remote != null) return;
                wheelX += dx;
                wheelY += dy;
                signal = !motionSignaled;
                motionSignaled = true;
            }
            if (signal) wake.Set();
        }

        // ---- input from a game PC (streaming PC side) ----

        // From now on pages show this session's input instead of this PC's.
        public void BeginRemote(string session)
        {
            lock (gate)
            {
                ReleaseAll();
                remote = session;
            }
            wake.Set();
        }

        public void EndRemote(string session)
        {
            lock (gate)
            {
                if (remote != session) return;
                ReleaseAll();
                remote = null;
            }
            wake.Set();
        }

        // One event from the game PC. Already de-duplicated, tap-handled and stuck-checked there;
        // checked again here because it came over the network.
        public void ApplyRemote(string session, Dictionary<string, object> message)
        {
            lock (gate)
            {
                if (remote != session) return;
                switch (Json.Str(message, "type"))
                {
                    case "key":
                    case "button":
                        bool isKey = Json.Str(message, "type") == "key";
                        string name = Json.Str(message, "name");
                        bool? down = Json.Bool(message, "down");
                        if (name == null || !NamePattern.IsMatch(name) || down == null) return;
                        if (SetHeld(isKey ? keys : buttons, name, 0, down.Value))
                            Enqueue(isKey ? Category.Key : Category.Button, KeyJson(isKey ? "key" : "button", name, down.Value));
                        break;
                    case "move":
                        moveX += Clamp(Json.Long(message, "dx"));
                        moveY += Clamp(Json.Long(message, "dy"));
                        break;
                    case "wheel":
                        wheelX += Clamp(Json.Long(message, "dx"));
                        wheelY += Clamp(Json.Long(message, "dy"));
                        break;
                    case "state":
                        Sync(keys, Category.Key, "key", Json.Array(message, "keys"));
                        Sync(buttons, Category.Button, "button", Json.Array(message, "buttons"));
                        break;
                }
            }
            wake.Set();
        }

        // ---- forwarding to the streaming PC (game PC side) ----

        // Returns the current pressed state; every later event goes to the sink.
        public string AttachSink(Action<string> sink)
        {
            lock (gate)
            {
                sinks.Add(sink);
                return StateJson();
            }
        }

        public void DetachSink(Action<string> sink)
        {
            lock (gate) sinks.Remove(sink);
        }

        // ---- clients ----

        public void AddClient(WsClient client)
        {
            int count;
            // Holding the client's send lock keeps any event from reaching it before the snapshot.
            lock (client.SendLock)
            {
                string snapshot;
                lock (gate)
                {
                    clients.Add(client);
                    count = clients.Count;
                    snapshot = StateJson();
                }
                client.TrySend(WsClient.TextFrame(snapshot));
                if ((client.Want & Category.Relay) != 0 && RelayStatus != null)
                    client.TrySend(WsClient.TextFrame(RelayStatus()));
            }
            ClientCountChanged?.Invoke(count);
        }

        // Pushes two-PC status to settings pages.
        public void BroadcastStatus(string json)
        {
            WsClient[] targets;
            lock (gate) targets = clients.ToArray();
            byte[] frame = null;
            foreach (WsClient client in targets)
            {
                if ((client.Want & Category.Relay) == 0) continue;
                frame ??= WsClient.TextFrame(json);
                if (!client.TrySend(frame)) RemoveClient(client);
            }
        }

        public void RemoveClient(WsClient client)
        {
            int count;
            lock (gate)
            {
                if (!clients.Remove(client)) return;
                count = clients.Count;
            }
            client.Close();
            ClientCountChanged?.Invoke(count);
        }

        public int ClientCount
        {
            get { lock (gate) return clients.Count; }
        }

        // ---- sender thread ----

        private void Run()
        {
            while (true)
            {
                try
                {
                    RunOnce();
                }
                catch (Exception e)
                {
                    // An exception escaping this thread would end the whole program.
                    Log.Error(I18n.T("error.send", ("error", e.Message)));
                    Thread.Sleep(100);
                }
            }
        }

        private void RunOnce()
        {
            List<Outgoing> batch = null;
            WsClient[] targets = null;
            Action<string>[] sinkTargets = null;
            int wait;
            lock (gate)
            {
                long now = clock.ElapsedMilliseconds;
                ReleaseTaps(now);
                if (HasMotion && now - lastMotionFlush >= MotionIntervalMs)
                    FlushMotion(now);
                if ((keys.Count > 0 || buttons.Count > 0) && now >= nextStuckCheck)
                {
                    ReleaseStuck(now);
                    nextStuckCheck = now + StuckCheckMs;
                }
                if (queue.Count > 0)
                {
                    batch = queue;
                    queue = new List<Outgoing>();
                    targets = clients.ToArray();
                    sinkTargets = sinks.ToArray();
                }
                wait = NextWait(now);
            }
            if (batch != null) Broadcast(batch, targets, sinkTargets);
            wake.WaitOne(wait);
        }

        private void Broadcast(List<Outgoing> batch, WsClient[] targets, Action<string>[] sinkTargets)
        {
            foreach (Outgoing message in batch)
            {
                foreach (Action<string> sink in sinkTargets) sink(message.Json);
                byte[] frame = null;
                foreach (WsClient client in targets)
                {
                    if ((client.Want & message.Category) == 0) continue;
                    frame ??= WsClient.TextFrame(message.Json);
                    if (!client.TrySend(frame)) RemoveClient(client);
                }
            }
        }

        private int NextWait(long now)
        {
            long wait = 1000;
            if (HasMotion) wait = Math.Min(wait, lastMotionFlush + MotionIntervalMs - now);
            foreach (long until in tapsUntil.Values) wait = Math.Min(wait, until - now);
            if (keys.Count > 0 || buttons.Count > 0) wait = Math.Min(wait, nextStuckCheck - now);
            return (int)Math.Max(0, wait);
        }

        // ---- state helpers (call with gate held) ----

        private bool HasMotion => moveX != 0 || moveY != 0 || wheelX != 0 || wheelY != 0;

        private void Enqueue(Category category, string json)
        {
            // Send pending movement first so the order of events is kept.
            if (HasMotion) FlushMotion(clock.ElapsedMilliseconds);
            queue.Add(new Outgoing { Category = category, Json = json });
        }

        private void FlushMotion(long now)
        {
            if (moveX != 0 || moveY != 0)
                queue.Add(new Outgoing { Category = Category.Move, Json = "{\"type\":\"move\",\"dx\":" + moveX + ",\"dy\":" + moveY + "}" });
            if (wheelX != 0 || wheelY != 0)
                queue.Add(new Outgoing { Category = Category.Wheel, Json = "{\"type\":\"wheel\",\"dx\":" + wheelX + ",\"dy\":" + wheelY + "}" });
            moveX = moveY = wheelX = wheelY = 0;
            motionSignaled = false;
            lastMotionFlush = now;
        }

        private bool SetHeld(Dictionary<string, Held> set, string name, int vk, bool down)
        {
            if (down)
            {
                if (set.ContainsKey(name)) return false;
                long now = clock.ElapsedMilliseconds;
                if (keys.Count + buttons.Count == 0) nextStuckCheck = now + StuckCheckMs;
                set[name] = new Held { Vk = vk, Since = now };
                return true;
            }
            return set.Remove(name);
        }

        private void ReleaseTaps(long now)
        {
            if (tapsUntil.Count == 0) return;
            List<string> done = null;
            foreach (KeyValuePair<string, long> tap in tapsUntil)
                if (tap.Value <= now) (done ??= new List<string>()).Add(tap.Key);
            if (done == null) return;
            foreach (string name in done)
            {
                tapsUntil.Remove(name);
                if (keys.Remove(name)) Enqueue(Category.Key, KeyJson("key", name, false));
            }
        }

        // A release can be missed (e.g. while UAC or an elevated window has focus).
        // If Windows says a held key is up on two checks in a row, release it here.
        private void ReleaseStuck(long now)
        {
            ReleaseStuck(keys, "key", Category.Key, now);
            ReleaseStuck(buttons, "button", Category.Button, now);
        }

        private void ReleaseStuck(Dictionary<string, Held> set, string type, Category category, long now)
        {
            List<string> stuck = null;
            foreach (KeyValuePair<string, Held> entry in set)
            {
                Held held = entry.Value;
                if (held.Vk == 0 || now - held.Since < StuckGraceMs) continue;
                bool downNow = (Native.GetAsyncKeyState(held.Vk) & 0x8000) != 0;
                held.Misses = downNow ? 0 : held.Misses + 1;
                if (held.Misses >= 2) (stuck ??= new List<string>()).Add(entry.Key);
            }
            if (stuck == null) return;
            foreach (string name in stuck)
            {
                set.Remove(name);
                Enqueue(category, KeyJson(type, name, false));
            }
        }

        private void ReleaseAll()
        {
            foreach (string name in new List<string>(keys.Keys)) Enqueue(Category.Key, KeyJson("key", name, false));
            foreach (string name in new List<string>(buttons.Keys)) Enqueue(Category.Button, KeyJson("button", name, false));
            keys.Clear();
            buttons.Clear();
            tapsUntil.Clear();
            moveX = moveY = wheelX = wheelY = 0;
        }

        private void Sync(Dictionary<string, Held> set, Category category, string type, object[] names)
        {
            var next = new HashSet<string>();
            foreach (object item in names ?? new object[0])
                if (item is string name && NamePattern.IsMatch(name)) next.Add(name);
            foreach (string name in new List<string>(set.Keys))
                if (!next.Contains(name) && set.Remove(name)) Enqueue(category, KeyJson(type, name, false));
            foreach (string name in next)
                if (SetHeld(set, name, 0, true)) Enqueue(category, KeyJson(type, name, true));
        }

        private static int Clamp(long? value)
        {
            return (int)Math.Max(-100000, Math.Min(100000, value ?? 0));
        }

        private static string KeyJson(string type, string name, bool down)
        {
            return "{\"type\":\"" + type + "\",\"name\":\"" + name + "\",\"down\":" + (down ? "true" : "false") + "}";
        }

        private string StateJson()
        {
            var sb = new StringBuilder("{\"type\":\"state\",\"keys\":[");
            AppendNames(sb, keys.Keys);
            sb.Append("],\"buttons\":[");
            AppendNames(sb, buttons.Keys);
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendNames(StringBuilder sb, IEnumerable<string> names)
        {
            bool first = true;
            foreach (string name in names)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(name).Append('"');
                first = false;
            }
        }
    }
}
