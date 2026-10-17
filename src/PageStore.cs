using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace KbmOverlay
{
    // Overlay pages built into the exe. A file in the "pages" folder next to the exe
    // wins over the built-in one, so pages can be customized without rebuilding.
    internal sealed class PageStore
    {
        private const string Prefix = "pages/";

        private readonly Dictionary<string, byte[]> embedded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public string OverrideDir { get; }

        public PageStore(string overrideDir)
        {
            OverrideDir = overrideDir;
            Assembly assembly = typeof(PageStore).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                string name = resource.Replace('\\', '/');
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    embedded[name.Substring(Prefix.Length)] = memory.ToArray();
                }
            }
        }

        // path is already checked to be a plain relative path (letters, digits, - _ / .).
        public bool TryGet(string path, out byte[] data)
        {
            string file = Path.Combine(OverrideDir, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file))
            {
                try
                {
                    data = File.ReadAllBytes(file);
                    return true;
                }
                catch (IOException)
                {
                    // Being saved by an editor right now; use the built-in copy.
                }
            }
            return embedded.TryGetValue(path, out data);
        }

        // Writes the built-in pages to OverrideDir, keeping files that already exist.
        public int Export()
        {
            int written = 0;
            foreach (KeyValuePair<string, byte[]> page in embedded)
            {
                string file = Path.Combine(OverrideDir, page.Key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, page.Value);
                written++;
            }
            return written;
        }
    }
}
