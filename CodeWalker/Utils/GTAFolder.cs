using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Windows.Forms;
using CodeWalker.Properties;
using Microsoft.Win32;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace CodeWalker
{
    public enum GtaGame
    {
        GtaVLegacy,
        GtaVEnhanced,
        GtaVI
    }

    public static class GTAFolder
    {
        public static string CurrentGTAFolder { get; private set; } = Settings.Default.GTAFolder;
        public static GtaGame CurrentGame { get; private set; } = ReadSelectedGame();
        public static bool IsGen9 { get { return CurrentGame == GtaGame.GtaVEnhanced; } }
        public static bool IsGta6 { get { return CurrentGame == GtaGame.GtaVI; } }
        public static string CurrentExecutablePath { get; private set; } = string.IsNullOrWhiteSpace(Settings.Default.GTAExecutablePath)
            ? GetExecutablePath(CurrentGTAFolder, CurrentGame) : Settings.Default.GTAExecutablePath;

        private static GtaGame ReadSelectedGame()
        {
            if (Enum.TryParse(Settings.Default.GTASelectedGame, out GtaGame game) && Enum.IsDefined(typeof(GtaGame), game)) return game;
            return Settings.Default.GTAGen9 ? GtaGame.GtaVEnhanced : GtaGame.GtaVLegacy;
        }

        public static string GetExecutableName(GtaGame game)
        {
            switch (game)
            {
                case GtaGame.GtaVLegacy: return "GTA5.exe";
                case GtaGame.GtaVEnhanced: return "GTA5_Enhanced.exe";
                case GtaGame.GtaVI: return "game_scarlett_master.exe";
                default: throw new ArgumentOutOfRangeException(nameof(game));
            }
        }

        public static string GetExecutablePath(string folder, GtaGame game) => Path.Combine(folder ?? "", GetExecutableName(game));

        public static bool TryGetGameFromExecutable(string executable, out GtaGame game)
        {
            foreach (GtaGame candidate in Enum.GetValues(typeof(GtaGame)))
                if (string.Equals(Path.GetFileName(executable), GetExecutableName(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    game = candidate;
                    return true;
                }
            game = GtaGame.GtaVLegacy;
            return false;
        }

        public static GtaGame DetectGame(string folder, GtaGame preferred = GtaGame.GtaVLegacy)
        {
            if (File.Exists(GetExecutablePath(folder, preferred))) return preferred;
            if (File.Exists(GetExecutablePath(folder, GtaGame.GtaVI))) return GtaGame.GtaVI;
            if (IsGen9Folder(folder)) return GtaGame.GtaVEnhanced;
            return File.Exists(GetExecutablePath(folder, GtaGame.GtaVLegacy)) ? GtaGame.GtaVLegacy : preferred;
        }

        public static bool IsGen9Folder(string folder)
        {
            return File.Exists(folder + @"\gta5_enhanced.exe");
        }

        public static bool ValidateGTAFolder(string folder, bool gen9, out string failReason)
            => ValidateGameFolder(folder, gen9 ? GtaGame.GtaVEnhanced : GtaGame.GtaVLegacy, null, out failReason);

        public static bool ValidateGameFolder(string folder, GtaGame game, string executable, out string failReason)
        {
            failReason = "";

            if(string.IsNullOrWhiteSpace(folder))
            {
                failReason = "No folder specified";
                return false;
            }

            if(!Directory.Exists(folder))
            {
                failReason = $"Folder \"{folder}\" does not exist";
                return false;
            }

            if (!Enum.IsDefined(typeof(GtaGame), game))
            {
                failReason = "Unknown game selection";
                return false;
            }
            executable = string.IsNullOrWhiteSpace(executable) ? GetExecutablePath(folder, game) : executable;
            if (!string.Equals(Path.GetFileName(executable), GetExecutableName(game), StringComparison.OrdinalIgnoreCase))
            {
                failReason = $"Select {GetExecutableName(game)} for this game.";
                return false;
            }
            if (!File.Exists(executable))
            {
                failReason = $"{GetExecutableName(game)} not found: \"{executable}\"";
                return false;
            }
            if (game != GtaGame.GtaVI && !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(GetExecutablePath(folder, game)), StringComparison.OrdinalIgnoreCase))
            {
                failReason = "The GTAV executable must be inside the selected game folder.";
                return false;
            }
            if (game == GtaGame.GtaVI)
            {
                try { Rpf9KeyStore.Load(executable); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    failReason = ex.Message;
                    return false;
                }
            }

            return true;
        }

        public static bool ValidateGTAFolder(string folder, bool gen9) => ValidateGTAFolder(folder, gen9, out string reason);

        public static bool IsCurrentGTAFolderValid() => ValidateGameFolder(CurrentGTAFolder, CurrentGame, CurrentExecutablePath, out string reason);

        public static bool UpdateGTAFolder(bool useCurrentIfValid = false, bool autoDetect = true)
        {
            if (useCurrentIfValid && IsCurrentGTAFolderValid())
            {
                return true;
            }

            var game = CurrentGame;
            var executable = CurrentExecutablePath;
            string origFolder = CurrentGTAFolder;
            string folder = CurrentGTAFolder;
            SelectFolderForm f = new SelectFolderForm();

            if (autoDetect)
            {
                string autoFolder = AutoDetectFolder(out string source);
                if (autoFolder != null && MessageBox.Show($"Auto-detected game folder \"{autoFolder}\" from {source}.\n\nContinue with auto-detected folder?", "Auto-detected game folder", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                {
                    f.SelectedFolder = autoFolder;
                    f.SelectedGame = autoFolder == CurrentGTAFolder ? CurrentGame : DetectGame(autoFolder, CurrentGame);
                    f.SelectedExecutable = autoFolder == CurrentGTAFolder ? CurrentExecutablePath : GetExecutablePath(autoFolder, f.SelectedGame);
                }
            }

            f.ShowDialog();
            if (f.Result == DialogResult.Cancel)
            {
                return false;
            }
            if (f.Result == DialogResult.OK && Directory.Exists(f.SelectedFolder))
            {
                folder = f.SelectedFolder;
                game = f.SelectedGame;
                executable = f.SelectedExecutable;
            }

            string failReason;
            if (ValidateGameFolder(folder, game, executable, out failReason))
            {
                SetGameFolder(folder, game, executable);
                if (folder != origFolder)
                {
                    MessageBox.Show($"Successfully changed GTA Folder to \"{folder}\"", "Set GTA Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return true;
            }
            else
            {
                var tryAgain = MessageBox.Show($"Folder \"{folder}\" is not a valid GTA folder:\n\n{failReason}\n\nDo you want to try choosing a different folder?", "Unable to set GTA Folder", MessageBoxButtons.RetryCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (tryAgain == DialogResult.Retry)
                {
                    return UpdateGTAFolder(false);
                }
                else
                {
                    return false;
                }
            }
        }

        public static bool SetGTAFolder(string folder, bool gen9)
            => SetGameFolder(folder, gen9 ? GtaGame.GtaVEnhanced : GtaGame.GtaVLegacy);

        public static bool SetGameFolder(string folder, GtaGame game, string executable = null, bool saveSettings = true)
        {
            if(ValidateGameFolder(folder, game, executable, out string reason))
            {
                CurrentGTAFolder = Path.GetFullPath(folder);
                CurrentGame = game;
                CurrentExecutablePath = Path.GetFullPath(string.IsNullOrWhiteSpace(executable) ? GetExecutablePath(folder, game) : executable);
                Rpf9KeyStore.ExecutablePath = IsGta6 ? CurrentExecutablePath : null;
                if (saveSettings)
                {
                    Settings.Default.GTAFolder = CurrentGTAFolder;
                    Settings.Default.GTAGen9 = IsGen9;
                    Settings.Default.GTASelectedGame = game.ToString();
                    Settings.Default.GTAExecutablePath = CurrentExecutablePath;
                    Settings.Default.Save();
                }
                return true;
            }

            return false;
        }

        public static string GetCurrentGTAFolderWithTrailingSlash() =>CurrentGTAFolder.EndsWith(@"\") ? CurrentGTAFolder : CurrentGTAFolder + @"\";

        public static bool AutoDetectFolder(out Dictionary<string, string> matches)
        {
            matches = new Dictionary<string, string>();

            if(IsCurrentGTAFolderValid())
            {
                matches.Add("Current CodeWalker Folder", CurrentGTAFolder);
            }

            RegistryKey baseKey32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            string steamPathValue = baseKey32.OpenSubKey(@"Software\Rockstar Games\GTAV")?.GetValue("InstallFolderSteam") as string;
            string retailPathValue = baseKey32.OpenSubKey(@"Software\Rockstar Games\Grand Theft Auto V")?.GetValue("InstallFolder") as string;
            string oivPathValue = Registry.CurrentUser.OpenSubKey(@"Software\NewTechnologyStudio\OpenIV.exe\BrowseForFolder")?.GetValue("game_path_Five_pc") as string;

            if(steamPathValue?.EndsWith("\\GTAV") == true)
            {
                steamPathValue = steamPathValue.Substring(0, steamPathValue.LastIndexOf("\\GTAV"));
            }

            if(ValidateGTAFolder(steamPathValue, false))
            {
                matches.Add("Steam", steamPathValue);
            }

            if(ValidateGTAFolder(retailPathValue, false))
            {
                matches.Add("Retail", retailPathValue);
            }

            if(ValidateGTAFolder(oivPathValue, false))
            {
                matches.Add("OpenIV", oivPathValue);
            }

            return matches.Count > 0;
        }

        public static string AutoDetectFolder(out string source)
        {
            source = null;

            if(AutoDetectFolder(out Dictionary<string, string> matches))
            {
                var match = matches.First();
                source = match.Key;
                return match.Value;
            }

            return null;
        }

        public static string AutoDetectFolder() => AutoDetectFolder(out string _);

        public static void UpdateSettings()
        {
            if (IsGta6) return;
            if (string.IsNullOrEmpty(Settings.Default.Key) && (GTA5Keys.PC_AES_KEY != null))
            {
                Settings.Default.Key = Convert.ToBase64String(GTA5Keys.PC_AES_KEY);
                Settings.Default.Save();
            }
        }

        public static string GetEnhancedFormTitle(string t)
        {
            return t + " (GTAV Enhanced)";
        }
        public static void UpdateEnhancedFormTitle(Form form)
        {
            if (IsGta6) form.Text += " (GTA 6 - RPF9 read-only)";
            else if (IsGen9) form.Text = GetEnhancedFormTitle(form.Text);
        }
    }
}
