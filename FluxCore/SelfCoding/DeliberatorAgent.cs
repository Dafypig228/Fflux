using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluxCore.LLM;

namespace FluxCore.SelfCoding
{
    public class DeliberatorAgent
    {
        public enum Role { Skeptic, Minimalist, UsersAdvocate }

        private readonly Role _role;
        private readonly ILLMService _llm;

        public DeliberatorAgent(ILLMService llm, Role role)
        {
            _llm = llm;
            _role = role;
        }

        public async Task<DeliberationResult> ReviewAsync(ImplementationPlan plan)
        {
            string rolePrompt = _role switch
            {
                Role.Skeptic => "You are the SKEPTIC. What could go wrong? Edge cases? Race conditions? Score: 0.0-1.0",
                Role.Minimalist => "You are the MINIMALIST. Simplest approach? Unnecessary steps? Reuse existing code? Score: 0.0-1.0",
                Role.UsersAdvocate => "You are the USER'S ADVOCATE. Does this match what user ACTUALLY asked? UX implications? Score: 0.0-1.0",
                _ => "Review this plan. Score: 0.0-1.0"
            };

            string response = await _llm.GenerateText(
                $"{rolePrompt}\n\nPLAN:\n{JsonSerializer.Serialize(plan)}\n\nRespond as JSON: {{\"score\": 0.8, \"blocking\": false, \"issues\": [...], \"suggestions\": [...]}}",
                temperature: 0.3f);

            return ParseDeliberation(response);
        }

        private DeliberationResult ParseDeliberation(string response)
        {
            try
            {
                // Strip markdown fences if present
                response = response.Trim();
                if (response.StartsWith("```json")) response = response.Substring(7);
                else if (response.StartsWith("```")) response = response.Substring(3);
                if (response.EndsWith("```")) response = response.Substring(0, response.Length - 3);
                response = response.Trim();

                var json = JsonSerializer.Deserialize<JsonElement>(response);
                return new DeliberationResult
                {
                    Dimension = _role.ToString(),
                    Score = json.GetProperty("score").GetSingle(),
                    HasBlockingIssues = json.TryGetProperty("blocking", out var b) && b.GetBoolean(),
                    Issues = json.TryGetProperty("issues", out var issues)
                        ? issues.EnumerateArray().Select(i => i.GetString() ?? "").ToList()
                        : new List<string>(),
                    Suggestions = json.TryGetProperty("suggestions", out var sugg)
                        ? sugg.EnumerateArray().Select(s => s.GetString() ?? "").ToList()
                        : new List<string>()
                };
            }
            catch
            {
                return new DeliberationResult
                {
                    Dimension = _role.ToString(),
                    Score = 0.5f,
                    Issues = new() { response }
                };
            }
        }
    }
}
