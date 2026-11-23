using CodeWalker;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using CodeWalker.Properties;
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
    static void ConfigurationChecks()
    {
        var folder=Path.Combine(Path.GetTempPath(),"cw-game-picker-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var oldFolder=GTAFolder.CurrentGTAFolder;
        var oldGame=GTAFolder.CurrentGame;
        var oldExe=GTAFolder.CurrentExecutablePath;
        var settingsFolder=Settings.Default.GTAFolder;
        var settingsGame=Settings.Default.GTASelectedGame;
        var settingsExe=Settings.Default.GTAExecutablePath;
        var settingsGen9=Settings.Default.GTAGen9;
        try
        {
            foreach (GtaGame game in Enum.GetValues(typeof(GtaGame)))
            {
                var executable=GTAFolder.GetExecutablePath(folder,game);
                File.WriteAllBytes(executable,new byte[0]);
                Require(GTAFolder.TryGetGameFromExecutable(executable,out var detected) && detected==game,"Executable/game mapping failed.");
            }
            Require(GTAFolder.ValidateGTAFolder(folder,false),"GTAV Legacy validation regressed.");
            Require(GTAFolder.ValidateGTAFolder(folder,true),"GTAV Enhanced validation regressed.");
            Require(!GTAFolder.ValidateGameFolder(folder,GtaGame.GtaVI,null,out var reason) && reason.Contains("SHA-256"),"Unsupported GTA 6 executable was accepted.");
            Require(!GTAFolder.ValidateGameFolder(folder,GtaGame.GtaVI,Path.Combine(folder,"GTA5.exe"),out reason),"GTAV executable accepted for GTA 6.");
            Require(GTAFolder.SetGameFolder(folder,GtaGame.GtaVLegacy,saveSettings:false),"Unable to set temporary GTAV profile.");
            using (var selector=new SelectFolderForm())
            {
                typeof(SelectFolderForm).GetMethod("SelectFolderForm_Load",Private).Invoke(selector,new object[] {null,EventArgs.Empty});
                Require(selector.TrySelectExecutable(Path.Combine(folder,"GTA5_Enhanced.exe"),out reason),"Executable picker rejected Enhanced.");
                Require(selector.SelectedGame==GtaGame.GtaVEnhanced && ((RadioButton)Field(selector,"EnhancedRadioButton")).Checked,"Picker did not select Enhanced.");
                ((RadioButton)Field(selector,"Gta6RadioButton")).Checked=true;
                Require(selector.SelectedGame==GtaGame.GtaVI && selector.SelectedExecutable.EndsWith("game_scarlett_master.exe"),"GTA 6 radio selection did not update executable.");
                Require(((Label)Field(selector,"ModeInfoLabel")).Text.Contains("read-only"),"GTA 6 capabilities missing from selector.");
                Require(!selector.TrySelectExecutable(Path.Combine(folder,"game_scarlett_master.exe"),out reason),"Picker accepted an unverified build.");
                ((RadioButton)Field(selector,"LegacyRadioButton")).Checked=true;
                Require(selector.SelectedGame==GtaGame.GtaVLegacy && !selector.IsGen9,"Switching back to Legacy retained GTA 6/Enhanced state.");
            }
            var readGame=typeof(GTAFolder).GetMethod("ReadSelectedGame",BindingFlags.Static|BindingFlags.NonPublic);
            Settings.Default.GTASelectedGame="";
            Settings.Default.GTAGen9=true;
            Require((GtaGame)readGame.Invoke(null,null)==GtaGame.GtaVEnhanced,"Old Enhanced settings migration failed.");
            Settings.Default.GTAGen9=false;
            Require((GtaGame)readGame.Invoke(null,null)==GtaGame.GtaVLegacy,"Old Legacy settings migration failed.");
            Settings.Default.GTASelectedGame="GtaVI";
            Require((GtaGame)readGame.Invoke(null,null)==GtaGame.GtaVI,"Saved GTA 6 selection was not restored.");
            Require(Settings.Default.GTAFolder==settingsFolder && Settings.Default.GTAExecutablePath==settingsExe,"Nonpersistent test changed profile paths.");
            Console.WriteLine("Game selection, executable mapping, unsupported-build rejection and GTAV settings migration passed.");
        }
        finally
        {
            Settings.Default.GTASelectedGame=settingsGame;
            Settings.Default.GTAGen9=settingsGen9;
            GTAFolder.SetGameFolder(oldFolder,oldGame,oldExe,saveSettings:false);
            Directory.Delete(folder,true);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { ConfigurationChecks(); }
        catch (Exception ex) {Console.Error.WriteLine(ex); return 1;}
        if (args.Length==1 && args[0]=="--configuration") return 0;
        if (args.Length!=1) {Console.Error.WriteLine("Pass --configuration, or a folder with common.rpf and the supported executable."); return 1;}
        using (var selector=new SelectFolderForm())
        {
            var executable=Path.Combine(args[0],"game_scarlett_master.exe");
            Require(selector.TrySelectExecutable(executable,out var reason),"GTA 6 executable selection failed: "+reason);
            Require(selector.SelectedGame==GtaGame.GtaVI,"Game executable did not select GTA 6.");
            var separateFolder=Path.Combine(Path.GetTempPath(),"cw-archive-folder-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(separateFolder);
            try
            {
                ((TextBox)Field(selector,"FolderTextBox")).Text=separateFolder;
                Require(selector.SelectedExecutable==Path.GetFullPath(executable),"Changing archives folder lost the chosen executable.");
                Require(GTAFolder.ValidateGameFolder(separateFolder,selector.SelectedGame,selector.SelectedExecutable,out reason),"Separate executable path was rejected: "+reason);
            }
            finally
            {
                ((TextBox)Field(selector,"FolderTextBox")).Text=Path.GetFullPath(args[0]);
                Directory.Delete(separateFolder);
            }
            Require(GTAFolder.SetGameFolder(selector.SelectedFolder,selector.SelectedGame,selector.SelectedExecutable,saveSettings:false),"GTA 6 profile validation failed.");
            Require(GTAFolder.IsGta6 && !GTAFolder.IsGen9,"GTA 6 was treated as GTAV Enhanced.");
            using (var bitmap=new System.Drawing.Bitmap(selector.Width,selector.Height))
            {
                selector.StartPosition=FormStartPosition.Manual;
                selector.Location=new System.Drawing.Point(-32000,-32000);
                selector.ShowInTaskbar=false;
                selector.Show();
                selector.PerformLayout();
                selector.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height));
                selector.Hide();
                Directory.CreateDirectory("artifacts");
                bitmap.Save("artifacts/gta6-game-selector.png");
            }
        }
        var result=1;
        using (var form=new ExploreForm()) //normal startup uses the selected GTA 6 profile, without --rpf9
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
                    Require(form.Text.Contains("GTA 6"),"Explorer title does not identify GTA 6.");
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
