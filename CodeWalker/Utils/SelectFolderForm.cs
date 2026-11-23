using CodeWalker.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodeWalker.Utils
{
    public partial class SelectFolderForm : Form
    {

        public string SelectedFolder { get; set; }
        public GtaGame SelectedGame { get; set; }
        public string SelectedExecutable { get; set; }
        public bool IsGen9 { get { return SelectedGame == GtaGame.GtaVEnhanced; } set { SelectedGame = value ? GtaGame.GtaVEnhanced : GtaGame.GtaVLegacy; } }
        public DialogResult Result { get; set; } = DialogResult.Cancel;
        private bool loading;
        private bool executableExplicit;

        public SelectFolderForm()
        {
            InitializeComponent();
            SelectedFolder = GTAFolder.CurrentGTAFolder;
            SelectedGame = GTAFolder.CurrentGame;
            SelectedExecutable = GTAFolder.CurrentExecutablePath;
        }

        private void SelectFolderForm_Load(object sender, EventArgs e)
        {
            executableExplicit = !string.Equals(SelectedExecutable, GTAFolder.GetExecutablePath(SelectedFolder, SelectedGame), StringComparison.OrdinalIgnoreCase);
            ApplySelection();
            RememberFolderCheckbox.Checked = Settings.Default.RememberGTAFolder;
        }

        private void FolderBrowseButton_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog.SelectedPath = FolderTextBox.Text;
            DialogResult res = FolderBrowserDialog.ShowDialogNew();
            if (res == DialogResult.OK)
            {
                var path = FolderBrowserDialog.SelectedPath;
                var game = GTAFolder.DetectGame(path, SelectedGame);
                var keepExecutable = executableExplicit && game == GtaGame.GtaVI && SelectedGame == game;
                SelectedFolder = path;
                SelectedGame = game;
                if (!keepExecutable) { SelectedExecutable = GTAFolder.GetExecutablePath(path, game); executableExplicit = false; }
                ApplySelection();
            }
        }

        private void FolderTextBox_TextChanged(object sender, EventArgs e)
        {
            SelectedFolder = FolderTextBox.Text;
            if (!loading && !executableExplicit)
            {
                SelectedExecutable = GTAFolder.GetExecutablePath(SelectedFolder, SelectedGame);
                ExecutableTextBox.Text = SelectedExecutable;
            }
        }

        private void GameRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (loading || !((RadioButton)sender).Checked) return;
            SelectedGame = Gta6RadioButton.Checked ? GtaGame.GtaVI : EnhancedRadioButton.Checked ? GtaGame.GtaVEnhanced : GtaGame.GtaVLegacy;
            SelectedExecutable = GTAFolder.GetExecutablePath(SelectedFolder, SelectedGame);
            executableExplicit = false;
            ApplySelection();
        }

        private void ApplySelection()
        {
            loading = true;
            FolderTextBox.Text = SelectedFolder;
            LegacyRadioButton.Checked = SelectedGame == GtaGame.GtaVLegacy;
            EnhancedRadioButton.Checked = SelectedGame == GtaGame.GtaVEnhanced;
            Gta6RadioButton.Checked = SelectedGame == GtaGame.GtaVI;
            ExecutableTextBox.Text = SelectedExecutable ?? GTAFolder.GetExecutablePath(SelectedFolder, SelectedGame);
            ExecutableLabel.Text = "Executable (" + GTAFolder.GetExecutableName(SelectedGame) + "):";
            ModeInfoLabel.Text = SelectedGame == GtaGame.GtaVI
                ? "GTA 6 supports read-only archive browsing and extraction. World and game resource viewers are not yet supported."
                : "Select the game folder containing the executable for the chosen GTAV edition.";
            loading = false;
        }

        public bool TrySelectExecutable(string path, out string failReason)
        {
            if (!GTAFolder.TryGetGameFromExecutable(path, out GtaGame game))
            {
                failReason = "Select GTA5.exe, GTA5_Enhanced.exe or game_scarlett_master.exe.";
                return false;
            }
            var fullPath = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(fullPath);
            if (!GTAFolder.ValidateGameFolder(folder, game, fullPath, out failReason)) return false;
            SelectedGame = game;
            SelectedFolder = folder;
            SelectedExecutable = fullPath;
            executableExplicit = true;
            ApplySelection();
            return true;
        }

        private void ExecutableBrowseButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Select game executable",
                Filter = "Supported game executables|game_scarlett_master.exe;GTA5.exe;GTA5_Enhanced.exe|Executable files (*.exe)|*.exe",
                FileName = SelectedExecutable,
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    if (!TrySelectExecutable(dialog.FileName, out string reason))
                        MessageBox.Show(this, reason, "Unsupported game executable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { Cursor = Cursors.Default; }
            }
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if(!GTAFolder.ValidateGameFolder(SelectedFolder, SelectedGame, SelectedExecutable, out string failReason))
            {
                MessageBox.Show("The selected folder could not be used:\n\n" + failReason, "Invalid game folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Result = DialogResult.OK;
            Close();
        }

        private void RememberFolderCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            Settings.Default.RememberGTAFolder = RememberFolderCheckbox.Checked;
        }
    }
}
