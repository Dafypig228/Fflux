using System;

namespace FluxCore
{
    /// <summary>
    /// Output truncation that never destroys the END of a command's output.
    /// A script prints its conclusion last — head-only truncation fed the model
    /// partial evidence and it fabricated the missing result (memory-usage task
    /// transcript, 2026-06-12). Both truncation layers (CodeExecutionAgent and
    /// JarvisCore step context) must keep head + tail.
    /// </summary>
    internal static class OutputTrim
    {
        /// <summary>
        /// Keeps the first <paramref name="head"/> and last <paramref name="tail"/> chars,
        /// cutting the middle with an explicit marker the model is taught to understand.
        /// Returns the string unchanged if it already fits.
        /// </summary>
        internal static string Middle(string s, int head, int tail)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= head + tail) return s;
            int omitted = s.Length - head - tail;
            return s[..head] +
                   $"\n…[{omitted} chars omitted from the MIDDLE of this output — beginning and END are shown]…\n" +
                   s[^tail..];
        }
    }
}
