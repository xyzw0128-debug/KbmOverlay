using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KbmOverlay
{
    // A small HTTP + WebSocket server that only listens on this PC (127.0.0.1 and ::1).
    // Binding to loopback means no firewall prompt, and the Host/Origin checks stop other
    // websites open in a normal browser from reading keystrokes.
    internal sealed class WebServer
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private static readonly Regex SafePath = new Regex(@"^[A-Za-z0-9_\-]+(/[A-Za-z0-9_\-]+)*\.[A-Za-z0-9]+$");

        private readonly int port;
        private readonly PageStore pages;
        private readonly InputHub hub;
        private readonly bool elevated;
        private readonly string version;
        private readonly Action<string> onCommand;

        public WebServer(int port, PageStore pages, InputHub hub, bool elevated, string version, Action<string> onCommand)
        {
            this.port = port;
            this.pages = pages;
            this.hub = hub;
            this.elevated = elevated;
            this.version = version;
            this.onCommand = onCommand;
        }

        public void Start()
        {
            var listeners = new List<TcpListener> { Listen(IPAddress.Loopback) };
            try
            {
                listeners.Add(Listen(IPAddress.IPv6Loopback));
            }
            catch (SocketException)
            {
                // IPv6 may be turned off; 127.0.0.1 is enough.
            }

            foreach (TcpListener listener in listeners)
            {
                var thread = new Thread(() => AcceptLoop(listener));
                thread.IsBackground = true;
                thread.Name = "Accept";
                thread.Start();
            }
        }

        private TcpListener Listen(IPAddress address)
        {
            var listener = new TcpListener(address, port);
            listener.ExclusiveAddressUse = true;
            listener.Start();
            return listener;
        }

        private void AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient tcp;
                try
                {
                    tcp = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                var thread = new Thread(() => Handle(tcp));
                thread.IsBackground = true;
                thread.Start();
            }
        }

        private void Handle(TcpClient tcp)
        {
            try
            {
                tcp.NoDelay = true;
                NetworkStream stream = tcp.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                HttpRequest request = HttpRequest.Read(stream);
                if (request == null) return;

                if (!IsLoopbackHost(request.Host))
                {
                    Respond(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Open this page through http://localhost:" + port + "/"), true);
                    return;
                }

                if (request.Path == "/ws")
                    RunWebSocket(tcp, stream, request);
                else
                    Serve(stream, request);
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
            {
                // The page went away mid-request.
            }
            catch (Exception e)
            {
                // An exception escaping a thread would end the whole program.
                Log.Error("요청을 처리하다가 오류가 났어요: " + e.Message);
            }
            finally
            {
                tcp.Close();
            }
        }

        private void RunWebSocket(TcpClient tcp, NetworkStream stream, HttpRequest request)
        {
            string key = request.Header("Sec-WebSocket-Key");
            string upgrade = request.Header("Upgrade");
            if (key == null || upgrade == null || upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Respond(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("WebSocket expected"), true);
                return;
            }

            string origin = request.Header("Origin");
            if (origin != null && !IsOwnOrigin(origin))
            {
                Log.Warn("다른 사이트(" + origin + ")에서 입력을 읽으려는 접속을 막았어요.");
                Respond(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Origin not allowed"), true);
                return;
            }

            string accept;
            using (SHA1 sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.ReadTimeout = Timeout.Infinite;
            stream.WriteTimeout = 3000;

            var client = new WsClient(tcp, stream, ParseWant(request.Query("want")), onCommand);
            hub.AddClient(client);
            try
            {
                client.ReadLoop();
            }
            finally
            {
                hub.RemoveClient(client);
            }
        }

        private void Serve(NetworkStream stream, HttpRequest request)
        {
            bool head = request.Method == "HEAD";
            if (request.Method != "GET" && !head)
            {
                Respond(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Method not allowed"), true);
                return;
            }

            string path = request.Path == "/" ? "index.html" : request.Path.TrimStart('/');
            if (path == "info.json")
            {
                string info = "{\"name\":\"KbmOverlay\",\"version\":\"" + version + "\",\"port\":" + port +
                              ",\"elevated\":" + (elevated ? "true" : "false") + ",\"clients\":" + hub.ClientCount + "}";
                Respond(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(info), !head);
                return;
            }

            if (!SafePath.IsMatch(path) || !pages.TryGet(path, out byte[] body))
            {
                Respond(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found: " + path), !head);
                return;
            }
            Respond(stream, 200, ContentType(path), body, !head);
        }

        private static void Respond(Stream stream, int status, string contentType, byte[] body, bool sendBody)
        {
            string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 403 ? "Forbidden" :
                            status == 404 ? "Not Found" : "Method Not Allowed";
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Cache-Control: no-cache\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            if (sendBody) stream.Write(body, 0, body.Length);
        }

        private static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".txt": return "text/plain; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".woff2": return "font/woff2";
                case ".ico": return "image/x-icon";
                default: return "application/octet-stream";
            }
        }

        private static Category ParseWant(string want)
        {
            if (string.IsNullOrEmpty(want)) return Category.All;
            Category result = Category.None;
            foreach (string part in want.Split(','))
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "key": result |= Category.Key; break;
                    case "button": result |= Category.Button; break;
                    case "wheel": result |= Category.Wheel; break;
                    case "move": result |= Category.Move; break;
                    case "relay": result |= Category.Relay; break;
                }
            }
            return result == Category.None ? Category.All : result;
        }

        private static bool IsLoopbackName(string host)
        {
            host = host.ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == "[::1]";
        }

        private static bool IsLoopbackHost(string hostHeader)
        {
            if (string.IsNullOrEmpty(hostHeader)) return false;
            string host = hostHeader;
            int colon = host.LastIndexOf(':');
            if (colon > host.LastIndexOf(']')) host = host.Substring(0, colon);
            return IsLoopbackName(host);
        }

        // Only pages served by this program (any loopback name, same port) may open the WebSocket.
        private bool IsOwnOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri uri)) return false;
            string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.DnsSafeHost + "]" : uri.Host;
            return uri.Scheme == Uri.UriSchemeHttp && uri.Port == port && IsLoopbackName(host);
        }

        private sealed class HttpRequest
        {
            public string Method;
            public string Path;
            public string Host;
            private readonly Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Header(string name) => headers.TryGetValue(name, out string value) ? value : null;
            public string Query(string name) => query.TryGetValue(name, out string value) ? value : null;

            public static HttpRequest Read(Stream stream)
            {
                var buffer = new byte[8192];
                int length = 0;
                int end;
                while (true)
                {
                    int read = stream.Read(buffer, length, buffer.Length - length);
                    if (read <= 0) return null;
                    length += read;
                    end = FindHeaderEnd(buffer, length);
                    if (end >= 0) break;
                    if (length == buffer.Length) return null;
                }

                string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 3) return null;

                var request = new HttpRequest { Method = requestLine[0] };
                string target = requestLine[1];
                int q = target.IndexOf('?');
                request.Path = Uri.UnescapeDataString(q < 0 ? target : target.Substring(0, q));
                if (q >= 0)
                {
                    foreach (string pair in target.Substring(q + 1).Split('&'))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        request.query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    }
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    request.headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
                request.Host = request.Header("Host");
                return request;
            }

            private static int FindHeaderEnd(byte[] buffer, int length)
            {
                for (int i = 3; i < length; i++)
                    if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                        return i - 3;
                return -1;
            }
        }
    }
}
