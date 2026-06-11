using System;
using System.IO;

namespace FluxCore
{
    /// <summary>
    /// Appends every user/assistant turn to a daily plain-text file:
    ///   %APPDATA%\Davos\chat_logs\YYYY-MM-DD.txt
    /// Easy to open in Notepad or any text editor.
    /// </summary>
    internal static class ChatLogger
    {
        private static readonly string _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Davos", "chat_logs");

        public static void Log(string text, bool isUser)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string file  = Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd}.txt");
                string label = isUser ? "You" : "Davos";
                string line  = $"[{DateTime.Now:HH:mm:ss}] {label}: {text}{Environment.NewLine}";
                File.AppendAllText(file, line);
            }
            catch { /* never crash the app over logging */ }
        }

        /// <summary>
        /// Writes a diagnostic error line so you can see exactly why a reply failed.
        /// Format:  [HH:mm:ss] ERROR (source): detail
        /// </summary>
        public static void LogError(string source, string detail)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string file = Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd}.txt");
                // Truncate huge raw API blobs so the log stays readable
                if (detail.Length > 500) detail = detail[..500] + "…";
                string line = $"[{DateTime.Now:HH:mm:ss}] ERROR ({source}): {detail}{Environment.NewLine}";
                File.AppendAllText(file, line);
            }
            catch { }
        }
    }
}
