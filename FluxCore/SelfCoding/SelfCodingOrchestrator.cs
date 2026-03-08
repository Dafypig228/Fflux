using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore.SelfCoding
{
    public class SelfCodingOrchestrator
    {
        private readonly PlannerAgent _planner;
        private readonly DeliberatorAgent _skeptic, _minimalist, _advocate;
        private readonly CodeWriterAgent _codeWriter;
        private readonly BuildVerifier _buildVerifier;
        private readonly RuntimeVerifier _verifier;
        private readonly GitManager _git;
        private readonly Func<string, Task<bool>> _userApproval;
        private readonly Action<string> _logToUI;

        public SelfCodingOrchestrator(
            ILLMService proModel,
            ILLMService flashModel,
            string repoRoot,
            Func<string, Task<bool>> userApproval,
            Action<string> logToUI)
        {
            _planner = new PlannerAgent(proModel, repoRoot);
            _skeptic = new DeliberatorAgent(flashModel, DeliberatorAgent.Role.Skeptic);
            _minimalist = new DeliberatorAgent(flashModel, DeliberatorAgent.Role.Minimalist);
            _advocate = new DeliberatorAgent(flashModel, DeliberatorAgent.Role.UsersAdvocate);
            _codeWriter = new CodeWriterAgent(proModel, repoRoot);
            _buildVerifier = new BuildVerifier(repoRoot);
            _verifier = new RuntimeVerifier(_buildVerifier, proModel, repoRoot);
            _git = new GitManager(repoRoot);
            _userApproval = userApproval;
            _logToUI = logToUI;
        }

        public async Task<SelfCodingResult> ExecuteAsync(string userRequest, CancellationToken ct = default)
        {
            try
            {
                // 1. PLAN
                _logToUI("[brain] Planning implementation...");
                var plan = await _planner.GeneratePlanAsync(userRequest);

                if (plan?.Steps == null || plan.Steps.Count == 0)
                    return SelfCodingResult.Failed("Planner could not generate a valid plan");

                _logToUI($"[plan] Plan: {plan.Summary} ({plan.Steps.Count} steps)");

                // 2. DELIBERATE (3 reviewers in parallel)
                _logToUI("[review] Skeptic, Minimalist, User's Advocate reviewing...");
                var critiques = await Task.WhenAll(
                    _skeptic.ReviewAsync(plan),
                    _minimalist.ReviewAsync(plan),
                    _advocate.ReviewAsync(plan));

                var blocking = critiques.Where(c => c.HasBlockingIssues).ToList();
                if (blocking.Count > 0)
                {
                    _logToUI($"[warning] {blocking.Count} blocking issues. Revising...");
                    var revisedPlan = await _planner.RevisePlanAsync(plan, critiques);
                    if (revisedPlan != null) plan = revisedPlan;

                    critiques = await Task.WhenAll(
                        _skeptic.ReviewAsync(plan),
                        _minimalist.ReviewAsync(plan),
                        _advocate.ReviewAsync(plan));

                    blocking = critiques.Where(c => c.HasBlockingIssues).ToList();
                    if (blocking.Count > 0)
                        return SelfCodingResult.Failed(
                            $"Blocking issues persist:\n{string.Join("\n", blocking.SelectMany(c => c.Issues))}");
                }

                _logToUI($"[ok] Deliberators approve (avg: {critiques.Average(c => c.Score):F1})");

                // 3. USER APPROVAL
                if (!await _userApproval(FormatPlanForDisplay(plan, critiques)))
                    return SelfCodingResult.Rejected("User rejected plan");

                // 4. GIT BRANCH
                string branch = await _git.CreateBranchAsync(userRequest);
                _logToUI($"[branch] Branch: {branch}");

                // 5. IMPLEMENT
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    _logToUI($"[code] Step {i + 1}/{plan.Steps.Count}: {plan.Steps[i].Description}");
                    await _codeWriter.ImplementStepAsync(plan.Steps[i]);
                    await _git.CommitAsync($"Step {i + 1}: {plan.Steps[i].Description}");
                }

                // 6. VERIFY
                _logToUI("[build] Verifying...");
                var result = await _verifier.VerifyAsync(plan.Summary);

                for (int fix = 0; fix < 3 && !result.Success && result.BuildErrors?.Count > 0; fix++)
                {
                    _logToUI($"[fix] Auto-fix attempt {fix + 1}/3...");
                    await _codeWriter.FixBuildErrorsAsync(result.BuildErrors);
                    await _git.CommitAsync($"Auto-fix (attempt {fix + 1})");
                    result = await _verifier.VerifyAsync(plan.Summary);
                }

                if (!result.Success)
                {
                    if (!await _userApproval($"Verification failed: {result.Message}\nMerge anyway?"))
                    {
                        await _git.AbandonBranchAsync();
                        return SelfCodingResult.Failed(result.Message);
                    }
                }

                // 7. USER REVIEWS DIFF
                string diff = await _git.GetDiffAsync();
                string diffPreview = diff.Length > 2000 ? diff.Substring(0, 2000) + "\n...(truncated)" : diff;

                if (await _userApproval($"Build: {(result.Success ? "ok" : "fail")}\nBranch: {branch}\n\n{diffPreview}"))
                {
                    await _git.MergeToCurrentAsync();
                    _logToUI($"[ok] Merged {branch}");
                    return SelfCodingResult.Success(branch, plan);
                }

                await _git.AbandonBranchAsync();
                return SelfCodingResult.Rejected("User rejected after review");
            }
            catch (OperationCanceledException)
            {
                return SelfCodingResult.Failed("Cancelled");
            }
            catch (Exception ex)
            {
                _logToUI($"[error] Error: {ex.Message}");
                try { await _git.AbandonBranchAsync(); } catch { }
                return SelfCodingResult.Failed(ex.Message);
            }
        }

        private string FormatPlanForDisplay(ImplementationPlan plan, DeliberationResult[] critiques)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {plan.Summary} ===");
            sb.AppendLine($"Risk: {plan.RiskLevel} | Steps: {plan.Steps.Count}");
            foreach (var step in plan.Steps)
                sb.AppendLine($"  {step.ChangeType.ToUpper()} {Path.GetFileName(step.TargetFile)}: {step.Description}");
            sb.AppendLine("\nDeliberator Scores:");
            foreach (var c in critiques)
                sb.AppendLine($"  {c.Dimension}: {c.Score:F1} {(c.HasBlockingIssues ? "[BLOCKING]" : "[OK]")}");
            return sb.ToString();
        }
    }
}
