using System.Collections.Generic;

namespace FluxCore.SelfCoding
{
    public record SelfCodingResult(bool IsSuccess, string? Branch, ImplementationPlan? Plan, string? Error = null)
    {
        public static SelfCodingResult Success(string branch, ImplementationPlan plan) => new(true, branch, plan);
        public static SelfCodingResult Failed(string error) => new(false, null, null, error);
        public static SelfCodingResult Rejected(string reason) => new(false, null, null, reason);
    }

    public record VerificationResult(bool Success, string Message, List<BuildError>? BuildErrors = null);
    public record BuildResult(bool Success, List<BuildError> Errors, List<string> Warnings);
    public record BuildError(string FilePath, int Line, string Message);

    public class ImplementationPlan
    {
        public string Summary { get; set; } = "";
        public List<PlanStep> Steps { get; set; } = new();
        public List<string> FilesToModify { get; set; } = new();
        public List<string> FilesToCreate { get; set; } = new();
        public string RiskLevel { get; set; } = "low";
        public string EstimatedImpact { get; set; } = "";
    }

    public class PlanStep
    {
        public string Description { get; set; } = "";
        public string TargetFile { get; set; } = "";
        public string ChangeType { get; set; } = "modify";
        public string CodeSketch { get; set; } = "";
    }

    public class DeliberationResult
    {
        public string Dimension { get; set; } = "";
        public bool HasBlockingIssues { get; set; }
        public List<string> Issues { get; set; } = new();
        public List<string> Suggestions { get; set; } = new();
        public float Score { get; set; }
    }
}
