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

            // Read-only check for troubleshooting the streaming PC's firewall.
            if (args.Length == 1 && args[0] == "--firewall-check")
            {
                Firewall.Status status = Firewall.Check(exePath, RelayManager.DefaultPort);
                Console.WriteLine("exe: " + exePath);
                Console.WriteLine("포트 " + RelayManager.DefaultPort + " 허용 (TCP / UDP / 이 exe 차단 규칙)");
                foreach (var profile in new[] { (Firewall.ProfilePrivate, "개인"), (Firewall.ProfileDomain, "도메인"), (Firewall.ProfilePublic, "공용") })
                {
                    Console.WriteLine("  " + profile.Item2 + ": " +
                                      ((status.TcpProfiles & profile.Item1) != 0 ? "예" : "아니오") + " / " +
                                      ((status.UdpProfiles & profile.Item1) != 0 ? "예" : "아니오") + " / " +
                                      ((status.BlockedProfiles & profile.Item1) != 0 ? "있음" : "없음"));
                }
                Console.WriteLine("공용 네트워크만 연결됨: " + (Firewall.OnlyPublicNetwork() ? "예" : "아니오"));
                return 0;
            }

            int? port = null;
            int dev = 0;
            bool devPublic = false;
            bool export = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                if ((arg == "--port" || arg == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p > 0 && p < 65536)
                {
                    port = p;
                    i++;
                }
                else if (arg == "--export-pages")
                {
                    export = true;
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
                    Console.WriteLine("사용법: KbmOverlay.exe [--port 번호] [--export-pages]");
                    Console.WriteLine("  --port 번호       기본값 " + DefaultPort + ". OBS 주소의 포트도 같이 바꿔야 해요.");
                    Console.WriteLine("  --export-pages    내장 페이지를 exe 옆 pages 폴더로 꺼내요 (꾸미기용).");
                    return 2;
                }
            }

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var pages = new PageStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pages"));

            if (export)
            {
                int written = pages.Export();
                Console.WriteLine("pages 폴더에 " + written + "개 파일을 꺼냈어요 (이미 있던 파일은 그대로 뒀어요): " + pages.OverrideDir);
                return 0;
            }

            string mutexName = dev == 0 ? @"Local\KbmOverlay" : @"Local\KbmOverlay-dev" + dev;
            using (var mutex = new Mutex(true, mutexName, out bool firstInstance))
            {
                if (!firstInstance)
                {
                    Console.WriteLine("KbmOverlay가 이미 실행 중이에요. 작업 표시줄에서 기존 창을 확인하세요.");
                    WaitBeforeClose();
                    return 1;
                }
                return Run(port ?? DefaultPort + 10 * dev, dev, devPublic, exePath, pages, version);
            }
        }

        private static int Run(int port, int dev, bool devPublic, string exePath, PageStore pages, string version)
        {
            bool elevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            string dataDir = dev == 0
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KbmOverlay")
                : Path.Combine(Path.GetTempPath(), "KbmOverlay-dev" + dev);
            Settings settings = Settings.Load(dataDir);

            var hub = new InputHub();
            hub.ClientCountChanged += count => Log.Info("  연결된 페이지: " + count + "개");
            hub.Start();

            var devProbes = new List<IPEndPoint>();
            if (dev != 0)
                for (int other = 1; other <= 9; other++)
                    if (other != dev) devProbes.Add(new IPEndPoint(IPAddress.Loopback, RelayManager.DefaultPort + 10 * other));
            var relay = new RelayManager(settings, hub, exePath, RelayManager.DefaultPort + 10 * dev, dev != 0, devPublic, devProbes);

            try
            {
                new WebServer(port, pages, hub, elevated, version, relay.Command).Start();
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    Log.Error("포트 " + port + "를 다른 프로그램이 쓰고 있어요. 다른 번호로 실행하세요: KbmOverlay.exe --port 5530");
                else if (e.SocketErrorCode == SocketError.AccessDenied)
                    Log.Error("Windows가 포트 " + port + "를 예약해 뒀어요 (Hyper-V/WSL). 다른 번호로 실행하세요: KbmOverlay.exe --port 5530");
                else
                    Log.Error("포트 " + port + "를 열 수 없어요: " + e.Message);
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
                Log.Error("키보드·마우스 입력을 받을 수 없어요: " + e.Message);
                Log.Flush();
                WaitBeforeClose();
                return 1;
            }

            string root = "http://localhost:" + port + "/";
            Log.Accent("KbmOverlay " + version + "  키보드·마우스 오버레이" + (dev != 0 ? "  [개발용 " + dev + "]" : ""));
            Log.Info("");
            Log.Good("  설정 페이지  " + root + "   (Enter를 누르면 브라우저로 열려요)");
            Log.Info("               OBS 주소 복사, 권장 크기, 미리보기, 투컴 설정이 있어요.");
            Log.Info("");
            Log.Info("  OBS 브라우저 소스 주소 (투컴이어도 OBS가 있는 PC에서는 이 주소 그대로예요)");
            Url(root + "keyboard.html", "키보드 (WASD)");
            Url(root + "keyboard.html?layout=tkl", "키보드 (텐키리스)");
            Url(root + "keyboard.html?layout=full", "키보드 (풀배열)");
            Url(root + "mouse.html", "마우스");
            Url(root + "mouse-move.html", "마우스 이동");
            Log.Info("");
            foreach (string line in relay.Describe()) Log.Info(line);
            Log.Info("");
            if (!elevated)
            {
                Log.Warn("  [!] 관리자 권한이 아니에요. 관리자 권한으로 실행한 게임의 입력은 안 보일 수 있어요.");
                Log.Warn("      그럴 땐 이 프로그램을 우클릭 → \"관리자 권한으로 실행\".");
                Log.Info("");
            }
            Log.Info("  끄려면 이 창을 닫으세요.");
            Log.Info("");

            relay.Start();

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
                Log.Warn("브라우저를 열 수 없어요 (" + e.Message + "). 주소를 직접 열어 주세요: " + url);
            }
        }

        private static void WaitBeforeClose()
        {
            if (Console.IsInputRedirected) return;
            Console.WriteLine("아무 키나 누르면 창이 닫혀요.");
            Console.ReadKey(true);
        }
    }
}
