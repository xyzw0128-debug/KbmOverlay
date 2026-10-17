using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace KbmOverlay.Relay
{
    internal static class NetInfo
    {
        public static string MachineName => CleanName(Environment.MachineName);

        // This PC's LAN IPv4 addresses, the ones with a default gateway first.
        public static List<IPAddress> LocalAddresses()
        {
            var withGateway = new List<IPAddress>();
            var others = new List<IPAddress>();
            foreach (LanAddress info in Unicast())
            {
                bool gateway = false;
                foreach (GatewayIPAddressInformation g in info.Nic.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gateway = true;
                (gateway ? withGateway : others).Add(info.Address);
            }
            withGateway.AddRange(others);
            return withGateway;
        }

        // Directed broadcast address of every LAN interface (e.g. 192.168.0.255).
        public static List<IPAddress> BroadcastAddresses()
        {
            var result = new List<IPAddress>();
            foreach (LanAddress info in Unicast())
            {
                if (info.Mask == null) continue;
                byte[] ip = info.Address.GetAddressBytes();
                byte[] mask = info.Mask.GetAddressBytes();
                if (mask.Length != 4 || (mask[0] | mask[1] | mask[2] | mask[3]) == 0) continue;
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                var address = new IPAddress(broadcast);
                if (!result.Contains(address)) result.Add(address);
            }
            return result;
        }

        // Private, link-local, CGNAT (Tailscale etc.) and loopback addresses. Others are never accepted.
        public static bool IsLan(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        public static string Show(IPAddress address)
        {
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }

        // Names from other PCs are shown in the console and the settings page; keep them plain.
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            name = Regex.Replace(name, @"[^\p{L}\p{N} ._\-]", "").Trim();
            if (name.Length > 32) name = name.Substring(0, 32);
            return name.Length == 0 ? "?" : name;
        }

        private sealed class LanAddress
        {
            public NetworkInterface Nic;
            public IPAddress Address;
            public IPAddress Mask;
        }

        private static IEnumerable<LanAddress> Unicast()
        {
            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                yield break;
            }
            foreach (NetworkInterface nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = address.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // no DHCP answer; not useful
                    yield return new LanAddress { Nic = nic, Address = address.Address, Mask = address.IPv4Mask };
                }
            }
        }
    }
}
