using CodeWalker;
using CodeWalker.GameFiles;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Windows-only integration smoke test. Pass a folder containing ONLY common.rpf
// and the verified executable. It never edits either input; its test export is removed.
static class Program
{
    static readonly BindingFlags Private = BindingFlags.Instance|BindingFlags.NonPublic;
    static object Field(object target,string name) { return target.GetType().GetField(name,Private).GetValue(target); }
    static void Require(bool condition,string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length!=1) {Console.Error.WriteLine("Pass a folder with common.rpf and the supported executable."); return 1;}
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var result=1;
        using (var form=new ExploreForm(args[0]))
        using (var timer=new Timer {Interval=100})
        {
            var watch=Stopwatch.StartNew();
            form.Shown+=(s,e)=>form.Hide();
            timer.Tick+=(s,e)=>
            {
                if (!(bool)Field(form,"Ready") && watch.Elapsed < TimeSpan.FromMinutes(2)) return;
                timer.Stop();
                string export=null;
                try
                {
                    Require((bool)Field(form,"Ready"),"Explorer scan timed out.");
                    var rpfs=(IList)typeof(ExploreForm).GetProperty("AllRpfs",Private).GetValue(form);
                    Require(rpfs.Count==1,"Expected only common.rpf in the test folder.");
                    var archive=(RpfFile)rpfs[0];
                    Require(archive.IsRpf9 && archive.Root.Files.Count==418,"RPF9 listing did not contain 418 entries.");
                    Require(!form.EditMode,"RPF9 started in edit mode.");
                    var root=(MainTreeFolder)Field(form,"RootFolder");
                    var common=root.Children.Single(c=>c.Name=="common.rpf");
                    typeof(ExploreForm).GetMethod("Navigate",Private|BindingFlags.Public,null,new[] {typeof(MainTreeFolder)},null).Invoke(form,new object[] {common});
                    var files=(IList)Field(form,"CurrentFiles");
                    Require(files.Count==418,"Navigation did not populate the file list.");
                    var first=files[0];
                    var fullPath=(string)first.GetType().GetProperty("FullPath").GetValue(first);
                    Require(fullPath.StartsWith(archive.FilePath,StringComparison.OrdinalIgnoreCase),"RPF9 virtual path uses the GTAV folder.");
                    export=Path.Combine(args[0],"cw-test-"+Guid.NewGuid().ToString("N")+".bin");
                    var saver=typeof(ExploreForm).GetMethod("TrySaveRpf9",Private);
                    Require((bool)saver.Invoke(form,new[] {first,export}),"RPF9 streamed export was not handled.");
                    Require(Encoding.UTF8.GetString(File.ReadAllBytes(export)).StartsWith("<?xml"),"Export is not decrypted XML.");
                    var protectedSource=false;
                    try {saver.Invoke(form,new[] {first,archive.FilePath});}
                    catch (TargetInvocationException ex) when (ex.InnerException is IOException) {protectedSource=true;}
                    Require(protectedSource,"Export over source archive was allowed.");
                    Console.WriteLine("Desktop RPF9 scan, 418-entry navigation, virtual paths, streamed XML export and source protection passed.");
                    result=0;
                }
                catch (Exception ex) {Console.Error.WriteLine(ex);}
                finally {if (export!=null && File.Exists(export)) File.Delete(export); form.Close();}
            };
            timer.Start();
            Application.Run(form);
        }
        return result;
    }
}
