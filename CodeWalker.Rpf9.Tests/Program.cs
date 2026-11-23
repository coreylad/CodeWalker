using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using CodeWalker.GameFiles;

static class Program
{
    static int assertions;
    static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is EndOfStreamException || e is ICSharpCode.SharpZipLib.SharpZipBaseException) { assertions++; return; }
        throw new Exception(message);
    }
    static byte[] Compress(byte[] bytes, bool raw)
    {
        using var output=new MemoryStream();
        using (Stream encoder=raw ? new DeflateStream(output,CompressionLevel.Optimal,true) : new ZLibStream(output,CompressionLevel.Optimal,true)) encoder.Write(bytes);
        return output.ToArray();
    }
    static byte[] Decode(byte[] encoded, byte[] decoded, int codec, long? compressed=null)
    {
        var record=new Rpf9Record { Index=0, StoredSize=encoded.Length, DecodedSize=decoded.Length, CompressionId=codec,
            Blocks=new[] {new Rpf9Block {CompressedSize=compressed ?? encoded.Length,DecodedSize=decoded.Length,Storage=3}} };
        var archive=new Rpf9Archive(0,encoded.Length,new[] {record},null);
        using var source=new MemoryStream(encoded);
        using var target=new MemoryStream();
        archive.Extract(source,record,target);
        return target.ToArray();
    }
    static void Synthetic()
    {
        var q=(12160UL>>4)|(1520UL<<28)|(4UL<<58)|(3UL<<62);
        var block=Rpf9Block.Decode(q,2);
        Check(block.CompressedSize==1516 && block.DecodedSize==12160 && block.Storage==3,"Format 2 length/padding");
        q=12345UL|(2352UL<<24)|(7UL<<52)|(3UL<<56);
        block=Rpf9Block.Decode(q,4);
        Check(block.CompressedSize==2345 && block.DecodedSize==12345 && block.Storage==3,"Format 4 lengths");
        var data=new byte[3*1024*1024+37]; new Random(73).NextBytes(data);
        foreach (var raw in new[] {false,true})
        {
            var encoded=Compress(data,raw);
            Check(Decode(encoded,data,raw?1:0).SequenceEqual(data),"Streaming codec round trip");
            Reject(()=>Decode(encoded[..^1],data,raw?1:0),"Truncated compressed stream accepted");
            Reject(()=>Decode(encoded.Concat(new byte[] {0}).ToArray(),data,raw?1:0),"Trailing block data accepted");
            if (!raw) { encoded[^1]^=1; Reject(()=>Decode(encoded,data,0),"Corrupted Adler checksum accepted"); }
        }
        var stored=new Rpf9Record { Index=0,Offset=1L<<35,StoredSize=32,DecodedSize=32 };
        using (var source=new SparseStream((1L<<35)+32))
        using (var output=new MemoryStream())
        {
            new Rpf9Archive(0,source.Length,new[] {stored},null).Extract(source,stored,output);
            Check(output.Length==32 && output.ToArray().All(b=>b==0),"64-bit byte offset truncated");
        }
        Reject(()=>Rpf9Archive.Read(new MemoryStream(new byte[64]),0,64,null),"Invalid magic accepted");
        var rpf=new RpfFile("synthetic.rpf","synthetic.rpf",64) { Version=Rpf9Archive.Magic };
        var dir=new RpfDirectoryEntry {File=rpf};
        var entry=new RpfBinaryFileEntry {File=rpf};
        Reject(()=>RpfFile.CreateFile(dir,"x",new byte[0]),"Create allowed");
        Reject(()=>RpfFile.CreateDirectory(dir,"x"),"Create directory allowed");
        Reject(()=>RpfFile.CreateNew(dir,"x.rpf"),"Create archive allowed");
        Reject(()=>RpfFile.DeleteEntry(entry),"Delete allowed");
        Reject(()=>RpfFile.RenameEntry(entry,"x"),"Rename allowed");
        Reject(()=>RpfFile.SetEncryptionType(rpf,RpfEncryption.OPEN),"Encryption conversion allowed");
        Reject(()=>RpfFile.Defragment(rpf),"Defragment allowed");
        Check(!RpfFile.EnsureValidEncryption(rpf,null),"Read-only encryption check");
        var temp=Path.Combine(Path.GetTempPath(),"cw-rpf7-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var legacy=RpfFile.CreateNew(temp+Path.DirectorySeparatorChar,"test.rpf");
            var payload=System.Text.Encoding.UTF8.GetBytes("RPF7 regression fixture");
            RpfFile.CreateFile(legacy.Root,"test.txt",payload);
            var scanned=new RpfFile(Path.Combine(temp,"test.rpf"),"test.rpf");
            scanned.ScanStructure(null,null);
            Check(scanned.LastException==null,"RPF7 scan regression: "+scanned.LastError);
            Check(scanned.ExtractFile(scanned.Root.Files.Single()).SequenceEqual(payload),"RPF7 extraction regression");
        }
        finally { Directory.Delete(temp,true); }
        Console.WriteLine($"Synthetic and RPF7 regression checks: {assertions} passed");
    }

    static void VerifyReport(string root, string reportPath, Rpf9KeyStore keys)
    {
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,reportPath)));
        var archivePath=report.RootElement.GetProperty("archive").GetString();
        using var source=File.OpenRead(archivePath);
        var archive=Rpf9Archive.Read(source,0,source.Length,keys);
        var checkedCount=0;
        foreach (var row in report.RootElement.GetProperty("recovered").EnumerateArray())
        {
            var i=row.GetProperty("index").GetInt32();
            using var sha=SHA256.Create();
            using (var sink=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write)) { archive.Extract(source,archive.Records[i],sink); sink.FlushFinalBlock(); }
            Check(Convert.ToHexString(sha.Hash).Equals(row.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase),archivePath+" entry "+i+" differs from Python result");
            checkedCount++;
        }
        Console.WriteLine($"Python SHA-256 parity: {Path.GetFileName(archivePath)} {checkedCount} entries");
    }
    static void Real(string root)
    {
        Rpf9KeyStore.ExecutablePath=Path.Combine(root,"game_scarlett_master.exe");
        var keys=Rpf9KeyStore.Load(Rpf9KeyStore.ExecutablePath);
        var reports=new[] {"common_entries","appdata_entries","shaders_entries","script_entries/000000_contents","script_entries/000001_contents"};
        foreach (var report in reports) VerifyReport(root,"rpf9_recovered/"+report+"/extraction_manifest.json",keys);
        foreach (var report in Directory.EnumerateFiles(Path.Combine(root,"rpf9_recovered/coverage"),"extraction_manifest.json",SearchOption.AllDirectories)) VerifyReport(root,report,keys);
        var script=new RpfFile(Path.Combine(root,"script.rpf"),"script.rpf");
        script.ScanStructure(null,Console.WriteLine);
        Check(script.LastException==null && script.Children.Count==2,"Nested RPF9 scan failed: "+script.LastError);
        foreach (var child in script.Children)
        {
            Check(child.IsRpf9 && child.IsReadOnly,"Child version/read-only status");
            var first=(Rpf9FileEntry)child.Root.Files.First();
            using var output=new MemoryStream();
            child.ExtractFile(first,output);
            Check(output.Length==first.Record.DecodedSize,"Nested byte-offset extraction");
        }
        Console.WriteLine($"All checks: {assertions} passed");
    }
    static int Main(string[] args)
    {
        try { Synthetic(); if (args.Length>0) Real(Path.GetFullPath(args[0])); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    sealed class SparseStream : Stream
    {
        readonly long length;
        internal SparseStream(long length) {this.length=length;}
        public override long Length=>length;
        public override long Position {get;set;}
        public override bool CanRead=>true;
        public override bool CanSeek=>true;
        public override bool CanWrite=>false;
        public override int Read(byte[] buffer,int offset,int count) {var n=(int)Math.Min(count,length-Position); Array.Clear(buffer,offset,n); Position+=n; return n;}
        public override long Seek(long offset,SeekOrigin origin)=>Position=origin==SeekOrigin.Begin?offset:origin==SeekOrigin.Current?Position+offset:length+offset;
        public override void Flush() {}
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
