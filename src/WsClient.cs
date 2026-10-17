using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace KbmOverlay
{
    // One connected page. Sends text frames and answers ping/close. Overlay pages send nothing;
    // the settings page sends two-PC commands as text.
    internal sealed class WsClient
    {
        private const int MaxIncomingPayload = 64 * 1024;

        public readonly object SendLock = new object();
        public readonly Category Want;

        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly Action<string> onText;
        private volatile bool closed;

        public WsClient(TcpClient tcp, NetworkStream stream, Category want, Action<string> onText)
        {
            this.tcp = tcp;
            this.stream = stream;
            this.onText = onText;
            Want = want;
        }

        public bool TrySend(byte[] frame)
        {
            lock (SendLock)
            {
                if (closed) return false;
                try
                {
                    stream.Write(frame, 0, frame.Length);
                    return true;
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
                {
                    closed = true;
                    return false;
                }
            }
        }

        public void Close()
        {
            closed = true;
            try { tcp.Close(); } catch (Exception) { }
        }

        // Runs until the page disconnects.
        public void ReadLoop()
        {
            var header = new byte[10];
            var mask = new byte[4];
            while (!closed)
            {
                if (!ReadExact(header, 0, 2)) return;
                int opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;
                if (length == 126)
                {
                    if (!ReadExact(header, 2, 2)) return;
                    length = (header[2] << 8) | header[3];
                }
                else if (length == 127)
                {
                    if (!ReadExact(header, 2, 8)) return;
                    length = 0;
                    for (int i = 2; i < 10; i++) length = (length << 8) | header[i];
                }

                // Browsers always mask their frames; anything else is not one of our pages.
                if (!masked || length > MaxIncomingPayload)
                {
                    TrySend(Frame(0x8, new byte[] { 0x03, 0xEA })); // 1002 protocol error
                    return;
                }
                if (!ReadExact(mask, 0, 4)) return;
                var payload = new byte[length];
                if (!ReadExact(payload, 0, payload.Length)) return;
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                if (opcode == 0x8)
                {
                    TrySend(Frame(0x8, payload.Length >= 2 ? new[] { payload[0], payload[1] } : new byte[0]));
                    return;
                }
                if (opcode == 0x9)
                    TrySend(Frame(0xA, payload));
                else if (opcode == 0x1 && (header[0] & 0x80) != 0 && onText != null)
                    onText(Encoding.UTF8.GetString(payload));
            }
        }

        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, offset, count);
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
                {
                    return false;
                }
                if (read <= 0) return false;
                offset += read;
                count -= read;
            }
            return true;
        }

        public static byte[] TextFrame(string text)
        {
            return Frame(0x1, Encoding.UTF8.GetBytes(text));
        }

        private static byte[] Frame(int opcode, byte[] payload)
        {
            int length = payload.Length;
            int headerLength = length < 126 ? 2 : length <= 0xFFFF ? 4 : 10;
            var frame = new byte[headerLength + length];
            frame[0] = (byte)(0x80 | opcode);
            if (length < 126)
            {
                frame[1] = (byte)length;
            }
            else if (length <= 0xFFFF)
            {
                frame[1] = 126;
                frame[2] = (byte)(length >> 8);
                frame[3] = (byte)length;
            }
            else
            {
                frame[1] = 127;
                for (int i = 0; i < 8; i++) frame[9 - i] = (byte)((long)length >> (8 * i));
            }
            Buffer.BlockCopy(payload, 0, frame, headerLength, length);
            return frame;
        }
    }
}
