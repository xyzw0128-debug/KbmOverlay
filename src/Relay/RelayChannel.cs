using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KbmOverlay.Relay
{
    // Keys for one game PC -> streaming PC connection.
    //
    // Both sides make a fresh P-256 key pair per connection and exchange public keys in the hello
    // messages. From the shared secret and both hellos they derive:
    //   - AES-256 + HMAC-SHA256 keys for each direction,
    //   - a 6-digit code both screens show the first time, so the user can see nobody is in between,
    //   - the pairing secret both PCs store after the user approves. Later connections prove they
    //     know it instead of asking again.
    internal sealed class SessionKeys
    {
        public byte[] SendEnc, SendMac, RecvEnc, RecvMac;
        public byte[] PairSecret;
        public byte[] Transcript;
        public string Code;
    }

    internal static class RelayCrypto
    {
        public static ECDiffieHellmanCng NewKey()
        {
            var key = new ECDiffieHellmanCng(256);
            key.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
            key.HashAlgorithm = CngAlgorithm.Sha256;
            return key;
        }

        public static byte[] PublicBlob(ECDiffieHellmanCng key) => key.PublicKey.ToByteArray();

        // A P-256 ECDH public key blob: "ECK1", length 32, then X and Y (32 bytes each).
        public static bool IsPublicBlob(byte[] blob)
        {
            return blob != null && blob.Length == 72 &&
                   blob[0] == 0x45 && blob[1] == 0x43 && blob[2] == 0x4B && blob[3] == 0x31 &&
                   blob[4] == 32 && blob[5] == 0 && blob[6] == 0 && blob[7] == 0;
        }

        public static SessionKeys Derive(ECDiffieHellmanCng mine, byte[] peerBlob, byte[] clientHello, byte[] serverHello, bool isClient)
        {
            byte[] shared;
            using (ECDiffieHellmanPublicKey peer = ECDiffieHellmanCngPublicKey.FromByteArray(peerBlob, CngKeyBlobFormat.EccPublicBlob))
                shared = mine.DeriveKeyMaterial(peer);

            byte[] transcript;
            using (SHA256 sha = SHA256.Create())
            {
                sha.TransformBlock(clientHello, 0, clientHello.Length, null, 0);
                sha.TransformFinalBlock(serverHello, 0, serverHello.Length);
                transcript = sha.Hash;
            }

            byte[] prk = Hmac(shared, transcript);
            byte[] c2sEnc = Hmac(prk, Ascii("c2s enc"));
            byte[] c2sMac = Hmac(prk, Ascii("c2s mac"));
            byte[] s2cEnc = Hmac(prk, Ascii("s2c enc"));
            byte[] s2cMac = Hmac(prk, Ascii("s2c mac"));
            byte[] codeBytes = Hmac(prk, Ascii("code"));
            uint codeNumber = ((uint)codeBytes[0] << 24 | (uint)codeBytes[1] << 16 | (uint)codeBytes[2] << 8 | codeBytes[3]) % 1000000;

            return new SessionKeys
            {
                SendEnc = isClient ? c2sEnc : s2cEnc,
                SendMac = isClient ? c2sMac : s2cMac,
                RecvEnc = isClient ? s2cEnc : c2sEnc,
                RecvMac = isClient ? s2cMac : c2sMac,
                PairSecret = Hmac(prk, Ascii("pair")),
                Transcript = transcript,
                Code = codeNumber.ToString("D6").Insert(3, " ")
            };
        }

        // Proves knowledge of the pairing secret for this connection only.
        public static byte[] Proof(byte[] secret, string role, byte[] transcript) => Hmac(secret, Ascii(role), transcript);

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (var hmac = new HMACSHA256(key))
            {
                for (int i = 0; i < parts.Length - 1; i++) hmac.TransformBlock(parts[i], 0, parts[i].Length, null, 0);
                byte[] last = parts[parts.Length - 1];
                hmac.TransformFinalBlock(last, 0, last.Length);
                return hmac.Hash;
            }
        }

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return bytes;
        }

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    }

    // Length-prefixed frames on a TCP stream.
    internal static class Frames
    {
        public static void Write(Stream stream, byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            frame[0] = (byte)(payload.Length >> 24);
            frame[1] = (byte)(payload.Length >> 16);
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            stream.Write(frame, 0, frame.Length);
        }

        public static byte[] Read(Stream stream, int maxLength)
        {
            byte[] header = ReadExact(stream, 4);
            int length = header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3];
            if (length < 0 || length > maxLength) throw new InvalidDataException("frame too large");
            return ReadExact(stream, length);
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }
    }

    // Encrypted, authenticated messages: IV | AES-256-CBC ciphertext | HMAC-SHA256(seq | IV | ciphertext).
    // The sequence number is implicit, so replayed, dropped or reordered frames fail the check.
    internal sealed class SecureChannel
    {
        private const int IvLength = 16;
        private const int MacLength = 32;

        private readonly Stream stream;
        private readonly SessionKeys keys;
        private readonly object sendLock = new object();
        private long sendSeq;
        private long recvSeq;

        public SecureChannel(Stream stream, SessionKeys keys)
        {
            this.stream = stream;
            this.keys = keys;
        }

        public void Send(string text)
        {
            byte[] plain = Encoding.UTF8.GetBytes(text);
            lock (sendLock)
            {
                byte[] iv = RelayCrypto.RandomBytes(IvLength);
                byte[] cipher;
                using (Aes aes = Aes.Create())
                using (ICryptoTransform encryptor = aes.CreateEncryptor(keys.SendEnc, iv))
                    cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                byte[] mac = RelayCrypto.Hmac(keys.SendMac, Seq(sendSeq), iv, cipher);
                sendSeq++;

                var payload = new byte[IvLength + cipher.Length + MacLength];
                Buffer.BlockCopy(iv, 0, payload, 0, IvLength);
                Buffer.BlockCopy(cipher, 0, payload, IvLength, cipher.Length);
                Buffer.BlockCopy(mac, 0, payload, IvLength + cipher.Length, MacLength);
                Frames.Write(stream, payload);
            }
        }

        // Throws InvalidDataException when a frame was not made with this connection's keys.
        public string Receive(int maxLength)
        {
            byte[] payload = Frames.Read(stream, maxLength + IvLength + MacLength + 16);
            int cipherLength = payload.Length - IvLength - MacLength;
            if (cipherLength <= 0 || cipherLength % 16 != 0) throw new InvalidDataException("bad frame");

            var iv = new byte[IvLength];
            var cipher = new byte[cipherLength];
            var mac = new byte[MacLength];
            Buffer.BlockCopy(payload, 0, iv, 0, IvLength);
            Buffer.BlockCopy(payload, IvLength, cipher, 0, cipherLength);
            Buffer.BlockCopy(payload, IvLength + cipherLength, mac, 0, MacLength);
            if (!RelayCrypto.SameBytes(mac, RelayCrypto.Hmac(keys.RecvMac, Seq(recvSeq), iv, cipher)))
                throw new InvalidDataException("bad mac");
            recvSeq++;

            using (Aes aes = Aes.Create())
            using (ICryptoTransform decryptor = aes.CreateDecryptor(keys.RecvEnc, iv))
                return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
        }

        private static byte[] Seq(long value)
        {
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++) bytes[7 - i] = (byte)(value >> (8 * i));
            return bytes;
        }
    }
}
