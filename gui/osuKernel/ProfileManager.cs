using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace osuKernel
{
    public class TabletProfile
    {
        public string Name { get; set; } = "Default Profile";
        public double WidthMm { get; set; } = 152.0;
        public double HeightMm { get; set; } = 95.0;
        public double XMm { get; set; } = 76.0; // Center X in mm (matching OpenTabletDriver)
        public double YMm { get; set; } = 47.5; // Center Y in mm (matching OpenTabletDriver)
        public bool LockAspectRatio { get; set; } = false;
        public bool AreaClipping { get; set; } = true;
        public bool AreaLimiting { get; set; } = false;
        public int Rotation { get; set; } = 0;
        public bool LowLatencyKernelMode { get; set; } = true;
        public bool EnableTipClick { get; set; } = true;
        public bool ShowAreaHandles { get; set; } = true;
        public int ScreenX { get; set; } = 0;
        public int ScreenY { get; set; } = 0;
        public int ScreenWidth { get; set; } = 1920;
        public int ScreenHeight { get; set; } = 1080;

        // Aspect Ratio
        public string AspectRatioPreset { get; set; } = "Display";
        public double CustomRatioX { get; set; } = 16.0;
        public double CustomRatioY { get; set; } = 9.0;

        // Smoothing, Antichatter, and 8kHz Overclock / Interpolation
        public bool EnableSmoothing { get; set; } = false;
        public int SmoothingStrength { get; set; } = 25;
        public int AntichatterDeadzone { get; set; } = 0;
        public int InterpolationRate { get; set; } = 0; // 0 = Native, 1000, 2000, 4000, 8000
        public bool EnablePrediction { get; set; } = false;
        public double PredictionLookaheadMs { get; set; } = 1.5;
        public bool ShowPenCursor { get; set; } = true;
        public string TabletModel { get; set; } = "Auto";
    }

    /// <summary>
    /// Manages settings persistence via a portable .ini file next to the executable.
    /// Profiles are stored as JSON files in a "Profiles" subfolder next to the exe.
    /// Supports designated autoload preset on startup.
    /// </summary>
    public static class ProfileManager
    {
        public const string ConfigChangedEventName = "osuKernel_Config_Changed_Event";

        public static readonly string ExeDir;
        public static readonly string SettingsIniPath;
        public static readonly string ProfilesDir;

        public static bool DevMode { get; set; } = false;

        private static string _currentAutoloadPreset = "LastSession";

        static ProfileManager()
        {
            ExeDir = AppContext.BaseDirectory;
            SettingsIniPath = Path.Combine(ExeDir, "settings.ini");
            ProfilesDir = Path.Combine(ExeDir, "Profiles");

            if (!Directory.Exists(ProfilesDir))
            {
                Directory.CreateDirectory(ProfilesDir);
                CreateDefaultProfiles();
            }

            if (!File.Exists(SettingsIniPath))
            {
                SaveActiveSettings(new TabletProfile());
            }
        }

        private static void CreateDefaultProfiles()
        {
            // Preset 1: Full Area Drawing (Centered on 152x95 mm tablet)
            SaveProfile(new TabletProfile
            {
                Name = "Full Area (152×95)",
                WidthMm = 152.0,
                HeightMm = 95.0,
                XMm = 76.0,
                YMm = 47.5,
                LockAspectRatio = false,
                AreaClipping = true
            });

            // Preset 2: osu! Competitive Small (70x43.8 mm, centered matching OTD)
            SaveProfile(new TabletProfile
            {
                Name = "osu! Small (70×43.8)",
                WidthMm = 70.0,
                HeightMm = 43.75,
                XMm = 76.0,
                YMm = 47.5,
                LockAspectRatio = true,
                AreaClipping = true
            });

            // Preset 3: osu! Medium (90x56.3 mm, centered matching OTD)
            SaveProfile(new TabletProfile
            {
                Name = "osu! Medium (90×56.3)",
                WidthMm = 90.0,
                HeightMm = 56.25,
                XMm = 76.0,
                YMm = 47.5,
                LockAspectRatio = true,
                AreaClipping = true
            });

            // Preset 4: osu! Large (110x68.8 mm, centered matching OTD)
            SaveProfile(new TabletProfile
            {
                Name = "osu! Large (110×68.8)",
                WidthMm = 110.0,
                HeightMm = 68.75,
                XMm = 76.0,
                YMm = 47.5,
                LockAspectRatio = true,
                AreaClipping = true
            });
        }

        public static string[] GetAvailableProfiles()
        {
            if (!Directory.Exists(ProfilesDir)) return Array.Empty<string>();
            var files = Directory.GetFiles(ProfilesDir, "*.json");
            string[] names = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                names[i] = Path.GetFileNameWithoutExtension(files[i]);
            }
            Array.Sort(names);
            return names;
        }

        public static void SaveProfile(TabletProfile profile)
        {
            try
            {
                if (!Directory.Exists(ProfilesDir)) Directory.CreateDirectory(ProfilesDir);
                string sanitize = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars()));
                string path = Path.Combine(ProfilesDir, $"{sanitize}.json");
                string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public static TabletProfile? LoadProfile(string name)
        {
            try
            {
                string path = Path.Combine(ProfilesDir, $"{name}.json");
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<TabletProfile>(json);
                return profile;
            }
            catch
            {
                return null;
            }
        }

        public static bool DeleteProfile(string name)
        {
            try
            {
                string path = Path.Combine(ProfilesDir, $"{name}.json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    if (string.Equals(_currentAutoloadPreset, name, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentAutoloadPreset = "LastSession";
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string GetAutoloadPreset()
        {
            return _currentAutoloadPreset;
        }

        public static void SetAutoloadPreset(string presetName)
        {
            _currentAutoloadPreset = string.IsNullOrWhiteSpace(presetName) ? "LastSession" : presetName.Trim();
        }

        public static void OpenProfilesFolder()
        {
            try
            {
                if (!Directory.Exists(ProfilesDir)) Directory.CreateDirectory(ProfilesDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = ProfilesDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // ── INI-based active settings & autoload (portable, next to exe) ──

        public static void SaveActiveSettings(TabletProfile profile, string? autoloadPreset = null)
        {
            try
            {
                if (autoloadPreset != null)
                {
                    _currentAutoloadPreset = autoloadPreset;
                }

                var lines = new List<string>
                {
                    "# Wacom One (CTL-472) Hardware & Mapping Configuration",
                    "# Generated automatically - portable configuration file",
                    "",
                    "[Area]",
                    $"WidthMm={profile.WidthMm.ToString("0.##", CultureInfo.InvariantCulture)}",
                    $"HeightMm={profile.HeightMm.ToString("0.##", CultureInfo.InvariantCulture)}",
                    $"XMm={profile.XMm.ToString("0.##", CultureInfo.InvariantCulture)}",
                    $"YMm={profile.YMm.ToString("0.##", CultureInfo.InvariantCulture)}",
                    $"Rotation={profile.Rotation}",
                    $"LockAspectRatio={profile.LockAspectRatio}",
                    $"AspectRatioPreset={profile.AspectRatioPreset}",
                    $"CustomRatioX={profile.CustomRatioX.ToString("0.##", CultureInfo.InvariantCulture)}",
                    $"CustomRatioY={profile.CustomRatioY.ToString("0.##", CultureInfo.InvariantCulture)}",
                    $"AreaClipping={profile.AreaClipping}",
                    $"AreaLimiting={profile.AreaLimiting}",
                    $"ShowAreaHandles={profile.ShowAreaHandles}",
                    "",
                    "[Display]",
                    $"ScreenX={profile.ScreenX}",
                    $"ScreenY={profile.ScreenY}",
                    $"ScreenWidth={profile.ScreenWidth}",
                    $"ScreenHeight={profile.ScreenHeight}",
                    "",
                    "[Pen]",
                    $"EnableTipClick={(profile.EnableTipClick ? 1 : 0)}",
                    "",
                    "[Driver]",
                    $"LowLatencyKernelMode={profile.LowLatencyKernelMode}",
                    $"EnableSmoothing={profile.EnableSmoothing}",
                    $"SmoothingStrength={profile.SmoothingStrength}",
                    $"AntichatterDeadzone={profile.AntichatterDeadzone}",
                    $"InterpolationRate={profile.InterpolationRate}",
                    $"EnablePrediction={profile.EnablePrediction}",
                    $"PredictionLookaheadMs={profile.PredictionLookaheadMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    $"ShowPenCursor={profile.ShowPenCursor}",
                    "",
                    "[Developer]",
                    $"DevMode={(DevMode ? 1 : 0)}",
                    "",
                    "[Profile]",
                    $"Name={profile.Name}",
                    $"AutoloadPreset={_currentAutoloadPreset}",
                    $"TabletModel={profile.TabletModel}"
                };

                File.WriteAllLines(SettingsIniPath, lines);
                SignalConfigChanged();
            }
            catch { }
        }

        public static void SignalConfigChanged()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ConfigChangedEventName, out var handle))
                {
                    using (handle) handle.Set();
                }
            }
            catch { }
        }

        public static TabletProfile LoadStartupProfile()
        {
            var active = LoadActiveSettings();

            // If user designated a specific preset to autoload on boot, load that preset
            if (!string.IsNullOrWhiteSpace(_currentAutoloadPreset) &&
                !string.Equals(_currentAutoloadPreset, "LastSession", StringComparison.OrdinalIgnoreCase))
            {
                var preset = LoadProfile(_currentAutoloadPreset);
                if (preset != null)
                {
                    return preset;
                }
            }

            return active;
        }

        public static TabletProfile LoadActiveSettings()
        {
            var profile = new TabletProfile();

            try
            {
                if (!File.Exists(SettingsIniPath))
                {
                    var migrated = TryMigrateFromLegacyJson();
                    if (migrated != null) return migrated;
                    return profile;
                }

                var values = ParseIniFile(SettingsIniPath);

                if (values.TryGetValue("WidthMm", out string? wMm))
                    profile.WidthMm = ParseDouble(wMm, 152.0);
                if (values.TryGetValue("HeightMm", out string? hMm))
                    profile.HeightMm = ParseDouble(hMm, 95.0);
                if (values.TryGetValue("XMm", out string? xMm))
                    profile.XMm = ParseDouble(xMm, 76.0);
                if (values.TryGetValue("YMm", out string? yMm))
                    profile.YMm = ParseDouble(yMm, 47.5);
                if (values.TryGetValue("Rotation", out string? rot))
                    profile.Rotation = ParseInt(rot, 0);
                if (values.TryGetValue("LockAspectRatio", out string? lar))
                    profile.LockAspectRatio = ParseBool(lar, false);
                if (values.TryGetValue("AspectRatioPreset", out string? arp))
                    profile.AspectRatioPreset = arp;
                if (values.TryGetValue("CustomRatioX", out string? crx))
                    profile.CustomRatioX = ParseDouble(crx, 16.0);
                if (values.TryGetValue("CustomRatioY", out string? cry))
                    profile.CustomRatioY = ParseDouble(cry, 9.0);
                if (values.TryGetValue("AreaClipping", out string? ac))
                    profile.AreaClipping = ParseBool(ac, true);
                if (values.TryGetValue("AreaLimiting", out string? al))
                    profile.AreaLimiting = ParseBool(al, false);
                if (values.TryGetValue("ShowAreaHandles", out string? sah))
                    profile.ShowAreaHandles = ParseBool(sah, true);

                if (values.TryGetValue("ScreenX", out string? sx))
                    profile.ScreenX = ParseInt(sx, 0);
                if (values.TryGetValue("ScreenY", out string? sy))
                    profile.ScreenY = ParseInt(sy, 0);
                if (values.TryGetValue("ScreenWidth", out string? sw))
                    profile.ScreenWidth = ParseInt(sw, 1920);
                if (values.TryGetValue("ScreenHeight", out string? sh))
                    profile.ScreenHeight = ParseInt(sh, 1080);

                if (values.TryGetValue("EnableTipClick", out string? etc))
                    profile.EnableTipClick = ParseBool(etc, true);

                if (values.TryGetValue("DevMode", out string? dm))
                    DevMode = ParseBool(dm, false);

                if (values.TryGetValue("LowLatencyKernelMode", out string? llkm))
                    profile.LowLatencyKernelMode = ParseBool(llkm, true);
                if (values.TryGetValue("EnableSmoothing", out string? es))
                    profile.EnableSmoothing = ParseBool(es, false);
                if (values.TryGetValue("SmoothingStrength", out string? ss))
                    profile.SmoothingStrength = ParseInt(ss, 25);
                if (values.TryGetValue("AntichatterDeadzone", out string? ad))
                    profile.AntichatterDeadzone = ParseInt(ad, 0);
                if (values.TryGetValue("InterpolationRate", out string? ir))
                    profile.InterpolationRate = ParseInt(ir, 0);
                if (values.TryGetValue("EnablePrediction", out string? ep))
                    profile.EnablePrediction = ParseBool(ep, false);
                if (values.TryGetValue("PredictionLookaheadMs", out string? pl))
                    profile.PredictionLookaheadMs = ParseDouble(pl, 1.5);
                if (values.TryGetValue("ShowPenCursor", out string? spc))
                    profile.ShowPenCursor = ParseBool(spc, true);

                if (values.TryGetValue("Name", out string? name))
                    profile.Name = name;

                if (values.TryGetValue("AutoloadPreset", out string? autoPreset))
                    _currentAutoloadPreset = string.IsNullOrWhiteSpace(autoPreset) ? "LastSession" : autoPreset.Trim();

                if (values.TryGetValue("TabletModel", out string? tm))
                    profile.TabletModel = string.IsNullOrWhiteSpace(tm) ? "Auto" : tm.Trim();
            }
            catch { }

            return profile;
        }

        // ── INI parser ──

        private static Dictionary<string, string> ParseIniFile(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                result[key] = val;
            }
            return result;
        }

        private static double ParseDouble(string s, double fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            string norm = s.Trim().Replace(',', '.');
            return double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static bool ParseBool(string s, bool fallback)
        {
            if (bool.TryParse(s, out bool v)) return v;
            if (s == "1") return true;
            if (s == "0") return false;
            return fallback;
        }

        private static TabletProfile? TryMigrateFromLegacyJson()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string newPath = Path.Combine(localAppData, "osuKernel", "active_settings.json");
                string legacyPath = Path.Combine(localAppData, "CTL472Configurator", "active_settings.json");

                string targetPath = File.Exists(newPath) ? newPath : (File.Exists(legacyPath) ? legacyPath : string.Empty);
                if (string.IsNullOrEmpty(targetPath)) return null;

                string json = File.ReadAllText(targetPath);
                var profile = JsonSerializer.Deserialize<TabletProfile>(json);
                if (profile != null)
                {
                    SaveActiveSettings(profile);
                    return profile;
                }
            }
            catch { }

            return null;
        }
    }
}
