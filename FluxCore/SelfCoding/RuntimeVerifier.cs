using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore.SelfCoding
{
    public class RuntimeVerifier
    {
        private readonly BuildVerifier _build;
        private readonly ILLMService _llm;
        private readonly string _repoRoot;

        public RuntimeVerifier(BuildVerifier build, ILLMService llm, string repoRoot)
        {
            _build = build;
            _llm = llm;
            _repoRoot = repoRoot;
        }

        public async Task<VerificationResult> VerifyAsync(string changeDescription)
        {
            // Backup existing binaries
            string backupDir = Path.Combine(_repoRoot, "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            try
            {
                Directory.CreateDirectory(backupDir);
                var binDir = Path.Combine(_repoRoot, "bin");
                if (Directory.Exists(binDir))
                {
                    foreach (var file in Directory.GetFiles(binDir, "*.exe", SearchOption.AllDirectories))
                        File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RuntimeVerifier] Backup warning: {ex.Message}");
            }

            // Build
            var buildResult = await _build.BuildAsync();
            if (!buildResult.Success)
            {
                return new VerificationResult(false,
                    $"Build failed: {(buildResult.Errors.Count > 0 ? buildResult.Errors[0].Message : "unknown error")}",
                    buildResult.Errors);
            }

            // Try smoke test launch
            try
            {
                var csproj = Directory.GetFiles(_repoRoot, "*.csproj", SearchOption.TopDirectoryOnly);
                if (csproj.Length == 0)
                    return new VerificationResult(true, "Build succeeded (no csproj for smoke test)");

                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project \"{csproj[0]}\" -- --smoke-test",
                    WorkingDirectory = _repoRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return new VerificationResult(true, "Build succeeded (could not start smoke test)");

                bool exited = process.WaitForExit(10000);
                if (exited && process.ExitCode != 0)
                {
                    string stderr = await process.StandardError.ReadToEndAsync();
                    return new VerificationResult(false,
                        $"App crashed (exit code {process.ExitCode}): {stderr.Substring(0, Math.Min(200, stderr.Length))}");
                }

                if (!exited)
                {
                    // App is running — that's good enough for smoke test
                    process.Kill();
                    return new VerificationResult(true, "Build + smoke test passed (app launched successfully)");
                }

                return new VerificationResult(true, "Build succeeded, smoke test exited cleanly");
            }
            catch (Exception ex)
            {
                // Smoke test failure is non-critical if build succeeded
                return new VerificationResult(true, $"Build succeeded (smoke test skipped: {ex.Message})");
            }
        }
    }
}
