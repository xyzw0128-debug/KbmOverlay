using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KbmOverlay
{
    // Another PC this one is paired with.
    internal sealed class Peer
    {
        public string Id;
        public string Name;
        public string Address;  // last known address (only kept for the streaming PC we send to)
        public int Port;
        public byte[] Secret;   // pairing secret, known to both PCs
    }

    // Per-user settings in %APPDATA%\KbmOverlay\settings.json. Pairing secrets are encrypted with
    // DPAPI, so the file is useless on another Windows account or PC.
    // Not thread-safe: callers lock on the Settings object.
    internal sealed class Settings
    {
        private static readonly Regex IdPattern = new Regex("^[0-9a-f]{32}$");
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("KbmOverlay pairing");

        private readonly string path;

        public string Id;              // this PC's id, sent to other PCs
        public bool Receive;           // this PC accepts input from game PCs
        public bool AllowPublic;       // ...also while connected to a "Public" network
        public string Lang;            // language picked on the settings page; null = automatic
        public readonly List<Peer> Senders = new List<Peer>();  // game PCs allowed to send here
        public Peer Receiver;          // streaming PC this PC sends to

        private Settings(string path)
        {
            this.path = path;
        }

        public static bool IsId(string id) => id != null && IdPattern.IsMatch(id);

        public static Settings Load(string directory)
        {
            var settings = new Settings(Path.Combine(directory, "settings.json"));
            try
            {
                if (File.Exists(settings.path))
                {
                    Dictionary<string, object> root = Json.Parse(File.ReadAllText(settings.path, Encoding.UTF8));
                    settings.Id = Json.Str(root, "id");
                    settings.Receive = Json.Bool(root, "receive") ?? false;
                    settings.AllowPublic = Json.Bool(root, "allowPublic") ?? false;
                    settings.Lang = Json.Str(root, "lang");
                    foreach (object item in Json.Array(root, "senders") ?? new object[0])
                    {
                        Peer peer = ReadPeer(item as Dictionary<string, object>);
                        if (peer != null) settings.Senders.Add(peer);
                    }
                    if (root != null && root.TryGetValue("receiver", out object receiver))
                        settings.Receiver = ReadPeer(receiver as Dictionary<string, object>);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Warn(I18n.T("settings.readFailed", ("error", e.Message)));
            }

            if (!IsId(settings.Id))
            {
                settings.Id = Guid.NewGuid().ToString("N");
                settings.Save();
            }
            return settings;
        }

        public void Save()
        {
            var senders = new List<object>();
            foreach (Peer peer in Senders) senders.Add(WritePeer(peer));
            var root = new Dictionary<string, object>
            {
                ["id"] = Id,
                ["receive"] = Receive,
                ["allowPublic"] = AllowPublic,
                ["lang"] = Lang,
                ["senders"] = senders,
                ["receiver"] = Receiver == null ? null : WritePeer(Receiver)
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, Json.Write(root), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error(I18n.T("settings.saveFailed", ("error", e.Message)));
            }
        }

        public Peer FindSender(string id) => Senders.Find(p => p.Id == id);

        private static Dictionary<string, object> WritePeer(Peer peer)
        {
            return new Dictionary<string, object>
            {
                ["id"] = peer.Id,
                ["name"] = peer.Name,
                ["address"] = peer.Address,
                ["port"] = peer.Port,
                ["secret"] = Convert.ToBase64String(ProtectedData.Protect(peer.Secret, Entropy, DataProtectionScope.CurrentUser))
            };
        }

        private static Peer ReadPeer(Dictionary<string, object> map)
        {
            string id = Json.Str(map, "id");
            string secret = Json.Str(map, "secret");
            if (!IsId(id) || secret == null) return null;
            try
            {
                return new Peer
                {
                    Id = id,
                    Name = Json.Str(map, "name") ?? "?",
                    Address = Json.Str(map, "address"),
                    Port = (int)(Json.Long(map, "port") ?? 0),
                    Secret = ProtectedData.Unprotect(Convert.FromBase64String(secret), Entropy, DataProtectionScope.CurrentUser)
                };
            }
            catch (Exception e) when (e is CryptographicException || e is FormatException)
            {
                return null; // made on another account or PC: pair again
            }
        }
    }
}
