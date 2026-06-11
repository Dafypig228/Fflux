using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FluxCore.InnerVoice
{
    /// <summary>
    /// Sends multi-part Telegram messages with human-like pacing.
    ///
    /// Instead of one wall of text, Davos splits longer responses into 2–3 natural
    /// parts, shows a typing indicator before each, waits proportional to message
    /// length (simulating actual typing speed ~60 WPM), then sends.
    ///
    /// Between parts there is a short pause (600–1500 ms) — like someone composing
    /// a follow-up thought, not copy-pasting a document.
    /// </summary>
    public class TelegramComposer
    {
        private readonly TelegramService _telegram;
        private readonly Random          _rng = new();

        public TelegramComposer(TelegramService telegram) => _telegram = telegram;

        /// <summary>
        /// Send a message to the owner chat with multi-part human pacing.
        /// If Telegram is not connected, the message is silently dropped.
        /// </summary>
        public async Task SendAsync(string text)
        {
            if (!_telegram.IsConnected) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            var parts = SplitNaturally(text, maxParts: 3);

            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i];
                if (string.IsNullOrWhiteSpace(part)) continue;

                // Show typing indicator
                await _telegram.SetTypingAsync();

                // Wait proportional to word count — ~60 WPM typing simulation
                int words     = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                int thinkMs   = Math.Clamp(words * 350, 800, 3000);
                await Task.Delay(thinkMs);

                // Send the part
                await _telegram.SendMessageAsync(part);

                // Pause between parts (composing next thought)
                if (i < parts.Count - 1)
                    await Task.Delay(_rng.Next(600, 1500));
            }
        }

        // ── Natural text splitting ────────────────────────────────────────────────

        /// <summary>
        /// Splits text at sentence boundaries (.!?) into at most <paramref name="maxParts"/> parts.
        /// Respects a ~160-char soft target per part (feels like a natural SMS/Telegram message).
        /// Falls back to whitespace splitting if no sentence boundaries are found.
        /// </summary>
        private static List<string> SplitNaturally(string text, int maxParts)
        {
            const int SoftCharLimit = 160;

            // Collect sentence fragments
            var sentences = new List<string>();
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?')
                {
                    // Include trailing punctuation and any immediately following spaces
                    int end = i + 1;
                    while (end < text.Length && text[end] == ' ') end++;

                    string sentence = text[start..end].Trim();
                    if (!string.IsNullOrEmpty(sentence))
                        sentences.Add(sentence);

                    start = end;
                }
            }

            // Anything remaining after the last punctuation
            if (start < text.Length)
            {
                string tail = text[start..].Trim();
                if (!string.IsNullOrEmpty(tail))
                    sentences.Add(tail);
            }

            // If no sentence boundaries found, treat whole text as one part
            if (sentences.Count == 0)
                return [text.Trim()];

            // Merge short sentences into chunks respecting SoftCharLimit and maxParts
            var parts   = new List<string>();
            var current = new System.Text.StringBuilder();

            foreach (var s in sentences)
            {
                if (parts.Count >= maxParts - 1)
                {
                    // Dump everything remaining into the last allowed part
                    if (current.Length > 0) current.Append(' ');
                    current.Append(s);
                    continue;
                }

                if (current.Length > 0 && current.Length + 1 + s.Length > SoftCharLimit)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                }

                if (current.Length > 0) current.Append(' ');
                current.Append(s);
            }

            if (current.Length > 0) parts.Add(current.ToString().Trim());

            return parts.Count > 0 ? parts : [text.Trim()];
        }
    }
}
