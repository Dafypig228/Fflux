using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore.SelfCoding
{
    public class PlannerAgent
    {
        private readonly ILLMService _proModel;
        private readonly string _projectRoot;

        public PlannerAgent(ILLMService proModel, string projectRoot)
        {
            _proModel = proModel;
            _projectRoot = projectRoot;
        }

        public async Task<ImplementationPlan?> GeneratePlanAsync(string request)
        {
            string projectContext = GetProjectContext(request);

            string prompt = $@"You are an expert C# developer planning a change to the Davos project.

PROJECT STRUCTURE:
{projectContext}

USER REQUEST: {request}

Generate an implementation plan as JSON:
{{
  ""summary"": ""..."",
  ""steps"": [{{ ""targetFile"": ""path"", ""changeType"": ""add/modify/delete"", ""description"": ""..."", ""codeSketch"": ""..."" }}],
  ""filesToModify"": [...],
  ""filesToCreate"": [...],
  ""riskLevel"": ""low/medium/high"",
  ""estimatedImpact"": ""...""
}}

Return ONLY the JSON, no markdown fences.";

            string response = await _proModel.GenerateText(prompt, temperature: 0.4f);

            try
            {
                response = StripMarkdownFences(response);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<ImplementationPlan>(response, options);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Planner] Parse error: {ex.Message}");
                return null;
            }
        }

        public async Task<ImplementationPlan?> RevisePlanAsync(ImplementationPlan plan, DeliberationResult[] critiques)
        {
            string critiquesSummary = string.Join("\n", critiques.Select(c =>
                $"{c.Dimension} (score {c.Score:F1}): {string.Join("; ", c.Issues)}"));

            string prompt = $@"Revise this plan based on deliberator feedback:

PLAN: {JsonSerializer.Serialize(plan)}

CRITIQUES:
{critiquesSummary}

Return revised plan as JSON (same format). No markdown fences.";

            string response = await _proModel.GenerateText(prompt, temperature: 0.4f);

            try
            {
                response = StripMarkdownFences(response);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<ImplementationPlan>(response, options);
            }
            catch
            {
                return plan; // Return original if revision fails
            }
        }

        private string GetProjectContext(string userRequest)
        {
            try
            {
                // TIER 1: File names + line counts (~500 tokens)
                var allFiles = Directory.GetFiles(_projectRoot, "*.cs", SearchOption.AllDirectories);
                var fileList = allFiles.Select(f =>
                    $"  {Path.GetRelativePath(_projectRoot, f)} ({CountLines(f)} lines)");

                // TIER 2: Relevant files' method signatures (~1000 tokens)
                var keywords = ExtractKeywords(userRequest);
                var relevantFiles = allFiles.Where(f =>
                    keywords.Any(k => File.ReadAllText(f).Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .Take(10);

                var signatures = relevantFiles.Select(f =>
                {
                    var content = File.ReadAllText(f);
                    var methods = Regex.Matches(content,
                        @"(public|private|protected|internal)\s+(static\s+)?(async\s+)?\w+[\w<>\[\],\s]*\s+\w+\s*\([^)]*\)")
                        .Cast<Match>().Select(m => m.Value);
                    return $"// {Path.GetFileName(f)}:\n{string.Join("\n", methods.Take(15))}";
                });

                // TIER 3: Full content of tiny files (<50 lines)
                var tinyFiles = allFiles.Where(f => CountLines(f) < 50)
                    .Take(5)
                    .Select(f => $"// {Path.GetFileName(f)} (full):\n{File.ReadAllText(f)}");

                return $"FILES:\n{string.Join("\n", fileList)}\n\nRELEVANT:\n{string.Join("\n", signatures)}\n\nSMALL FILES:\n{string.Join("\n", tinyFiles)}";
            }
            catch (Exception ex)
            {
                return $"[Error reading project: {ex.Message}]";
            }
        }

        private static string[] ExtractKeywords(string text)
        {
            return text.ToLower()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .ToArray();
        }

        private static int CountLines(string filePath)
        {
            try { return File.ReadAllLines(filePath).Length; }
            catch { return 0; }
        }

        private static string StripMarkdownFences(string code)
        {
            code = code.Trim();
            if (code.StartsWith("```json")) code = code.Substring(7);
            else if (code.StartsWith("```csharp")) code = code.Substring(9);
            else if (code.StartsWith("```cs")) code = code.Substring(5);
            else if (code.StartsWith("```")) code = code.Substring(3);
            if (code.EndsWith("```")) code = code.Substring(0, code.Length - 3);
            return code.Trim();
        }
    }
}
