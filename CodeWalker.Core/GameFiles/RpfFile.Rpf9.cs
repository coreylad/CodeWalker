using System;
using System.IO;
using System.Collections.Generic;

namespace CodeWalker.GameFiles
{
    public partial class RpfFile
    {
        public bool IsRpf9 { get { return Version == Rpf9Archive.Magic; } }
        public bool IsReadOnly { get { return IsRpf9 || (Parent != null && Parent.IsReadOnly); } }
        public Rpf9Archive Rpf9 { get; private set; }

        private void EnsureWritable()
        {
            if (IsReadOnly) throw new NotSupportedException("RPF9 archives and their descendants are read-only. Editing, repacking and encryption conversion are not supported.");
        }

        private void ReadRpf9Header(BinaryReader reader)
        {
            Rpf9 = Rpf9Archive.Read(reader.BaseStream, StartPos, FileSize, Rpf9KeyStore.ForArchive(GetPhysicalFilePath()));
            Encryption = RpfEncryption.RPF9;
            EntryCount = checked((uint)Rpf9.Records.Count + 1);
            NamesLength = 0;
            IsAESEncrypted = IsNGEncrypted = false;
            Root = new RpfDirectoryEntry { File=this, Name="", NameLower="", Path=Path };
            AllEntries = new List<RpfEntry> { Root };
            TotalFileCount = TotalBinaryFileCount = (uint)Rpf9.Records.Count;
            TotalFolderCount = 1;
            TotalResourceCount = 0;
            foreach (var record in Rpf9.Records)
            {
                // Names/hierarchy are unknown. Never manufacture GTAV resource flags.
                var name = record.Index.ToString("D6") + (record.IsStoredArchive ? ".rpf" : ".bin");
                var entry = new Rpf9FileEntry
                {
                    Record=record, File=this, Parent=Root, Name=name, NameLower=name,
                    Path=Path+"\\"+name, NameHash=JenkHash.GenHash(name), ShortNameHash=JenkHash.GenHash(record.Index.ToString("D6")),
                    FileSize=(uint)Math.Min(record.StoredSize,uint.MaxValue),
                    FileUncompressedSize=(uint)Math.Min(record.DecodedSize,uint.MaxValue),
                    IsEncrypted=record.CipherConfiguration != null
                };
                Root.Files.Add(entry);
                AllEntries.Add(entry);
                if (record.IsResource) { TotalResourceCount++; TotalBinaryFileCount--; }
            }
            Root.EntriesCount = (uint)Root.Files.Count;
            CurrentFileReader = null;
        }

        /// <summary>Extract to a caller-owned stream, including RPF9 entries larger than 2 GiB.</summary>
        public void ExtractFile(RpfFileEntry entry, Stream destination)
        {
            if (entry == null || entry.File != this) throw new ArgumentException("Entry does not belong to this archive.");
            if (destination == null || !destination.CanWrite) throw new ArgumentException("Output must be writable.");
            if (entry is Rpf9FileEntry rpf9)
            {
                using (var source = System.IO.File.OpenRead(GetPhysicalFilePath())) Rpf9.Extract(source,rpf9.Record,destination);
                return;
            }
            var data = ExtractFile(entry);
            if (data == null) throw new InvalidDataException(LastError ?? "Unable to extract file.");
            destination.Write(data,0,data.Length);
        }
    }

    public sealed class Rpf9FileEntry : RpfBinaryFileEntry
    {
        public Rpf9Record Record { get; internal set; }
        public override long GetFileSize() { return Record.StoredSize; }
        public override void SetFileSize(uint size) { throw new NotSupportedException("RPF9 entries are read-only."); }
        public override void Write(DataWriter writer) { throw new NotSupportedException("RPF9 entries cannot be written as RPF7 records."); }
    }
}
