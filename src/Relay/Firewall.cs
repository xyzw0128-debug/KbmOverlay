using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace KbmOverlay.Relay
{
    // Windows Firewall rules for the streaming PC, through the HNetCfg COM API.
    // Reading needs no admin rights; changes run in a copy of this exe started with UAC
    // ("--firewall-setup <port>"), so the program itself never runs elevated for this.
    //
    // The rules allow only this exe, only the relay port (TCP and UDP), only from the local subnet,
    // and only on Private/Domain networks, plus Public ones if the user chose "Also receive on Public networks".
    // Adding them before the first listen also keeps Windows from showing its own firewall popup,
    // which creates Block rules when it is cancelled.
    internal static class Firewall
    {
        public const string RuleTcp = "KbmOverlay two-PC relay (TCP)";
        public const string RuleUdp = "KbmOverlay two-PC relay (UDP)";
        // Names used by versions 0.2 and 0.3; still recognized and replaced.
        private static readonly string[] OldRuleNames = { "KbmOverlay 투컴 받기 (TCP)", "KbmOverlay 투컴 받기 (UDP)" };

        public const int ProfileDomain = 1;
        public const int ProfilePrivate = 2;
        public const int ProfilePublic = 4;
        private const int DirectionIn = 1;
        private const int ActionBlock = 0;
        private const int ActionAllow = 1;
        private const int ProtocolTcp = 6;
        private const int ProtocolUdp = 17;
        private const int ProtocolAny = 256;

        // Network profiles (bit masks) for which inbound rules on this exe allow or block the port.
        public sealed class Status
        {
            public int TcpProfiles;
            public int UdpProfiles;
            public int BlockedProfiles;
            public int OwnRuleProfiles; // profiles of the two rules this program made

            public bool Covers(int profiles) =>
                (TcpProfiles & profiles) == profiles && (UdpProfiles & profiles) == profiles && (BlockedProfiles & profiles) == 0;
        }

        // True when every connected network is "Public".
        public static bool OnlyPublicNetwork()
        {
            dynamic policy = Policy();
            int profiles = policy.CurrentProfileTypes;
            return (profiles & (ProfileDomain | ProfilePrivate)) == 0;
        }

        public static Status Check(string exePath, int port)
        {
            var status = new Status();
            dynamic policy = Policy();
            foreach (dynamic rule in policy.Rules)
            {
                string application = rule.ApplicationName;
                if (application == null || !SamePath(application, exePath)) continue;
                if ((int)rule.Direction != DirectionIn || !(bool)rule.Enabled) continue;
                int profiles = rule.Profiles;
                string name = rule.Name;
                if (IsOwnRule(name)) status.OwnRuleProfiles |= profiles;

                if ((int)rule.Action == ActionBlock)
                {
                    status.BlockedProfiles |= profiles;
                    continue;
                }
                if ((int)rule.Action != ActionAllow || !PortMatches((string)rule.LocalPorts, port)) continue;
                int protocol = rule.Protocol;
                if (protocol == ProtocolTcp || protocol == ProtocolAny) status.TcpProfiles |= profiles;
                if (protocol == ProtocolUdp || protocol == ProtocolAny) status.UdpProfiles |= profiles;
            }
            return status;
        }

        // Runs in the elevated copy. Returns the process exit code.
        public static int Setup(string exePath, int port, bool includePublic)
        {
            int profiles = ProfileDomain | ProfilePrivate | (includePublic ? ProfilePublic : 0);
            try
            {
                dynamic policy = Policy();
                dynamic rules = policy.Rules;

                // Block rules for this exe (left by a cancelled Windows popup) would win over Allow rules.
                foreach (dynamic rule in rules)
                {
                    string application = rule.ApplicationName;
                    if (application != null && SamePath(application, exePath) &&
                        (int)rule.Direction == DirectionIn && (int)rule.Action == ActionBlock &&
                        ((int)rule.Profiles & profiles) != 0)
                        rule.Enabled = false;
                }

                RemoveAll(rules, RuleTcp);
                RemoveAll(rules, RuleUdp);
                foreach (string old in OldRuleNames) RemoveAll(rules, old);
                rules.Add(NewRule(RuleTcp, exePath, ProtocolTcp, port, profiles));
                rules.Add(NewRule(RuleUdp, exePath, ProtocolUdp, port, profiles));
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        // Starts the elevated copy and waits. Returns null on success, or a message for the user.
        public static string RunElevatedSetup(string exePath, int port, bool includePublic)
        {
            var info = new ProcessStartInfo(exePath, "--firewall-setup " + port + (includePublic ? " public" : ""))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (Process process = Process.Start(info))
                {
                    if (process == null) return I18n.T("firewall.failed");
                    if (!process.WaitForExit(60000)) return I18n.T("firewall.timeout");
                    return process.ExitCode == 0 ? null : I18n.T("firewall.failed");
                }
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return I18n.T("firewall.uacCancelled");
            }
            catch (Win32Exception e)
            {
                // For example the exe was moved or renamed while running, or a policy blocks elevation.
                return I18n.T("firewall.startFailed", ("error", e.Message));
            }
        }

        private static bool IsOwnRule(string name) => name == RuleTcp || name == RuleUdp || Array.IndexOf(OldRuleNames, name) >= 0;

        private static dynamic Policy()
        {
            return Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true));
        }

        private static dynamic NewRule(string name, string exePath, int protocol, int port, int profiles)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
            rule.Name = name;
            rule.Description = "KbmOverlay: receives keyboard and mouse input from a game PC on the same network.";
            rule.ApplicationName = exePath;
            rule.Protocol = protocol;
            rule.LocalPorts = port.ToString();
            rule.RemoteAddresses = "LocalSubnet";
            rule.Direction = DirectionIn;
            rule.Action = ActionAllow;
            rule.Profiles = profiles;
            rule.Enabled = true;
            return rule;
        }

        private static void RemoveAll(dynamic rules, string name)
        {
            for (int i = 0; i < 10; i++)
            {
                bool found = false;
                foreach (dynamic rule in rules)
                    if ((string)rule.Name == name) { found = true; break; }
                if (!found) return;
                rules.Remove(name);
            }
        }

        private static bool SamePath(string ruleApplication, string exePath)
        {
            try
            {
                string a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ruleApplication));
                string b = Path.GetFullPath(exePath);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return false;
            }
        }

        private static bool PortMatches(string ports, int port)
        {
            if (string.IsNullOrEmpty(ports) || ports == "*") return true;
            foreach (string part in ports.Split(','))
            {
                string p = part.Trim();
                int dash = p.IndexOf('-');
                if (dash > 0 && int.TryParse(p.Substring(0, dash), out int low) && int.TryParse(p.Substring(dash + 1), out int high))
                {
                    if (port >= low && port <= high) return true;
                }
                else if (int.TryParse(p, out int single) && single == port)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
