using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FluxCore
{
    public partial class CodeExecutionAgent
    {
        // =========================================
        // FILE SYSTEM MANAGEMENT (Clean Desktop)
        // =========================================

        public async Task<ExecutionResult> ListFilesAsync(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path == ".")
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

                path = Environment.ExpandEnvironmentVariables(path);

                // FIX: AI often guesses "C:\Users\User", replace with actual profile
                string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (path.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);
                }

                // FIX: AI might guess wrong Desktop path (e.g., C:\Users\adila\Desktop when OneDrive is used)
                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                    path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                // If path is just "Desktop", fix it
                if (path.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

                // DEBUG: Log the actual path being used
                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
                File.AppendAllText(debugPath, $"[LIST] Path: {path}\n");

                var sb = new StringBuilder();

                // Check if this is the Desktop - if so, also include Public Desktop
                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string publicDesktop = @"C:\Users\Public\Desktop";
                bool isDesktopQuery = path.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) ||
                                      path.EndsWith("Desktop", StringComparison.OrdinalIgnoreCase);

                if (isDesktopQuery)
                {
                    sb.AppendLine($"=== DESKTOP CONTENTS ===");
                    sb.AppendLine($"★★★ USE THESE EXACT FULL PATHS FOR MOVE_FILE! ★★★");
                    sb.AppendLine();

                    // User Desktop
                    if (Directory.Exists(userDesktop))
                    {
                        var userDirs = Directory.GetDirectories(userDesktop);
                        var userFiles = Directory.GetFiles(userDesktop);
                        sb.AppendLine($"--- User Desktop ---");
                        foreach (var d in userDirs.Take(20)) sb.AppendLine($"[DIR]  {d}");
                        foreach (var f in userFiles.Take(50)) sb.AppendLine($"[FILE] {f}");
                    }

                    // Public Desktop (shared shortcuts)
                    if (Directory.Exists(publicDesktop))
                    {
                        var pubDirs = Directory.GetDirectories(publicDesktop);
                        var pubFiles = Directory.GetFiles(publicDesktop);
                        if (pubDirs.Length > 0 || pubFiles.Length > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"--- Public Desktop (shared shortcuts) ---");
                            foreach (var d in pubDirs.Take(20)) sb.AppendLine($"[DIR]  {d}");
                            foreach (var f in pubFiles.Take(50)) sb.AppendLine($"[FILE] {f}");
                        }
                    }

                    sb.AppendLine();
                    sb.AppendLine("TIP: Copy the FULL path from above when using MOVE_FILE.");

                    return new ExecutionResult(true, sb.ToString());
                }

                // Standard directory listing for non-Desktop paths
                if (!Directory.Exists(path)) return new ExecutionResult(false, $"Directory not found: {path}");

                var files = Directory.GetFiles(path);
                var dirs = Directory.GetDirectories(path);

                sb.AppendLine($"Contents of {path}:");
                foreach (var d in dirs.Take(20)) sb.AppendLine($"[DIR]  {Path.GetFileName(d)}");
                foreach (var f in files.Take(50)) sb.AppendLine($"[FILE] {Path.GetFileName(f)}");

                if (files.Length > 50) sb.AppendLine($"... and {files.Length - 50} more files.");

                return new ExecutionResult(true, sb.ToString());
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"List failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> MoveFileAsync(string source, string dest)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || source == ".") source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

                source = Environment.ExpandEnvironmentVariables(source);
                dest = Environment.ExpandEnvironmentVariables(dest);

                string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (source.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                    source = source.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);
                if (dest.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                    dest = dest.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);

                // FIX: AI might guess wrong Desktop path (e.g., C:\Users\adila\Desktop when OneDrive is used)
                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (source.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                        source = source.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                    if (dest.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                        dest = dest.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                // If source is relative, assume Desktop
                if (!Path.IsPathRooted(source))
                    source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), source);

                // If dest is relative, assume Desktop
                if (!Path.IsPathRooted(dest))
                    dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), dest);

                // DEBUG: Log the actual paths being used
                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
                File.AppendAllText(debugPath, $"[MOVE] Source: {source} | Dest: {dest}\n");

                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string publicDesktop = @"C:\Users\Public\Desktop";

                // Smart file resolution: try multiple locations and extensions
                string? resolvedSource = null;
                string[] candidatePaths = new[]
                {
                    source,
                    source + ".lnk",
                    Path.Combine(publicDesktop, Path.GetFileName(source)),
                    Path.Combine(publicDesktop, Path.GetFileName(source) + ".lnk"),
                    Path.Combine(userDesktop, Path.GetFileName(source)),
                    Path.Combine(userDesktop, Path.GetFileName(source) + ".lnk"),
                };

                foreach (var candidate in candidatePaths)
                {
                    if (File.Exists(candidate))
                    {
                        resolvedSource = candidate;
                        if (candidate != source)
                        {
                            File.AppendAllText(debugPath, $"[MOVE] Resolved to: {candidate}\n");
                        }
                        break;
                    }
                }

                if (resolvedSource == null)
                {
                    File.AppendAllText(debugPath, $"[MOVE] FAILED - Tried: {string.Join(", ", candidatePaths)}\n");
                    var triedPaths = candidatePaths.Select(p => $"  - {p}").ToList();
                    return new ExecutionResult(false,
                        $"File not found. Tried these locations:\n{string.Join("\n", triedPaths)}\n" +
                        $"TIP: Use LIST_FILES to get exact paths before moving.");
                }

                source = resolvedSource;

                // If dest is a directory, append filename
                if (Directory.Exists(dest))
                    dest = Path.Combine(dest, Path.GetFileName(source));

                // Ensure dest dir exists
                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.Move(source, dest, true);

                return new ExecutionResult(true, $"Moved to: {dest}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Move failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> MakeDirAsync(string path)
        {
            try
            {
                path = Environment.ExpandEnvironmentVariables(path);

                string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (path.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                    path = path.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);

                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                    path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), path);

                string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FluxDebug.txt");
                File.AppendAllText(debugPath, $"[MKDIR] Path: {path}\n");

                Directory.CreateDirectory(path);
                return new ExecutionResult(true, $"Created directory: {path}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"MkDir failed: {ex.Message}");
            }
        }

        // =========================================
        // TIER 1: ESSENTIAL FILE OPERATIONS
        // =========================================

        public async Task<ExecutionResult> CopyFileAsync(string source, string dest, bool overwrite = false)
        {
            try
            {
                source = SanitizePath(source);
                dest = SanitizePath(dest);

                if (!File.Exists(source))
                    return new ExecutionResult(false, $"Source file not found: {source}");

                if (Directory.Exists(dest))
                    dest = Path.Combine(dest, Path.GetFileName(source));

                if (File.Exists(dest) && !overwrite)
                    return new ExecutionResult(false, $"Destination exists (use overwrite flag): {dest}");

                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.Copy(source, dest, overwrite);
                return new ExecutionResult(true, $"Copied to: {dest}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Copy failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> DeleteFileAsync(string path)
        {
            try
            {
                path = SanitizePath(path);

                if (!File.Exists(path) && !Directory.Exists(path))
                    return new ExecutionResult(false, $"File/folder not found: {path}");

                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                return new ExecutionResult(true, $"Moved to Recycle Bin: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Delete failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> RenameFileAsync(string path, string newName)
        {
            try
            {
                path = SanitizePath(path);

                if (!File.Exists(path) && !Directory.Exists(path))
                    return new ExecutionResult(false, $"File/folder not found: {path}");

                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) dir = ".";

                string newPath = Path.Combine(dir, newName);

                if (File.Exists(newPath) || Directory.Exists(newPath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(newName);
                    string ext = Path.GetExtension(newName);
                    int counter = 1;
                    while (File.Exists(newPath) || Directory.Exists(newPath))
                    {
                        newPath = Path.Combine(dir, $"{baseName} ({counter}){ext}");
                        counter++;
                    }
                }

                if (File.Exists(path))
                    File.Move(path, newPath);
                else
                    Directory.Move(path, newPath);

                return new ExecutionResult(true, $"Renamed to: {Path.GetFileName(newPath)}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Rename failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> GetFileInfoAsync(string path)
        {
            try
            {
                path = SanitizePath(path);

                if (!File.Exists(path) && !Directory.Exists(path))
                    return new ExecutionResult(false, $"File/folder not found: {path}");

                var sb = new StringBuilder();

                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    sb.AppendLine($"=== FILE INFO: {info.Name} ===");
                    sb.AppendLine($"Full Path: {info.FullName}");
                    sb.AppendLine($"Size: {FormatBytes(info.Length)}");
                    sb.AppendLine($"Created: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Accessed: {info.LastAccessTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Attributes: {info.Attributes}");
                    sb.AppendLine($"Extension: {info.Extension}");
                    sb.AppendLine($"Read-Only: {info.IsReadOnly}");
                }
                else
                {
                    var info = new DirectoryInfo(path);
                    var files = info.GetFiles("*", SearchOption.AllDirectories);
                    var dirs = info.GetDirectories("*", SearchOption.AllDirectories);
                    long totalSize = files.Sum(f => f.Length);

                    sb.AppendLine($"=== FOLDER INFO: {info.Name} ===");
                    sb.AppendLine($"Full Path: {info.FullName}");
                    sb.AppendLine($"Total Size: {FormatBytes(totalSize)}");
                    sb.AppendLine($"Files: {files.Length}");
                    sb.AppendLine($"Subfolders: {dirs.Length}");
                    sb.AppendLine($"Created: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                }

                return new ExecutionResult(true, sb.ToString());
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Info failed: {ex.Message}");
            }
        }

        public async Task<ExecutionResult> ReadFileAsync(string path, int maxLines = 100)
        {
            try
            {
                path = SanitizePath(path);

                if (!File.Exists(path))
                    return new ExecutionResult(false, $"File not found: {path}");

                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024)
                    return new ExecutionResult(false, $"File too large ({FormatBytes(info.Length)}). Max 1MB.");

                string content = await File.ReadAllTextAsync(path);
                var lines = content.Split('\n');

                var sb = new StringBuilder();
                sb.AppendLine($"=== {Path.GetFileName(path)} ({lines.Length} lines) ===");

                int linesToShow = Math.Min(lines.Length, maxLines);
                for (int i = 0; i < linesToShow; i++)
                {
                    sb.AppendLine(lines[i].TrimEnd('\r'));
                }

                if (lines.Length > maxLines)
                    sb.AppendLine($"\n... ({lines.Length - maxLines} more lines)");

                return new ExecutionResult(true, sb.ToString());
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Read failed: {ex.Message}");
            }
        }

        /// <summary>
        /// SMART FILE DISCOVERY: Searches common directories to find a file by name.
        /// </summary>
        public string? SmartFindFile(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return null;

            if (Path.IsPathRooted(filename) && (File.Exists(filename) || Directory.Exists(filename)))
                return filename;

            string searchName = Path.GetFileName(filename);

            string[] searchRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                @"C:\Users\Public\Desktop",
            };

            foreach (var root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;

                try
                {
                    string directPath = Path.Combine(root, searchName);
                    if (File.Exists(directPath)) return directPath;
                    if (File.Exists(directPath + ".lnk")) return directPath + ".lnk";
                    if (Directory.Exists(directPath)) return directPath;

                    var found = Directory.EnumerateFiles(root, searchName, SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (found != null) return found;

                    if (!searchName.EndsWith(".lnk"))
                    {
                        found = Directory.EnumerateFiles(root, searchName + ".lnk", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (found != null) return found;
                    }
                }
                catch { /* Access denied to some folders - continue */ }
            }

            return null;
        }

        public async Task<ExecutionResult> SearchFilesAsync(string pattern)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    return new ExecutionResult(false, "Usage: SEARCH_FILES:filename or pattern (e.g., *.pdf, notes*)");

                var results = new List<string>();

                string[] searchRoots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    @"C:\Users\Public\Desktop",
                };

                foreach (var root in searchRoots)
                {
                    if (!Directory.Exists(root)) continue;

                    try
                    {
                        var files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                            .Take(20)
                            .ToList();
                        results.AddRange(files);

                        var dirs = Directory.EnumerateDirectories(root, pattern, SearchOption.AllDirectories)
                            .Take(10)
                            .ToList();
                        results.AddRange(dirs);
                    }
                    catch { /* Access denied - continue */ }

                    if (results.Count >= 30) break;
                }

                if (results.Count == 0)
                    return new ExecutionResult(false, $"No files found matching '{pattern}'. Try a different pattern or check the filename.");

                var sb = new StringBuilder();
                sb.AppendLine($"=== SEARCH RESULTS for '{pattern}' ({results.Count} found) ===");
                sb.AppendLine("USE THESE EXACT PATHS for file operations:");
                foreach (var r in results.Take(30))
                {
                    bool isDir = Directory.Exists(r);
                    sb.AppendLine($"  {(isDir ? "[DIR]" : "[FILE]")} {r}");
                }

                return new ExecutionResult(true, sb.ToString());
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Search failed: {ex.Message}");
            }
        }

        // =========================================
        // DIRECT FILE OPERATIONS
        // =========================================

        public async Task<ExecutionResult> WriteFileAsync(string path, string content)
        {
            try
            {
                path = Environment.ExpandEnvironmentVariables(path);

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(path, content, Encoding.UTF8);

                return new ExecutionResult(true, $"File written: {path} ({content.Length} chars)");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Write failed: {ex.Message}");
            }
        }

        // =========================================
        // CLIPBOARD
        // =========================================

        public ExecutionResult SetClipboard(string text)
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetText(text);
                });
                return new ExecutionResult(true, $"Copied to clipboard: {text.Substring(0, Math.Min(50, text.Length))}...");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Clipboard failed: {ex.Message}");
            }
        }

        // =========================================
        // DOWNLOAD FILE
        // =========================================

        public async Task<ExecutionResult> DownloadFileAsync(string url, string savePath)
        {
            try
            {
                savePath = Environment.ExpandEnvironmentVariables(savePath);

                string? dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(60);

                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var bytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(savePath, bytes);

                return new ExecutionResult(true, $"Downloaded: {savePath} ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Download failed: {ex.Message}");
            }
        }
    }
}
