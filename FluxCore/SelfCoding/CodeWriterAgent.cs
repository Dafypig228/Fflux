using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore.SelfCoding
{
    public class CodeWriterAgent
    {
        private readonly ILLMService _proModel;
        private readonly string _projectRoot;

        public CodeWriterAgent(ILLMService proModel, string projectRoot)
        {
            _proModel = proModel;
            _projectRoot = projectRoot;
        }

        public async Task ImplementStepAsync(PlanStep step)
        {
            string fullPath = Path.IsPathRooted(step.TargetFile)
                ? step.TargetFile
                : Path.Combine(_projectRoot, step.TargetFile);

            string existingCode = "";
            if (File.Exists(fullPath))
                existingCode = await File.ReadAllTextAsync(fullPath);

            bool isLargeFile = existingCode.Split('\n').Length > 300;

            string prompt;
            if (isLargeFile)
            {
                prompt = $@"You are modifying a LARGE file in the Davos project (C# WPF, .NET 10, namespace FluxCore).

CURRENT FILE ({step.TargetFile}, {existingCode.Split('\n').Length} lines):
```csharp
{existingCode}
```

CHANGE: {step.ChangeType} — {step.Description}
CODE SKETCH: {step.CodeSketch}

Because this file is large, return ONLY the modified method/class using this format:
// REPLACE METHOD: MethodName
// END REPLACE
You may include multiple REPLACE blocks if needed.";
            }
            else
            {
                prompt = $@"You are modifying the Davos project (C# WPF, .NET 10, namespace FluxCore).

CONVENTIONS: namespace FluxCore, partial classes no base class repeat, async/await, nullable, C# 12+

CURRENT FILE ({step.TargetFile}):
```csharp
{existingCode}
```

CHANGE: {step.ChangeType} — {step.Description}
CODE SKETCH: {step.CodeSketch}

Return the COMPLETE modified file. No markdown fences. No explanations.";
            }

            string generatedCode = await _proModel.GenerateText(prompt, temperature: 0.2f);
            generatedCode = StripMarkdownFences(generatedCode);

            if (step.ChangeType == "delete")
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                return;
            }

            if (isLargeFile && generatedCode.Contains("// REPLACE METHOD:"))
            {
                try
                {
                    ApplyReplaceBlocks(fullPath, existingCode, generatedCode);
                }
                catch
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeWriter] REPLACE parsing failed for {step.TargetFile}, falling back to full-file");
                    generatedCode = await _proModel.GenerateText(
                        prompt.Replace("return ONLY the modified method", "Return the COMPLETE modified file"),
                        temperature: 0.15f);
                    generatedCode = StripMarkdownFences(generatedCode);
                    await File.WriteAllTextAsync(fullPath, generatedCode);
                }
            }
            else
            {
                if (!generatedCode.Contains("namespace") && step.ChangeType != "delete"
                    && !string.IsNullOrWhiteSpace(existingCode))
                {
                    generatedCode = await _proModel.GenerateText(
                        prompt + "\n\nCRITICAL: Return the COMPLETE file with namespace declaration.",
                        temperature: 0.15f);
                    generatedCode = StripMarkdownFences(generatedCode);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, generatedCode);
            }
        }

        public async Task FixBuildErrorsAsync(List<BuildError> errors)
        {
            foreach (var errorGroup in errors.GroupBy(e => e.FilePath))
            {
                if (!File.Exists(errorGroup.Key)) continue;

                string code = await File.ReadAllTextAsync(errorGroup.Key);
                string errorList = string.Join("\n", errorGroup.Select(e => $"  Line {e.Line}: {e.Message}"));

                string fix = await _proModel.GenerateText(
                    $"Fix these build errors in {Path.GetFileName(errorGroup.Key)}:\n{errorList}\n\nCurrent code:\n```csharp\n{code}\n```\n\nReturn COMPLETE fixed file. No markdown fences.",
                    temperature: 0.1f);

                await File.WriteAllTextAsync(errorGroup.Key, StripMarkdownFences(fix));
            }
        }

        private static string StripMarkdownFences(string code)
        {
            code = code.Trim();
            if (code.StartsWith("```csharp")) code = code.Substring(9);
            else if (code.StartsWith("```cs")) code = code.Substring(5);
            else if (code.StartsWith("```")) code = code.Substring(3);
            if (code.EndsWith("```")) code = code.Substring(0, code.Length - 3);
            return code.Trim();
        }

        private void ApplyReplaceBlocks(string filePath, string existingCode, string replaceOutput)
        {
            var blocks = Regex.Matches(replaceOutput,
                @"// REPLACE METHOD: (\w+)\s*\n(.*?)// END REPLACE",
                RegexOptions.Singleline);

            string result = existingCode;
            foreach (Match block in blocks)
            {
                string methodName = block.Groups[1].Value;
                string newCode = block.Groups[2].Value.Trim();

                // Find existing method by name and replace using brace counting
                var methodStart = Regex.Match(result,
                    $@"(public|private|protected|internal)\s+.*?\b{Regex.Escape(methodName)}\b\s*\([^)]*\)\s*\{{",
                    RegexOptions.Singleline);

                if (!methodStart.Success) continue;

                int braceStart = methodStart.Index + methodStart.Length - 1;
                int braceCount = 1;
                int pos = braceStart + 1;
                while (pos < result.Length && braceCount > 0)
                {
                    if (result[pos] == '{') braceCount++;
                    else if (result[pos] == '}') braceCount--;
                    pos++;
                }

                if (braceCount == 0)
                {
                    result = result.Substring(0, methodStart.Index) + newCode + result.Substring(pos);
                }
            }

            File.WriteAllText(filePath, result);
        }
    }
}
