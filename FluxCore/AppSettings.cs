using System;
using System.IO;
using System.Text.Json;

namespace FluxCore
{
    /// <summary>
    /// Persistent settings for FluxCore UI.
    /// Saved to %APPDATA%\FluxCore\settings.json
    /// </summary>
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FluxCore", "settings.json");

        // Window appearance
        public double WindowOpacity { get; set; } = 0.95;
        public double BlurRadius { get; set; } = 10;
        
        // Window position (optional - for remembering location)
        public double WindowTop { get; set; } = 100;
        public double WindowLeft { get; set; } = 100;
        
        // Behavior
        public bool RequireWakeWord { get; set; } = true;
        public string WakeWord { get; set; } = "Fluxoria";
        public bool AutoMinimizeOnComplete { get; set; } = true;
        
        /// <summary>
        /// Load settings from disk, or return defaults if file doesn't exist.
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Load error: {ex.Message}");
            }
            return new AppSettings();
        }

        /// <summary>
        /// Save settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save error: {ex.Message}");
            }
        }
    }
}
