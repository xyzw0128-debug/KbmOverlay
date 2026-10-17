using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace KbmOverlay.Relay
{
    internal sealed class FoundReceiver
    {
        public string Id;
        public string Name;
        public string Address;
        public int Port;
        public long SeenAt;
    }

    // Finding the streaming PC without typing an IP.
    // The game PC broadcasts "KBMO1? <its id>" to UDP port 5521 on every LAN interface; a PC with
    // receiving turned on answers with its id, name and TCP port. The game PC only sends, and
    // Windows Firewall lets the unicast answer to a broadcast back in, so it needs no firewall rule.
    internal static class Discovery
    {
        private const string Request = "KBMO1? ";

        // Streaming PC side. Returns null if the port could not be opened.
        public static UdpClient Respond(IPAddress bindAddress, int port, string ownId, Func<string> answer)
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.ExclusiveAddressUse = true;
            udp.Client.Bind(new IPEndPoint(bindAddress, port));

            var thread = new Thread(() =>
            {
                while (true)
                {
                    IPEndPoint from = null;
                    byte[] data;
                    try
                    {
                        data = udp.Receive(ref from);
                    }
                    catch (SocketException)
                    {
                        continue; // e.g. ICMP port unreachable from an earlier answer
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    if (data.Length > 200 || !NetInfo.IsLan(from.Address)) continue;
                    string text = Encoding.ASCII.GetString(data);
                    if (!text.StartsWith(Request, StringComparison.Ordinal) || text.Substring(Request.Length) == ownId) continue;
                    byte[] reply = Encoding.UTF8.GetBytes(answer());
                    try
                    {
                        udp.Send(reply, reply.Length, from);
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "DiscoveryRespond";
            thread.Start();
            return udp;
        }

        // Game PC side. Asks every LAN (plus extra targets) and collects answers for waitMs.
        public static List<FoundReceiver> Search(string ownId, int port, IEnumerable<IPEndPoint> extraTargets, bool broadcast, int waitMs)
        {
            var found = new List<FoundReceiver>();
            var targets = new List<IPEndPoint>();
            if (broadcast)
            {
                foreach (IPAddress address in NetInfo.BroadcastAddresses()) targets.Add(new IPEndPoint(address, port));
                targets.Add(new IPEndPoint(IPAddress.Broadcast, port));
            }
            if (extraTargets != null) targets.AddRange(extraTargets);
            if (targets.Count == 0) return found;

            byte[] request = Encoding.ASCII.GetBytes(Request + ownId);
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                udp.EnableBroadcast = true;
                foreach (IPEndPoint target in targets)
                {
                    try
                    {
                        udp.Send(request, request.Length, target);
                    }
                    catch (SocketException)
                    {
                        // Interface went away or the target is unreachable.
                    }
                }

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < waitMs)
                {
                    int remainingMicros = (int)(waitMs - clock.ElapsedMilliseconds) * 1000;
                    if (remainingMicros <= 0 || !udp.Client.Poll(remainingMicros, SelectMode.SelectRead)) break;
                    IPEndPoint from = null;
                    byte[] data;
                    try
                    {
                        data = udp.Receive(ref from);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }
                    if (data.Length > 1000 || !NetInfo.IsLan(from.Address)) continue;

                    Dictionary<string, object> answer = Json.Parse(Encoding.UTF8.GetString(data));
                    string id = Json.Str(answer, "id");
                    long tcpPort = Json.Long(answer, "port") ?? 0;
                    if (Json.Long(answer, "kbmo") != 1 || !Settings.IsId(id) || id == ownId || tcpPort <= 0 || tcpPort > 65535) continue;
                    if (found.Exists(f => f.Id == id)) continue;
                    found.Add(new FoundReceiver
                    {
                        Id = id,
                        Name = NetInfo.CleanName(Json.Str(answer, "name")),
                        Address = NetInfo.Show(from.Address),
                        Port = (int)tcpPort
                    });
                }
            }
            return found;
        }
    }
}
