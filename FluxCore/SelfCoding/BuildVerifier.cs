using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FluxCore.SelfCoding
{
    public class BuildVerifier
    {
        private readonly string _projectRoot;

        public BuildVerifier(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public async Task<BuildResult> BuildAsync()
        {
            var csproj = Directory.GetFiles(_projectRoot, "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (csproj == null)
                return new BuildResult(false, new List<BuildError> { new("", 0, "No .csproj found") }, new());

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csproj}\" --no-restore",
                WorkingDirectory = _projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return new BuildResult(false, new List<BuildError> { new("", 0, "Failed to start dotnet build") }, new());

                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                string output = stdout + "\n" + stderr;
                var errors = ParseBuildErrors(output);
                var warnings = ParseBuildWarnings(output);

                bool success = process.ExitCode == 0;
                return new BuildResult(success, errors, warnings);
            }
            catch (Exception ex)
            {
                return new BuildResult(false, new List<BuildError> { new("", 0, $"Build exception: {ex.Message}") }, new());
            }
        }

        private List<BuildError> ParseBuildErrors(string output)
        {
            var errors = new List<BuildError>();
            var pattern = new Regex(@"(.+?)\((\d+),\d+\):\s*error\s+\w+:\s*(.+?)(?:\[|$)", RegexOptions.Multiline);

            foreach (Match match in pattern.Matches(output))
            {
                errors.Add(new BuildError(
                    match.Groups[1].Value.Trim(),
                    int.TryParse(match.Groups[2].Value, out int line) ? line : 0,
                    match.Groups[3].Value.Trim()));
            }
            return errors;
        }

        private List<string> ParseBuildWarnings(string output)
        {
            var warnings = new List<string>();
            var pattern = new Regex(@"warning\s+\w+:\s*(.+?)(?:\[|$)", RegexOptions.Multiline);

            foreach (Match match in pattern.Matches(output))
            {
                warnings.Add(match.Groups[1].Value.Trim());
            }
            return warnings;
        }
    }
}
