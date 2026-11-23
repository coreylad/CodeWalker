using System;
using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace CodeWalker.GameFiles
{
    public sealed class Rpf9Block
    {
        public long DecodedSize { get; internal set; }
        public long CompressedSize { get; internal set; }
        public int Storage { get; internal set; }

        internal static Rpf9Block Decode(ulong q, int kind)
        {
            long decoded, compressed; int storage;
            switch (kind)
            {
                case 2:
                    decoded = (long)((q << 4) & 0xffffff0);
                    compressed = (long)((q >> 28) & 0xffffff0) - (long)((q >> 58) & 15);
                    storage = (int)(q >> 62); break;
                case 3:
                    decoded = 65536 - (long)(q & 65535);
                    compressed = 65536 - (long)((q >> 16) & 65535);
                    storage = (int)((q >> 60) & 3); break;
                case 4:
                    decoded = (long)(q & 0xfffffff);
                    compressed = (long)((q >> 24) & 0xffffff0) - (long)((q >> 52) & 15);
                    storage = (int)((q >> 56) & 3); break;
                default: throw new NotSupportedException("Unsupported RPF9 block format " + kind);
            }
            if (compressed < 0) throw new InvalidDataException("Invalid RPF9 block padding.");
            return new Rpf9Block { DecodedSize = decoded, CompressedSize = compressed, Storage = storage };
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class Rpf9Record
    {
        public int Index { get; internal set; }
        public long Offset { get; internal set; }
        public long StoredSize { get; internal set; }
        public long DecodedSize { get; internal set; }
        public bool IsResource { get; internal set; }
        public int CompressionId { get; internal set; }
        public ulong? MetadataHash { get; internal set; }
        public IReadOnlyList<Rpf9Block> Blocks { get; internal set; }
        public bool IsStoredArchive { get; internal set; }
        internal byte[] CipherConfiguration;
        internal bool IsStored { get { return !IsResource && Blocks == null && StoredSize >= DecodedSize && StoredSize - DecodedSize < 16; } }
    }

    /// <summary>Read-only, executable-build-specific RPF9 archive reader. All offsets are bytes.</summary>
    public sealed class Rpf9Archive
    {
        public const uint Magic = 0x52504639;
        public long StartOffset { get; private set; }
        public long Length { get; private set; }
        public IReadOnlyList<Rpf9Record> Records { get; private set; }
        private Rpf9KeyStore keys;
        internal Rpf9Archive(long start, long length, IReadOnlyList<Rpf9Record> records, Rpf9KeyStore keyStore)
        {
            StartOffset=start; Length=length; Records=records; keys=keyStore;
        }
        private sealed class Table
        {
            internal ulong Type;
            internal uint Count, Offset;
            internal int Stride, Index;
        }

        internal static byte[] ReadExactly(Stream stream, int length)
        {
            var data = new byte[length];
            var p = 0;
            while (p < length)
            {
                var n = stream.Read(data, p, length - p);
                if (n == 0) throw new EndOfStreamException("Truncated RPF9 data.");
                p += n;
            }
            return data;
        }

        private static void Bounds(byte[] data, long p, long length)
        {
            if (p < 0 || length < 0 || p > data.LongLength - length) throw new InvalidDataException("RPF9 metadata range outside buffer.");
        }
        private static ushort U16(byte[] d, long p) { Bounds(d,p,2); return BitConverter.ToUInt16(d,(int)p); }
        private static uint U32(byte[] d, long p) { Bounds(d,p,4); return BitConverter.ToUInt32(d,(int)p); }
        private static ulong U64(byte[] d, long p) { Bounds(d,p,8); return BitConverter.ToUInt64(d,(int)p); }
        private static byte[] Slice(byte[] d, long p, int n) { Bounds(d,p,n); var r = new byte[n]; Buffer.BlockCopy(d,(int)p,r,0,n); return r; }

        public static Rpf9Archive Read(Stream source, long start, long length, Rpf9KeyStore keyStore)
        {
            if (source == null || !source.CanSeek || start < 0 || length < 64 || start > source.Length - length) throw new InvalidDataException("Invalid RPF9 archive bounds.");
            source.Position = start;
            var header = ReadExactly(source, 64);
            if (U32(header,0) != Magic) throw new InvalidDataException("Not an RPF9 archive.");
            long total = U32(header,4), primary = U32(header,8);
            if (primary < 64 || total < primary || total > length || total > 256 * 1024 * 1024) throw new InvalidDataException("Invalid or unsupported RPF9 metadata size (limit 256 MiB).");
            if (header[46] != 9 || (header[45] & 1) == 0) throw new NotSupportedException("Unsupported RPF9 metadata cipher/layout flags.");
            source.Position = start;
            var data = ReadExactly(source, (int)total);
            long configStart = 64 + ((U16(header,26) * 8L + 15) & ~15L);
            long configBytes = U32(header,28);
            long firstEnd = configStart + configBytes - U32(header,48);
            if (configStart > firstEnd || firstEnd > primary) throw new InvalidDataException("Invalid RPF9 cipher boundaries.");
            keyStore.Cipher(0x10000001).Cbc(data,64,checked((int)(firstEnd-64)));
            long mainConfig = -1;
            for (long p = 64; p < configStart; p += 8)
                if (U32(data,p) == 1) { if (mainConfig != -1) throw new InvalidDataException("Duplicate RPF9 main cipher descriptor."); mainConfig = configStart + U32(data,p+4); }
            if (mainConfig < configStart || mainConfig + 22 > firstEnd || U16(data,mainConfig+4) != 16) throw new NotSupportedException("Missing or unsupported RPF9 metadata cipher.");
            var mainCipher = keyStore.Cipher(U32(data,mainConfig), Slice(data,mainConfig+6,16));
            mainCipher.Cbc(data,(int)firstEnd,(int)(primary-firstEnd));
            mainCipher.Cbc(data,(int)primary,(int)(total-primary)); // fresh descriptor-1 IV for the extension
            var count = U16(data,32);
            var optional = U16(data,34);
            var entries = U32(data,36);
            var width = data[44];
            if (optional > count || entries > int.MaxValue || (width != 1 && width != 2 && width != 4)) throw new InvalidDataException("Invalid RPF9 index layout.");
            long tableStart = configStart + configBytes;
            long indexes = tableStart + count * 24L;
            long indexBytes = checked((count-optional) * (long)entries * width);
            Bounds(data,indexes,indexBytes);
            long mainBase = (indexes + indexBytes + 7) & ~7L;
            var tables = new Dictionary<ulong, Table>();
            for (var i = 0; i < count; i++)
            {
                var p = tableStart + i * 24L;
                var t = new Table { Type=U64(data,p), Count=U32(data,p+8), Offset=U32(data,p+12), Stride=(int)(U32(data,p+16)&65535), Index=i };
                if (tables.ContainsKey(t.Type)) throw new InvalidDataException("Duplicate RPF9 typed table.");
                tables.Add(t.Type,t);
            }
            Func<int,ulong,long?> locate = (logical,type) =>
            {
                if (!tables.TryGetValue(type,out var t)) return null;
                if (t.Index >= count-optional) throw new NotSupportedException("RPF9 optional-extension table storage is not implemented for this record type.");
                long p = indexes + ((long)logical * (count-optional) + t.Index) * width;
                uint physical = width == 1 ? data[(int)p] : width == 2 ? U16(data,p) : U32(data,p);
                ulong sentinel = width == 4 ? uint.MaxValue : (1UL << (width*8))-1;
                if (physical == sentinel) return null;
                if (physical >= t.Count) throw new InvalidDataException("Invalid RPF9 physical index.");
                long position = mainBase + t.Offset + physical * (long)t.Stride;
                Bounds(data,position,t.Stride);
                return position;
            };
            var records = new List<Rpf9Record>();
            for (var i = 0; i < (int)entries; i++)
            {
                const ulong binaryType = 0x33f70dfaf2c50989, resourceType = 0xba180afb232d1b4b;
                var p = locate(i,binaryType);
                var resource = !p.HasValue;
                if (resource) p = locate(i,resourceType);
                if (!p.HasValue) throw new NotSupportedException("No supported RPF9 file record for logical index " + i);
                var position = p.Value;
                if (tables[resource ? resourceType : binaryType].Stride != (resource ? 20 : 24)) throw new NotSupportedException("Unsupported RPF9 file-record stride.");
                var r = new Rpf9Record { Index=i, IsResource=resource };
                if (resource)
                {
                    r.Offset = (long)U32(data,position+12) << 4;
                    r.StoredSize = (long)U32(data,position+8) << 4;
                    r.DecodedSize = (U32(data,position)&0x7ffffff0L) + (U32(data,position+4)&0xfffffff0L);
                    r.CompressionId = (int)((U32(data,position+16)>>8)&255);
                }
                else
                {
                    var a = U64(data,position);
                    r.Offset = (long)((a<<4)&0xffffffffff0UL);
                    r.StoredSize = (long)(U64(data,position+8)&0xffffffffffUL)*16;
                    r.DecodedSize = (long)(U64(data,position+16)&0xfffffffffffUL);
                    r.CompressionId = (int)((a>>48)&255);
                }
                if (r.Offset > length-r.StoredSize || (r.StoredSize > 0 && r.Offset < total)) throw new InvalidDataException("RPF9 entry overlaps metadata or exceeds archive bounds.");
                p = locate(i,0x73bcb72dce74f11f);
                if (p.HasValue) { if (tables[0x73bcb72dce74f11f].Stride != 8) throw new NotSupportedException("Unsupported RPF9 hash stride."); r.MetadataHash=U64(data,p.Value); }
                p = locate(i,0x286030a358a789fb);
                if (p.HasValue) { if (tables[0x286030a358a789fb].Stride != 64) throw new NotSupportedException("Unsupported RPF9 cipher stride."); r.CipherConfiguration=Slice(data,p.Value,64); }
                p = locate(i,0xe0c12d7a5d4884c5);
                if (p.HasValue)
                {
                    if (tables[0xe0c12d7a5d4884c5].Stride != 8) throw new NotSupportedException("Unsupported RPF9 block-table stride.");
                    var q = p.Value;
                    long relative = U32(data,q);
                    var blocks = U16(data,q+4);
                    if (data[(int)q+6] != 8) throw new NotSupportedException("Unsupported RPF9 block word size.");
                    var list = new List<Rpf9Block>();
                    long decoded = 0, stored = resource ? 16 : 0;
                    for (var j = 0; j < blocks; j++)
                    {
                        var block = Rpf9Block.Decode(U64(data,q+relative+j*8L),data[(int)q+7]);
                        decoded = checked(decoded + block.DecodedSize);
                        stored = checked(stored + ((block.CompressedSize+15)&~15L));
                        list.Add(block);
                    }
                    if (decoded != r.DecodedSize || stored > r.StoredSize) throw new InvalidDataException("RPF9 block totals disagree with file record.");
                    r.Blocks = list.AsReadOnly();
                }
                if (r.IsStored && r.CipherConfiguration == null && r.DecodedSize >= 64)
                {
                    source.Position = start + r.Offset;
                    r.IsStoredArchive = BitConverter.ToUInt32(ReadExactly(source,4),0) == Magic;
                }
                records.Add(r);
            }
            return new Rpf9Archive(start,length,records.AsReadOnly(),keyStore);
        }

        public void Extract(Stream source, Rpf9Record record, Stream destination)
        {
            if (record == null || record.Index < 0 || record.Index >= Records.Count || !ReferenceEquals(Records[record.Index],record)) throw new ArgumentException("Entry does not belong to this archive.");
            if (record.CompressionId != 0 && record.CompressionId != 1) throw new NotSupportedException("Unsupported RPF9 compression ID " + record.CompressionId);
            if (StartOffset > source.Length-Length) throw new EndOfStreamException("Truncated source archive.");
            var skip = record.IsResource ? 16 : 0;
            var extent = record.StoredSize-skip;
            if (extent < 0) throw new InvalidDataException("Truncated resource prefix.");
            Rpf9Cipher cipher = null;
            if (record.CipherConfiguration != null)
            {
                var config = record.CipherConfiguration;
                if (U16(config,4) != 17) throw new NotSupportedException("Unsupported RPF9 payload cipher.");
                cipher = keys.Cipher(U32(config,0),Slice(config,6,16));
            }
            var input = new byte[65536];
            var output = new byte[1024*1024];
            long position = 0, total = 0;
            Action<int> read = n =>
            {
                if (position > extent-n) throw new EndOfStreamException("Described block exceeds entry extent.");
                source.Position = StartOffset + record.Offset + skip + position;
                var got = 0;
                while (got < n) { var k=source.Read(input,got,n-got); if (k==0) throw new EndOfStreamException("Truncated RPF9 payload."); got+=k; }
                cipher?.SelectiveCtr(input,n,position,extent,record.CipherConfiguration);
                position += n;
            };
            if (record.Blocks == null)
            {
                if (!record.IsStored) throw new NotSupportedException("RPF9 compressed/resource entry has no supported block table.");
                while (total < record.DecodedSize)
                {
                    var n=(int)Math.Min(input.Length,record.DecodedSize-total);
                    read(n); destination.Write(input,0,n); total+=n;
                }
                return;
            }
            foreach (var block in record.Blocks)
            {
                var blockEnd = checked(position+block.CompressedSize);
                if (blockEnd > extent) throw new InvalidDataException("RPF9 block outside entry.");
                long decoded = 0;
                if (block.Storage == 0)
                {
                    if (block.CompressedSize != block.DecodedSize) throw new InvalidDataException("Stored block sizes disagree.");
                    while (position < blockEnd)
                    {
                        var n=(int)Math.Min(input.Length,blockEnd-position);
                        read(n); destination.Write(input,0,n); decoded+=n;
                    }
                }
                else if (block.Storage == 3)
                {
                    var inflater = new Inflater(record.CompressionId == 1);
                    while (!inflater.IsFinished)
                    {
                        if (inflater.IsNeedingInput)
                        {
                            if (position == blockEnd) throw new InvalidDataException("Truncated RPF9 compressed stream.");
                            var n=(int)Math.Min(input.Length,blockEnd-position); read(n); inflater.SetInput(input,0,n);
                        }
                        var written=inflater.Inflate(output);
                        if (written == 0 && !inflater.IsFinished && !inflater.IsNeedingInput) throw new InvalidDataException("Invalid RPF9 compressed stream or unsupported dictionary.");
                        decoded+=written;
                        if (decoded > block.DecodedSize) throw new InvalidDataException("RPF9 block exceeds decoded size.");
                        destination.Write(output,0,written);
                    }
                    if (position != blockEnd || inflater.RemainingInput != 0) throw new InvalidDataException("Trailing data in described RPF9 stream.");
                }
                else throw new NotSupportedException("Unsupported RPF9 block storage " + block.Storage);
                if (decoded != block.DecodedSize) throw new InvalidDataException("RPF9 decoded block size mismatch.");
                total += decoded;
                position=(position+15)&~15L;
            }
            if (total != record.DecodedSize) throw new InvalidDataException("RPF9 decoded file size mismatch.");
        }
    }
}
