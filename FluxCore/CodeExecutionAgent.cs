using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// Code Execution Sandbox: Runs Python, PowerShell, and CMD commands.
    /// Also provides direct file writing capabilities.
    /// </summary>
    public class CodeExecutionAgent
    {
        private readonly string _tempDir;
        private const int MAX_OUTPUT_LENGTH = 5000;
        private const int TIMEOUT_MS = 30000; // 30 seconds

        public CodeExecutionAgent()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "FluxSandbox");
            Directory.CreateDirectory(_tempDir);
        }

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
                    source,                                         // Exact path
                    source + ".lnk",                               // Add .lnk extension
                    Path.Combine(publicDesktop, Path.GetFileName(source)),           // Try Public Desktop
                    Path.Combine(publicDesktop, Path.GetFileName(source) + ".lnk"),  // Public Desktop + .lnk
                    Path.Combine(userDesktop, Path.GetFileName(source)),             // User Desktop (if relative)
                    Path.Combine(userDesktop, Path.GetFileName(source) + ".lnk"),    // User Desktop + .lnk
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
                    // Provide detailed error showing what was actually tried
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

                File.Move(source, dest, true); // Overwrite allowed
                
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

                // FIX: AI might guess wrong Desktop path (e.g., C:\Users\adila\Desktop when OneDrive is used)
                string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");
                if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                    path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
                }

                // If path is relative, assume Desktop
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), path);

                // DEBUG: Log the actual path being used
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

        /// <summary>
        /// Copies a file to a new location with overwrite protection.
        /// </summary>
        public async Task<ExecutionResult> CopyFileAsync(string source, string dest, bool overwrite = false)
        {
            try
            {
                source = SanitizePath(source);
                dest = SanitizePath(dest);

                if (!File.Exists(source))
                    return new ExecutionResult(false, $"Source file not found: {source}");

                // If dest is a directory, append filename
                if (Directory.Exists(dest))
                    dest = Path.Combine(dest, Path.GetFileName(source));

                // Check for overwrite
                if (File.Exists(dest) && !overwrite)
                    return new ExecutionResult(false, $"Destination exists (use overwrite flag): {dest}");

                // Ensure dest directory exists
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

        /// <summary>
        /// Safely deletes a file by moving it to the Recycle Bin.
        /// </summary>
        public async Task<ExecutionResult> DeleteFileAsync(string path)
        {
            try
            {
                path = SanitizePath(path);

                if (!File.Exists(path) && !Directory.Exists(path))
                    return new ExecutionResult(false, $"File/folder not found: {path}");

                // Use Shell API to move to Recycle Bin (safe delete)
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

        /// <summary>
        /// Renames a file or folder with collision handling.
        /// </summary>
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

                // Handle collision
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

        /// <summary>
        /// Gets detailed file information.
        /// </summary>
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

        /// <summary>
        /// Reads text file content with encoding detection.
        /// </summary>
        public async Task<ExecutionResult> ReadFileAsync(string path, int maxLines = 100)
        {
            try
            {
                path = SanitizePath(path);

                if (!File.Exists(path))
                    return new ExecutionResult(false, $"File not found: {path}");

                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024) // 1MB limit
                    return new ExecutionResult(false, $"File too large ({FormatBytes(info.Length)}). Max 1MB.");

                // Try to read with auto-detected encoding
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
        /// Sanitizes a path - handles OneDrive, wrong usernames, relative paths.
        /// Also searches common directories if file not found.
        /// </summary>
        private string SanitizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == ".")
                return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            path = Environment.ExpandEnvironmentVariables(path);

            string actualProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string actualDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string actualDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string wrongDesktopPath = Path.Combine(actualProfile, "Desktop");

            // Handle common shortcuts
            if (path.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                return actualDesktop;
            if (path.Equals("Documents", StringComparison.OrdinalIgnoreCase))
                return actualDocuments;
            if (path.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(actualProfile, "Downloads");
            if (path.Equals("Pictures", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (path.Equals("Videos", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (path.Equals("Music", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            // Fix wrong username
            if (path.StartsWith(@"C:\Users\User", StringComparison.OrdinalIgnoreCase))
                path = path.Replace(@"C:\Users\User", actualProfile, StringComparison.OrdinalIgnoreCase);

            // Fix wrong Desktop path (OneDrive)
            if (!actualDesktop.Equals(wrongDesktopPath, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(wrongDesktopPath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace(wrongDesktopPath, actualDesktop, StringComparison.OrdinalIgnoreCase);
            }
            
            // Fix wrong Documents path (OneDrive)
            string wrongDocumentsPath = Path.Combine(actualProfile, "Documents");
            if (!actualDocuments.Equals(wrongDocumentsPath, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(wrongDocumentsPath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace(wrongDocumentsPath, actualDocuments, StringComparison.OrdinalIgnoreCase);
            }

            // Handle relative paths - try to find the file
            if (!Path.IsPathRooted(path))
            {
                // Search common locations for the file
                string[] searchLocations = new[]
                {
                    actualDesktop,
                    actualDocuments,
                    Path.Combine(actualProfile, "Downloads"),
                    @"C:\Users\Public\Desktop",
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                };

                foreach (var location in searchLocations)
                {
                    string candidate = Path.Combine(location, path);
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                        return candidate;
                    
                    // Try with .lnk extension for shortcuts
                    if (File.Exists(candidate + ".lnk"))
                        return candidate + ".lnk";
                }
                
                // Default to Desktop if not found
                path = Path.Combine(actualDesktop, path);
            }

            return path;
        }

        public static string GetCommonPathsInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("YOUR KNOWN PATHS (USE THESE EXACT PATHS):");
            sb.AppendLine($"  Desktop: {Environment.GetFolderPath(Environment.SpecialFolder.Desktop)}");
            sb.AppendLine($"  Documents: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}");
            sb.AppendLine($"  Downloads: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")}");
            sb.AppendLine($"  Pictures: {Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)}");
            sb.AppendLine($"  Public Desktop: C:\\Users\\Public\\Desktop");
            return sb.ToString();
        }

        /// <summary>
        /// SMART FILE DISCOVERY: Searches common directories to find a file by name.
        /// Returns the full path if found, null if not found.
        /// </summary>
        public string? SmartFindFile(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return null;

            // If it's already a valid full path, return it
            if (Path.IsPathRooted(filename) && (File.Exists(filename) || Directory.Exists(filename)))
                return filename;

            // Extract just the filename if a path was given
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
                    // Check root directory first (fast)
                    string directPath = Path.Combine(root, searchName);
                    if (File.Exists(directPath)) return directPath;
                    if (File.Exists(directPath + ".lnk")) return directPath + ".lnk";
                    if (Directory.Exists(directPath)) return directPath;

                    // Recursive search (depth limited for speed)
                    var found = Directory.EnumerateFiles(root, searchName, SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (found != null) return found;

                    // Try with .lnk
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

        /// <summary>
        /// SEARCH_FILES command: Searches for files matching a pattern across common directories.
        /// AI should use this BEFORE trying to operate on files.
        /// </summary>
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

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }

        // =========================================
        // DIRECT FILE OPERATIONS
        // =========================================

        /// <summary>
        /// Writes content directly to a file.
        /// </summary>
        public async Task<ExecutionResult> WriteFileAsync(string path, string content)
        {
            try
            {
                // Expand environment variables
                path = Environment.ExpandEnvironmentVariables(path);
                
                // Create directory if needed
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

        /// <summary>
        /// Copies text to clipboard.
        /// </summary>
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

        /// <summary>
        /// Downloads a file from URL.
        /// </summary>
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

        // =========================================
        // NODE.JS EXECUTION
        // =========================================

        /// <summary>
        /// Runs Node.js code and returns output.
        /// </summary>
        public async Task<ExecutionResult> RunNodeAsync(string code)
        {
            try
            {
                string tempFile = Path.Combine(_tempDir, $"script_{Guid.NewGuid():N}.js");
                await File.WriteAllTextAsync(tempFile, code);

                try
                {
                    string nodePath = FindNode();
                    if (string.IsNullOrEmpty(nodePath))
                        return new ExecutionResult(false, "Node.js not found. Install Node.js and add to PATH.");

                    return await RunProcessAsync(nodePath, $"\"{tempFile}\"");
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Node.js error: {ex.Message}");
            }
        }

        private string FindNode()
        {
            var paths = new[] { "node", "node.exe", @"C:\Program Files\nodejs\node.exe" };
            foreach (var path in paths)
            {
                try
                {
                    var psi = new ProcessStartInfo(path, "--version")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(2000);
                        if (proc.ExitCode == 0) return path;
                    }
                }
                catch { }
            }
            return "";
        }

        // =========================================
        // PYTHON EXECUTION
        // =========================================

        /// <summary>
        /// Runs Python code and returns output.
        /// </summary>
        public async Task<ExecutionResult> RunPythonAsync(string code)
        {
            try
            {
                // Create temp file
                string tempFile = Path.Combine(_tempDir, $"script_{Guid.NewGuid():N}.py");
                await File.WriteAllTextAsync(tempFile, code);

                try
                {
                    // Try to find Python
                    string pythonPath = FindPython();
                    if (string.IsNullOrEmpty(pythonPath))
                        return new ExecutionResult(false, "Python not found. Install Python and add to PATH.");

                    var result = await RunProcessAsync(pythonPath, $"\"{tempFile}\"");
                    return result;
                }
                finally
                {
                    // Cleanup
                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Python error: {ex.Message}");
            }
        }

        private string FindPython()
        {
            // Check common paths
            var paths = new[]
            {
                "python",
                "python3",
                @"C:\Python311\python.exe",
                @"C:\Python310\python.exe",
                @"C:\Python39\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python311\python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python310\python.exe"),
            };

            foreach (var path in paths)
            {
                try
                {
                    var psi = new ProcessStartInfo(path, "--version")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(2000);
                        if (proc.ExitCode == 0)
                            return path;
                    }
                }
                catch { }
            }
            return "";
        }

        // =========================================
        // POWERSHELL EXECUTION
        // =========================================

        /// <summary>
        /// Runs PowerShell commands and returns output.
        /// </summary>
        public async Task<ExecutionResult> RunPowerShellAsync(string command)
        {
            try
            {
                // CRITICAL: Use UTF-8 encoding for Cyrillic/Unicode support
                // Set BOTH input AND output encoding to UTF-8
                string utf8Prefix = "$OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";
                string fullCommand = utf8Prefix + command;
                
                // Use -EncodedCommand with Base64 to avoid encoding issues in arguments
                byte[] commandBytes = Encoding.Unicode.GetBytes(fullCommand);
                string encodedCommand = Convert.ToBase64String(commandBytes);
                
                return await RunProcessAsync("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"PowerShell error: {ex.Message}");
            }
        }

        // =========================================
        // CMD EXECUTION
        // =========================================

        /// <summary>
        /// Runs CMD commands and returns output.
        /// </summary>
        public async Task<ExecutionResult> RunCmdAsync(string command)
        {
            try
            {
                return await RunProcessAsync("cmd.exe", $"/c {command}");
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"CMD error: {ex.Message}");
            }
        }

        // =========================================
        // PROCESS RUNNER
        // =========================================

        private async Task<ExecutionResult> RunProcessAsync(string executable, string arguments)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };
                
                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var completed = await Task.Run(() => process.WaitForExit(TIMEOUT_MS));
                
                if (!completed)
                {
                    try { process.Kill(); } catch { }
                    return new ExecutionResult(false, "Execution timed out (30s limit)");
                }

                string resultOutput = output.ToString().Trim();
                string resultError = error.ToString().Trim();

                // Truncate if too long
                if (resultOutput.Length > MAX_OUTPUT_LENGTH)
                    resultOutput = resultOutput.Substring(0, MAX_OUTPUT_LENGTH) + "\n...[truncated]";

                if (process.ExitCode != 0 && !string.IsNullOrEmpty(resultError))
                    return new ExecutionResult(false, $"Exit code {process.ExitCode}: {resultError}");

                return new ExecutionResult(true, string.IsNullOrEmpty(resultOutput) ? "(no output)" : resultOutput);
            }
            catch (Exception ex)
            {
                return new ExecutionResult(false, $"Process error: {ex.Message}");
            }
        }
    }
}
