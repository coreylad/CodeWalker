using CodeWalker.GameFiles;
using System;
using System.IO;
using System.Windows.Forms;

namespace CodeWalker
{
    public partial class ExploreForm
    {
        private readonly string Rpf9Folder;
        internal string ExplorerRootPath { get { return Rpf9Folder == null ? GTAFolder.GetCurrentGTAFolderWithTrailingSlash() : Rpf9Folder.TrimEnd('\\','/') + "\\"; } }

        private void AddRpf9Menu()
        {
            var menu = new ToolStripMenuItem("Open RPF9 folder...");
            menu.Click += (s,e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description="Select the RPF9 archive folder (with the supported game executable)." })
                    if (dialog.ShowDialog(this) == DialogResult.OK) new ExploreForm(dialog.SelectedPath).Show();
            };
            FileMenu.DropDownItems.Insert(0,menu);
        }

        // Returns true when this is an RPF9 export. Stage before replacing an existing target.
        private bool TrySaveRpf9(MainListItem item, string path)
        {
            var entry = item.File ?? item.Folder?.RpfFile?.ParentFileEntry;
            var rootArchive = item.Folder?.RpfFile;
            if (!(entry is Rpf9FileEntry) && !(entry == null && rootArchive?.IsRpf9 == true)) return false;
            var target = System.IO.Path.GetFullPath(path);
            var source = System.IO.Path.GetFullPath(entry?.File.GetPhysicalFilePath() ?? rootArchive.GetPhysicalFilePath());
            if (string.Equals(target,source,StringComparison.OrdinalIgnoreCase)) throw new IOException("Cannot export over the source archive.");
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp,FileMode.CreateNew,FileAccess.Write))
                {
                    if (entry != null) entry.File.ExtractFile(entry,output);
                    else using (var input = File.OpenRead(source)) input.CopyTo(output);
                }
                if (File.Exists(target)) File.Replace(temp,target,null);
                else File.Move(temp,target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return true;
        }

        private bool SaveSelectedRpf9(MainListItem item)
        {
            if (!(item.File is Rpf9FileEntry) && item.Folder?.RpfFile?.IsRpf9 != true) return false;
            SaveFileDialog.FileName=item.Name;
            if (SaveFileDialog.ShowDialog(this) == DialogResult.OK)
                try { TrySaveRpf9(item,SaveFileDialog.FileName); }
                catch (Exception ex) { MessageBox.Show("Unable to extract RPF9 entry: " + ex.Message); }
            return true;
        }
    }
}
