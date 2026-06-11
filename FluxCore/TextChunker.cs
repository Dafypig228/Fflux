using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FluxCore
{
    /// <summary>
    /// Splits text into overlapping chunks before embedding.
    /// Prevents vector meaning from being diluted in large blobs.
    /// Source-aware: Telegram messages are already per-message; Chrome text splits by paragraph.
    /// </summary>
    public static class TextChunker
    {
        private const int DefaultChunkSize = 400;
        private const int DefaultOverlap   = 50;

        /// <summary>
        /// Returns a list of chunks for the given content.
        /// Returns the original string as a single chunk if it fits within one chunk.
        /// </summary>
        public static List<string> Chunk(string content, string source)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new List<string>();
            if (content.Length <= DefaultChunkSize)
                return new List<string> { content };

            return source switch
            {
                "chrome"    => ChunkByParagraph(content),
                "vscode"    => ChunkByLineGroups(content),
                "telegram"  => new List<string> { content }, // already per-message
                _           => SlidingWindow(content, DefaultChunkSize, DefaultOverlap)
            };
        }

        // ── Strategies ────────────────────────────────────────────────────────────

        private static List<string> ChunkByParagraph(string text)
        {
            // Split on blank lines or sentence-terminal punctuation followed by uppercase
            var pieces = Regex.Split(text, @"\n\n+|(?<=[.!?])\s+(?=[A-Z])");
            return MergeAndSplit(pieces, DefaultChunkSize, DefaultOverlap);
        }

        private static List<string> ChunkByLineGroups(string text)
        {
            var lines = text.Split('\n');
            return MergeAndSplit(lines, DefaultChunkSize, DefaultOverlap);
        }

        private static List<string> MergeAndSplit(
            IEnumerable<string> pieces, int maxLen, int overlap)
        {
            var chunks  = new List<string>();
            var current = new StringBuilder();

            foreach (string raw in pieces)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;

                if (current.Length > 0 && current.Length + p.Length + 1 > maxLen)
                {
                    chunks.Add(current.ToString().Trim());
                    // Carry the tail for continuity
                    string tail = current.Length > overlap
                        ? current.ToString(current.Length - overlap, overlap)
                        : current.ToString();
                    current.Clear();
                    current.Append(tail).Append(' ');
                }
                current.Append(p).Append(' ');
            }

            if (current.Length > 0)
                chunks.Add(current.ToString().Trim());

            return chunks.Count > 0 ? chunks : new List<string> { string.Join(" ", pieces) };
        }

        private static List<string> SlidingWindow(string text, int size, int overlap)
        {
            var chunks = new List<string>();
            int step   = Math.Max(1, size - overlap);

            for (int i = 0; i < text.Length; i += step)
            {
                int len = Math.Min(size, text.Length - i);
                chunks.Add(text.Substring(i, len));
                if (i + len >= text.Length) break;
            }

            return chunks;
        }
    }
}
