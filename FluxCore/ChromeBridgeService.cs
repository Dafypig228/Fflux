using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FluxCore
{
    public class ChromePagePayload
    {
        [JsonPropertyName("url")]     public string    Url     { get; set; } = "";
        [JsonPropertyName("title")]   public string    Title   { get; set; } = "";
        [JsonPropertyName("text")]    public string    Text    { get; set; } = "";
        [JsonPropertyName("images")]  public string[]? Images  { get; set; }
    }

    public class VsCodePayload
    {
        [JsonPropertyName("source")]       public string   Source       { get; set; } = "";
        [JsonPropertyName("file")]         public string   File         { get; set; } = "";
        [JsonPropertyName("language")]     public string   Language     { get; set; } = "";
        [JsonPropertyName("cursorLine")]   public int      CursorLine   { get; set; }
        [JsonPropertyName("selectedText")] public string?  SelectedText { get; set; }
        [JsonPropertyName("visibleText")]  public string?  VisibleText  { get; set; }
        [JsonPropertyName("errors")]       public int      Errors       { get; set; }
        [JsonPropertyName("warnings")]     public int      Warnings     { get; set; }
    }

    /// <summary>Editor state received from the VS Code extension.</summary>
    public record VsCodeState(
        string   File,
        string   Language,
        int      CursorLine,
        string?  SelectedText,
        string?  VisibleText,
        int      Errors,
        int      Warnings,
        DateTime When);

    /// <summary>
    /// Hosts a local HTTP server that receives page context from the Davos Chrome extension.
    /// Extension POSTs DOM text + canvas-captured images to http://localhost:27834/.
    /// </summary>
    public class ChromeBridgeService : IDisposable
    {
        public const int Port = 27834;

        private HttpListener?  _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();

        private record PageData(string Url, string Title, string Text, string[] Images, DateTime When);
        private readonly Dictionary<string, PageData> _pages = new();

        /// <summary>Optional — set after construction to persist events to the data lake.</summary>
        public DataLakeService? DataLake { get; set; }

        /// <summary>Optional — set after construction to store chunked page text in semantic memory.</summary>
        public MemoryService? Memory { get; set; }

        public ChromeBridgeService()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();
                _ = ListenLoop();
            }
            catch { /* Port in use or no permission — skip silently */ }
        }

        private async Task ListenLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener!.GetContextAsync();
                    _ = HandleAsync(ctx);
                }
                catch (HttpListenerException) { break; }
                catch { }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                // CORS — Chrome extensions need this
                ctx.Response.Headers["Access-Control-Allow-Origin"]  = "*";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";

                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                    return;
                }

                using var reader  = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string    body    = await reader.ReadToEndAsync();

                // Peek at "source" field to route VS Code vs Chrome payloads
                using var doc = JsonDocument.Parse(body);
                bool isVsCode = doc.RootElement.TryGetProperty("source", out var src)
                                && src.GetString() == "vscode";

                if (isVsCode)
                {
                    var vsPayload = JsonSerializer.Deserialize<VsCodePayload>(body);
                    if (vsPayload != null && !string.IsNullOrEmpty(vsPayload.File))
                    {
                        var state = new VsCodeState(
                            vsPayload.File, vsPayload.Language, vsPayload.CursorLine,
                            vsPayload.SelectedText, vsPayload.VisibleText,
                            vsPayload.Errors, vsPayload.Warnings, DateTime.Now);
                        SetVsCodeState(state);
                        DataLake?.Write("vscode",
                            $"{vsPayload.File} [{vsPayload.Language}] Line:{vsPayload.CursorLine}",
                            new { file = vsPayload.File, language = vsPayload.Language,
                                  errors = vsPayload.Errors, warnings = vsPayload.Warnings });
                    }
                }
                else
                {
                    var payload = JsonSerializer.Deserialize<ChromePagePayload>(body);
                    if (payload != null && !string.IsNullOrEmpty(payload.Url))
                    {
                        var page = new PageData(
                            payload.Url, payload.Title, payload.Text,
                            payload.Images ?? Array.Empty<string>(), DateTime.Now);

                        lock (_lock)
                        {
                            _pages[payload.Url] = page;
                            // Keep only last 10 URLs to prevent unbounded growth
                            if (_pages.Count > 10)
                            {
                                var oldest = _pages.OrderBy(p => p.Value.When).First().Key;
                                _pages.Remove(oldest);
                            }
                        }

                        // Persist page text to data lake and semantic memory (chunked)
                        if (!string.IsNullOrEmpty(payload.Text))
                        {
                            long lakeId = DataLake?.Write("chrome", payload.Text,
                                new { url = payload.Url, title = payload.Title }) ?? 0;
                            if (Memory != null)
                                _ = Memory.SaveChunked(payload.Text, "chrome",
                                    lakeId > 0 ? lakeId : null);
                        }
                    }
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Close(); } catch { } }
        }

        /// <summary>Returns the most recently received page's text context.</summary>
        public string GetPageContext()
        {
            PageData? page;
            lock (_lock)
                page = _pages.Values.OrderByDescending(p => p.When).FirstOrDefault();

            if (page == null) return "";
            if ((DateTime.Now - page.When).TotalSeconds > 60) return ""; // Stale

            var sb = new StringBuilder();
            sb.AppendLine("=== CHROME PAGE ===");
            sb.AppendLine($"  URL: {page.Url}");
            sb.AppendLine($"  Title: {page.Title}");
            if (!string.IsNullOrEmpty(page.Text))
            {
                var truncated = page.Text.Length > 3000 ? page.Text[..3000] + "…" : page.Text;
                sb.AppendLine($"  Content: {truncated}");
            }
            if (page.Images.Length > 0)
                sb.AppendLine($"  [Images: {page.Images.Length} captured]");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the recently captured pages (up to 10) — the grounded answer to
        /// "what's open / what was I browsing in Chrome". Pages are captured by the
        /// Davos extension as the user browses; this is recent browsing history from
        /// the live session, not a tab enumeration. Exposed to RUN_CSHARP via
        /// ScriptGlobals.Chrome — without this, the model invented COM APIs
        /// (Chrome.Application) that don't exist.
        /// </summary>
        public string GetRecentPages(int maxAgeMinutes = 240)
        {
            List<PageData> pages;
            lock (_lock)
                pages = _pages.Values.OrderByDescending(p => p.When).ToList();

            if (pages.Count == 0)
                return "No pages captured — the Davos Chrome extension hasn't reported anything yet.";

            var sb = new StringBuilder();
            foreach (var p in pages)
            {
                var age = DateTime.Now - p.When;
                if (age.TotalMinutes > maxAgeMinutes) continue;
                sb.AppendLine($"[{age.TotalMinutes:F0}m ago] {p.Title} — {p.Url}");
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "No recently captured pages.";
        }

        /// <summary>Returns Base64 images from the current page for vision LLM input.</summary>
        public string[] GetPageImages()
        {
            lock (_lock)
            {
                var page = _pages.Values.OrderByDescending(p => p.When).FirstOrDefault();
                if (page == null || (DateTime.Now - page.When).TotalSeconds > 60)
                    return Array.Empty<string>();
                return page.Images;
            }
        }

        // VS Code context — populated in Phase F3 when the VS Code extension is built
        private VsCodeState? _vsCode;

        internal void SetVsCodeState(VsCodeState state) { lock (_lock) _vsCode = state; }

        /// <summary>Returns VS Code editor context (active file, cursor, diagnostics).</summary>
        public string GetVsCodeContext()
        {
            VsCodeState? s;
            lock (_lock) s = _vsCode;
            if (s == null || (DateTime.Now - s.When).TotalSeconds > 120) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== VS CODE ===");
            sb.AppendLine($"  File: {s.File} [{s.Language}]  Line:{s.CursorLine}");
            if (!string.IsNullOrEmpty(s.SelectedText))
                sb.AppendLine($"  Selected: {s.SelectedText[..Math.Min(200, s.SelectedText.Length)]}");
            if (s.Errors > 0 || s.Warnings > 0)
                sb.AppendLine($"  Diagnostics: {s.Errors} errors, {s.Warnings} warnings");
            if (!string.IsNullOrEmpty(s.VisibleText))
                sb.AppendLine($"  Visible:\n{s.VisibleText[..Math.Min(600, s.VisibleText.Length)]}");
            return sb.ToString();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener?.Stop();
        }
    }
}
