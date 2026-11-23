using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace CodeWalker.GameFiles
{
    /// <summary>Key material is read from the user's verified executable, never shipped with CodeWalker.</summary>
    public sealed class Rpf9KeyStore
    {
        public const string SupportedExecutableSha256 = "e95020b2fa4a721db3df59fdecf75621730ac559d8835252f6c3b658d5a08993";
        public static string ExecutablePath { get; set; }
        private static readonly object Sync = new object();
        private static Rpf9KeyStore cached;
        private static string cachedPath;
        private static DateTime cachedWrite;
        private static long cachedLength;
        private readonly Dictionary<uint, byte[]> blobs = new Dictionary<uint, byte[]>();
        private readonly byte[][] initial = new byte[2][];
        private readonly uint[][][] tables = new uint[2][][];
        internal readonly byte[] DefaultIV;

        public static Rpf9KeyStore Load(string path)
        {
            var info = new FileInfo(path);
            lock (Sync)
            {
                if (cached != null && cachedPath == info.FullName && cachedWrite == info.LastWriteTimeUtc && cachedLength == info.Length) return cached;
                var bytes = File.ReadAllBytes(info.FullName);
                using (var sha = SHA256.Create())
                {
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != SupportedExecutableSha256)
                        throw new NotSupportedException("Unsupported RPF9 executable build (SHA-256 mismatch). See docs/RPF9.md.");
                }
                var result = new Rpf9KeyStore(bytes);
                cachedPath = info.FullName;
                cachedWrite = info.LastWriteTimeUtc;
                cachedLength = info.Length;
                return cached = result;
            }
        }

        internal static Rpf9KeyStore ForArchive(string physicalPath)
        {
            var configured = ExecutablePath ?? Environment.GetEnvironmentVariable("CODEWALKER_RPF9_EXE");
            if (!string.IsNullOrEmpty(configured)) return Load(configured);
            for (var dir = new FileInfo(physicalPath).Directory; dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "game_scarlett_master.exe");
                if (File.Exists(candidate)) return Load(candidate);
            }
            throw new FileNotFoundException("RPF9 requires the supported game_scarlett_master.exe beside the archives or in a parent folder. Alternatively set CODEWALKER_RPF9_EXE.");
        }

        public static Rpf9KeyStore ForArchiveFolder(string folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            return ForArchive(System.IO.Path.Combine(folder,"archive.rpf"));
        }

        private Rpf9KeyStore(byte[] image)
        {
            // File mappings apply only after the exact executable digest has been verified.
            DefaultIV = ReadImage(image, 0x62fd470, 16);
            for (var i = 0; i < 24; i++)
            {
                var p = 0x66e5900 - 0x1200 + i * 24;
                var identity = BitConverter.ToUInt64(image, p);
                if ((uint)identity != 0x02000000) continue;
                var address = BitConverter.ToUInt64(image, p + 8);
                var length = BitConverter.ToUInt64(image, p + 16);
                if (length != 0x10f4 || address < 0x140000000) throw new InvalidDataException("Invalid RPF9 key bank.");
                blobs.Add((uint)(identity >> 32), ReadImage(image, checked((int)(address - 0x140000000)), (int)length));
            }
            for (var direction = 0; direction < 2; direction++)
            {
                var delta = direction * 0xa000;
                initial[direction] = ReadImage(image, 0x6036f30 + delta, 4096);
                tables[direction] = new uint[16][];
                for (var t = 0; t < 16; t++)
                {
                    var raw = ReadImage(image, 0x6037f30 + delta + t * 1024, 1024);
                    var words = new uint[256];
                    for (var j = 0; j < words.Length; j++) words[j] = BitConverter.ToUInt32(raw, j * 4);
                    tables[direction][t] = words;
                }
            }
        }

        private static byte[] ReadImage(byte[] image, int rva, int length)
        {
            var p = checked(rva - 0x1200);
            if (p < 0 || length < 0 || p > image.Length - length) throw new InvalidDataException("Key data outside executable.");
            var result = new byte[length];
            Buffer.BlockCopy(image, p, result, 0, length);
            return result;
        }

        internal Rpf9Cipher Cipher(uint id, byte[] iv = null)
        {
            if (!blobs.TryGetValue(id, out var blob)) throw new NotSupportedException("RPF9 key not available: " + id.ToString("x8"));
            var magic = BitConverter.ToUInt32(blob, 0);
            var forward = magic == 0x2718ce5d;
            if (!forward && magic != 0x4e3b9b8e) throw new InvalidDataException("Invalid key record magic.");
            return new Rpf9Cipher(blob, initial[forward ? 1 : 0], tables[forward ? 1 : 0], iv ?? DefaultIV, forward);
        }
    }

    internal sealed class Rpf9Cipher
    {
        private readonly uint[] keys = new uint[60];
        private readonly byte[] final = new byte[4096];
        private readonly byte[] initial, iv;
        private readonly uint[][] tables;
        private readonly bool forward;

        internal Rpf9Cipher(byte[] blob, byte[] initial, uint[][] tables, byte[] iv, bool forward)
        {
            if (iv.Length != 16) throw new InvalidDataException("Invalid cipher IV.");
            this.initial = initial;
            this.tables = tables;
            this.iv = (byte[])iv.Clone();
            this.forward = forward;
            for (var i = 0; i < keys.Length; i++) keys[i] = BitConverter.ToUInt32(blob, 4 + i * 4);
            Buffer.BlockCopy(blob, 244, final, 0, final.Length);
        }

        private static int B(uint word, int shift) { return (int)((word >> shift) & 255); }

        private void Block(byte[] input, int offset, byte[] output)
        {
            var state = new uint[4];
            var next = new uint[4];
            for (var i = 0; i < 4; i++)
            {
                uint word = 0;
                for (var j = 0; j < 4; j++) word = (word << 8) | initial[(i * 4 + j) * 256 + input[offset + i * 4 + j]];
                state[i] = word ^ keys[i];
            }
            var t = tables;
            for (var r = 0; r < 13; r++)
            {
                var a = state[0]; var b = state[1]; var c = state[2]; var d = state[3];
                if (forward)
                {
                    next[0] = t[0][B(a,24)] ^ t[5][B(b,16)] ^ t[10][B(c,8)] ^ t[15][B(d,0)];
                    next[1] = t[3][B(a,0)] ^ t[4][B(b,24)] ^ t[9][B(c,16)] ^ t[14][B(d,8)];
                    next[2] = t[2][B(a,8)] ^ t[7][B(b,0)] ^ t[8][B(c,24)] ^ t[13][B(d,16)];
                    next[3] = t[1][B(a,16)] ^ t[6][B(b,8)] ^ t[11][B(c,0)] ^ t[12][B(d,24)];
                }
                else
                {
                    next[0] = t[0][B(a,24)] ^ t[7][B(b,0)] ^ t[10][B(c,8)] ^ t[13][B(d,16)];
                    next[1] = t[1][B(a,16)] ^ t[4][B(b,24)] ^ t[11][B(c,0)] ^ t[14][B(d,8)];
                    next[2] = t[2][B(a,8)] ^ t[5][B(b,16)] ^ t[8][B(c,24)] ^ t[15][B(d,0)];
                    next[3] = t[3][B(a,0)] ^ t[6][B(b,8)] ^ t[9][B(c,16)] ^ t[12][B(d,24)];
                }
                for (var i = 0; i < 4; i++) next[i] ^= keys[4 + r * 4 + i];
                var swap = state; state = next; next = swap;
            }
            for (var col = 0; col < 4; col++)
                for (var row = 0; row < 4; row++)
                {
                    var source = forward ? (col + row) % 4 : (col - row + 4) % 4;
                    var i = col * 4 + row;
                    output[i] = final[i * 256 + B(state[source], 24 - row * 8)];
                }
        }

        internal void Cbc(byte[] data, int start, int length)
        {
            if (forward || (length & 15) != 0) throw new InvalidDataException("Invalid metadata cipher direction/alignment.");
            var previous = (byte[])iv.Clone();
            var decoded = new byte[16];
            for (var p = start; p < start + length; p += 16)
            {
                Block(data, p, decoded);
                for (var i = 0; i < 16; i++) { var c = data[p + i]; data[p + i] = (byte)(decoded[i] ^ previous[i]); previous[i] = c; }
            }
        }

        internal void SelectiveCtr(byte[] data, int count, long offset, long total, byte[] configuration)
        {
            if (!forward || offset < 0 || count < 0 || count > data.Length || offset > total - count) throw new InvalidDataException("Invalid payload cipher slice.");
            var head = checked((long)BitConverter.ToUInt64(configuration, 24));
            var block = checked((long)BitConverter.ToUInt64(configuration, 32));
            var gap = checked((long)BitConverter.ToUInt64(configuration, 40));
            var tail = checked((long)BitConverter.ToUInt64(configuration, 48));
            if (BitConverter.ToUInt64(configuration, 56) == 0) throw new NotSupportedException("Unsupported selective cipher layout.");
            long end = offset + count, last = offset;
            Action<long, long> apply = (a, b) =>
            {
                a = Math.Max(a, last); b = Math.Min(b, Math.Min(total, end));
                if (a >= b) return;
                XorRange(data, checked((int)(a - offset)), checked((int)(b - a)), a);
                last = b;
            };
            apply(0, head);
            var tailStart = Math.Max(head, total - tail);
            if (block > 0)
            {
                var period = checked(block + gap);
                var p = checked(head + Math.Max(0, (offset - head) / period) * period);
                while (p < Math.Min(tailStart, end))
                {
                    apply(p, Math.Min(checked(p + block), tailStart));
                    p = checked(p + period);
                }
            }
            apply(tailStart, total);
        }

        private void XorRange(byte[] data, int start, int count, long absolute)
        {
            var counter = (byte[])iv.Clone();
            ulong carry = (ulong)(absolute / 16);
            for (var i = 15; i >= 0; i--) { carry += counter[i]; counter[i] = (byte)carry; carry >>= 8; }
            var key = new byte[16];
            var skip = (int)(absolute & 15);
            while (count > 0)
            {
                Block(counter, 0, key);
                for (var i = skip; i < 16 && count > 0; i++, count--) data[start++] ^= key[i];
                skip = 0;
                for (var i = 15; i >= 0; i--) if (++counter[i] != 0) break;
            }
        }
    }
}
