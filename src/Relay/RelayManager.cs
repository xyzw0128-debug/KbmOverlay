using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

namespace KbmOverlay.Relay
{
    // Two-PC mode. Every KbmOverlay can be both:
    //   - a streaming PC (receiver), once "Receive from game PCs" is turned on, and
    //   - a game PC (sender), once the user picks a streaming PC it found.
    // Commands come from the settings page (WebSocket text) and console keys.
    internal sealed class RelayManager
    {
        public const int DefaultPort = 5521;

        private readonly Settings settings;
        private readonly InputHub hub;
        private readonly RelayReceiver receiver;
        private readonly RelaySender sender;

        public RelayManager(Settings settings, InputHub hub, string exePath, int port, bool dev, bool devPublic, List<IPEndPoint> devProbes)
        {
            this.settings = settings;
            this.hub = hub;
            // Dev instances (several on one PC for testing) stay on loopback and skip the firewall;
            // devPublic pretends the network is "Public" to test that path.
            Func<bool> onlyPublic = dev ? () => devPublic : (Func<bool>)Firewall.OnlyPublicNetwork;
            receiver = new RelayReceiver(settings, hub, port, dev ? IPAddress.Loopback : IPAddress.Any, exePath, !dev, onlyPublic, Changed);
            sender = new RelaySender(settings, hub, DefaultPort, !dev, devProbes, Changed);
            hub.RelayStatus = StatusJson;
        }

        public void Start()
        {
            bool receive;
            lock (settings) receive = settings.Receive;
            if (receive) receiver.SetEnabled(true, false);
            sender.Start();
        }

        // Lines for the start-up banner.
        public IEnumerable<string> Describe()
        {
            Peer target;
            bool receive;
            int paired;
            lock (settings)
            {
                target = settings.Receiver;
                receive = settings.Receive;
                paired = settings.Senders.Count;
            }
            yield return I18n.T("relay.banner", ("machine", NetInfo.MachineName));
            yield return !receive ? I18n.T("relay.receiveOff")
                : paired > 0 ? I18n.T("relay.receiveOnPaired", ("count", paired))
                : I18n.T("relay.receiveOn");
            yield return target != null
                ? I18n.T("relay.sendOn", ("name", target.Name))
                : I18n.T("relay.sendOff");
        }

        public string StatusJson()
        {
            var addresses = new List<object>();
            foreach (IPAddress address in NetInfo.LocalAddresses()) addresses.Add(address.ToString());
            return Json.Write(new Dictionary<string, object>
            {
                ["type"] = "relay",
                ["machine"] = NetInfo.MachineName,
                ["addresses"] = addresses,
                ["receive"] = receiver.Status(),
                ["send"] = sender.Status()
            });
        }

        private void Changed()
        {
            hub.BroadcastStatus(StatusJson());
        }

        // From the settings page. Only pages served by this program can send these (WebSocket Origin check).
        public void Command(string text)
        {
            Dictionary<string, object> command = Json.Parse(text);
            try
            {
                switch (Json.Str(command, "cmd"))
                {
                    case "receive":
                        receiver.SetEnabled(Json.Bool(command, "on") ?? false, true);
                        break;
                    case "allowPublic":
                        receiver.SetAllowPublic(Json.Bool(command, "on") ?? false);
                        break;
                    case "approve":
                        receiver.Decide(Json.Str(command, "id"), true);
                        break;
                    case "deny":
                        receiver.Decide(Json.Str(command, "id"), false);
                        break;
                    case "forget":
                        string id = Json.Str(command, "id");
                        if (Settings.IsId(id)) receiver.Forget(id);
                        break;
                    case "send":
                        if (!sender.ChooseFound(Json.Str(command, "id") ?? "")) sender.SearchNow();
                        break;
                    case "sendTo":
                        string error = sender.ChooseAddress(Json.Str(command, "address"));
                        if (error != null) hub.BroadcastStatus(Json.Write(new Dictionary<string, object> { ["type"] = "relayError", ["message"] = error }));
                        break;
                    case "stopSending":
                        sender.StopSending();
                        break;
                    case "search":
                        sender.SearchNow();
                        break;
                    case "networkSettings":
                        Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error(I18n.T("error.command", ("error", e.Message)));
            }
        }

        // Console keys: Y/N answer a pending request; S and 1-9 pick a streaming PC.
        public bool HandleKey(ConsoleKeyInfo key)
        {
            // ConsoleKey is the physical key, so this also works with the Korean IME on.
            if (receiver.CurrentPending != null)
            {
                if (key.Key == ConsoleKey.Y) { receiver.Decide(null, true); return true; }
                if (key.Key == ConsoleKey.N) { receiver.Decide(null, false); return true; }
            }
            return sender.HandleKey(key);
        }
    }
}
