using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using KbmOverlay.Relay;
using static KbmOverlay.I18n;

namespace KbmOverlay
{
    internal static class Program
    {
        private const int DefaultPort = 5520;

        private static int Main(string[] args)
        {
            string exePath = Assembly.GetExecutingAssembly().Location;

            // Elevated helper started by the streaming PC side; see Relay/Firewall.cs.
            if ((args.Length == 2 || (args.Length == 3 && args[2] == "public")) &&
                args[0] == "--firewall-setup" && int.TryParse(args[1], out int firewallPort))
                return Firewall.Setup(exePath, firewallPort, args.Length == 3);

            try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
            Console.Title = "KbmOverlay";

            int? port = null;
            int dev = 0;
            bool devPublic = false;
            bool export = false;
            bool openBrowser = true;
            bool firewallCheck = false;
            bool badArgs = false;
            string lang = null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                if ((arg == "--port" || arg == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p > 0 && p < 65536)
                {
                    port = p;
                    i++;
                }
                else if (arg == "--lang" && i + 1 < args.Length)
                {
                    lang = args[++i];
                }
                else if (arg == "--export-pages")
                {
                    export = true;
                }
                else if (arg == "--no-browser")
                {
                    openBrowser = false; // e.g. when started with Windows; Enter still opens the settings page
                }
                else if (arg == "--firewall-check")
                {
                    firewallCheck = true; // read-only check for troubleshooting the streaming PC's firewall
                }
                else if (arg == "--dev" && i + 1 < args.Length && int.TryParse(args[i + 1], out int d) && d >= 1 && d <= 9)
                {
                    // Development: several instances on one PC, relay on loopback only, no firewall changes.
                    dev = d;
                    i++;
                }
                else if (arg == "--dev-public")
                {
                    devPublic = true; // with --dev: pretend this PC is on a "Public" network
                }
                else
                {
                    badArgs = true;
                }
            }

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var pages = new PageStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pages"));
            I18n.Init(pages, lang);

            if (badArgs)
            {
                Console.WriteLine(T("usage.line"));
                Console.WriteLine(T("usage.port", ("port", DefaultPort)));
                Console.WriteLine(T("usage.lang"));
                Console.WriteLine(T("usage.export"));
                Console.WriteLine(T("usage.noBrowser"));
                return 2;
            }

            if (firewallCheck)
            {
                PrintFirewallCheck(exePath);
                return 0;
            }

            if (export)
            {
                int written = pages.Export();
                Console.WriteLine(T("export.done", ("count", written), ("dir", pages.OverrideDir)));
                return 0;
            }

            string mutexName = dev == 0 ? @"Local\KbmOverlay" : @"Local\KbmOverlay-dev" + dev;
            using (var mutex = new Mutex(true, mutexName, out bool firstInstance))
            {
                if (!firstInstance)
                {
                    Console.WriteLine(T("alreadyRunning"));
                    WaitBeforeClose();
                    return 1;
                }
                return Run(port ?? DefaultPort + 10 * dev, dev, devPublic, lang, exePath, pages, version, openBrowser);
            }
        }

        private static int Run(int port, int dev, bool devPublic, string cliLang, string exePath, PageStore pages, string version, bool openBrowser)
        {
            bool elevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            string dataDir = dev == 0
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KbmOverlay")
                : Path.Combine(Path.GetTempPath(), "KbmOverlay-dev" + dev);
            Settings settings = Settings.Load(dataDir);
            // A language picked on the settings page applies unless --lang was given.
            if (cliLang == null && settings.Lang != null) I18n.Use(settings.Lang);
            // What the settings page's language menu shows: "auto" or a language code.
            string langChoice = cliLang != null && I18n.Has(cliLang) ? cliLang : settings.Lang ?? "auto";

            var hub = new InputHub();
            hub.ClientCountChanged += count => Log.Info(T("pages.count", ("count", count)));
            hub.Start();

            var devProbes = new List<IPEndPoint>();
            if (dev != 0)
                for (int other = 1; other <= 9; other++)
                    if (other != dev) devProbes.Add(new IPEndPoint(IPAddress.Loopback, RelayManager.DefaultPort + 10 * other));
            var relay = new RelayManager(settings, hub, exePath, RelayManager.DefaultPort + 10 * dev, dev != 0, devPublic, devProbes);

            Func<string> info = () =>
            {
                var languages = new List<object>();
                foreach (KeyValuePair<string, string> language in I18n.Available())
                    languages.Add(new Dictionary<string, object> { ["code"] = language.Key, ["name"] = language.Value });
                return Json.Write(new Dictionary<string, object>
                {
                    ["name"] = "KbmOverlay",
                    ["version"] = version,
                    ["port"] = port,
                    ["elevated"] = elevated,
                    ["clients"] = hub.ClientCount,
                    ["lang"] = I18n.Lang,
                    ["langSetting"] = langChoice,
                    ["systemLang"] = I18n.NameOf(I18n.SystemLang),
                    ["languages"] = languages
                });
            };

            Action<string> onCommand = text =>
            {
                Dictionary<string, object> command = Json.Parse(text);
                string cmd = Json.Str(command, "cmd");
                if (cmd == "exportPages")
                {
                    hub.BroadcastStatus(ExportPages(pages));
                    return;
                }
                if (cmd != "language")
                {
                    relay.Command(text);
                    return;
                }
                string picked = Json.Str(command, "lang");
                if (picked != "auto" && !I18n.Has(picked)) return;
                lock (settings)
                {
                    settings.Lang = picked == "auto" ? null : picked;
                    settings.Save();
                }
                langChoice = picked;
                I18n.Use(picked);
                Log.Info(T("language.changed", ("name", I18n.NameOf(I18n.Lang))));
                hub.BroadcastStatus(relay.StatusJson());
            };

            try
            {
                new WebServer(port, pages, hub, info, onCommand).Start();
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    Log.Error(T("port.inUse", ("port", port)));
                else if (e.SocketErrorCode == SocketError.AccessDenied)
                    Log.Error(T("port.reserved", ("port", port)));
                else
                    Log.Error(T("port.failed", ("port", port), ("error", e.Message)));
                Log.Flush();
                WaitBeforeClose();
                return 1;
            }

            try
            {
                new RawInputReader(hub).Start();
            }
            catch (Exception e)
            {
                Log.Error(T("input.failed", ("error", e.Message)));
                Log.Flush();
                WaitBeforeClose();
                return 1;
            }

            string root = "http://localhost:" + port + "/";
            Log.Accent(T("banner.title", ("version", version)) + (dev != 0 ? T("banner.dev", ("n", dev)) : ""));
            Log.Info("");
            Log.Good(T("banner.settings", ("url", root)));
            Log.Info(T("banner.settingsHint"));
            Log.Info("");
            Log.Info(T("banner.urls"));
            Url(root + "keyboard.html", T("overlay.keyboardWasd"));
            Url(root + "keyboard.html?layout=tkl", T("overlay.keyboardTkl"));
            Url(root + "keyboard.html?layout=full", T("overlay.keyboardFull"));
            Url(root + "mouse.html", T("overlay.mouse"));
            Url(root + "mouse-move.html", T("overlay.mouseMove"));
            Log.Info("");
            foreach (string line in relay.Describe()) Log.Info(line);
            Log.Info("");
            if (!elevated)
            {
                Log.Warn(T("banner.notAdmin1"));
                Log.Warn(T("banner.notAdmin2"));
                Log.Info("");
            }
            Log.Info(T("banner.close"));
            Log.Info("");

            relay.Start();

            // The settings page is the next step anyway, so it opens by itself (not with --no-browser).
            if (openBrowser) OpenBrowser(root);

            if (Console.IsInputRedirected)
            {
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                    OpenBrowser(root);
                else
                    relay.HandleKey(key);
            }
        }

        private static void PrintFirewallCheck(string exePath)
        {
            Firewall.Status status = Firewall.Check(exePath, RelayManager.DefaultPort);
            string YesNo(bool value) => value ? T("fwcheck.yes") : T("fwcheck.no");
            Console.WriteLine("exe: " + exePath);
            Console.WriteLine(T("fwcheck.title", ("port", RelayManager.DefaultPort)));
            foreach ((int Profile, string Key) row in new[] { (Firewall.ProfilePrivate, "fwcheck.private"), (Firewall.ProfileDomain, "fwcheck.domain"), (Firewall.ProfilePublic, "fwcheck.public") })
            {
                Console.WriteLine("  " + T(row.Key) + ": " +
                                  YesNo((status.TcpProfiles & row.Profile) != 0) + " / " +
                                  YesNo((status.UdpProfiles & row.Profile) != 0) + " / " +
                                  ((status.BlockedProfiles & row.Profile) != 0 ? T("fwcheck.yes") : T("fwcheck.none")));
            }
            Console.WriteLine(T("fwcheck.onlyPublic", ("value", YesNo(Firewall.OnlyPublicNetwork()))));
        }

        // "Open the pages folder" on the settings page: copies the built-in pages next to the exe
        // (keeping files already there), opens the folder and returns the result for the page.
        private static string ExportPages(PageStore pages)
        {
            var result = new Dictionary<string, object> { ["type"] = "exported", ["dir"] = pages.OverrideDir };
            try
            {
                int written = pages.Export();
                result["count"] = written;
                Log.Info(T("export.done", ("count", written), ("dir", pages.OverrideDir)));
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + pages.OverrideDir + "\"") { UseShellExecute = true });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception)
            {
                result["error"] = e.Message;
                Log.Warn(T("export.failed", ("error", e.Message), ("dir", pages.OverrideDir)));
            }
            return Json.Write(result);
        }

        private static void Url(string url, string label)
        {
            Log.Info("    " + url.PadRight(50) + label);
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Log.Warn(T("browser.failed", ("error", e.Message), ("url", url)));
            }
        }

        private static void WaitBeforeClose()
        {
            if (Console.IsInputRedirected) return;
            Console.WriteLine(T("pressKeyToClose"));
            Console.ReadKey(true);
        }
    }
}
