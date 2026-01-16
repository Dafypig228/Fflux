using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;

namespace FluxCore
{
    /// <summary>
    /// Orchestrator Agent: The "Brain" that classifies intent and plans multi-step actions.
    /// </summary>
    public class OrchestratorAgent
    {
        private readonly GeminiService _gemini;

        public OrchestratorAgent(GeminiService gemini)
        {
            _gemini = gemini;
        }

        /// <summary>
        /// Analyzes user input and returns a structured plan.
        /// </summary>
        public async Task<OrchestrationPlan> AnalyzeAndPlanAsync(string userInput, string currentContext)
        {
            string prompt = $@"
You are an AI task planner. Analyze the user's request and create an execution plan.

USER REQUEST: {userInput}
CURRENT CONTEXT: {currentContext}

Respond with ONLY valid JSON in this exact format:
{{
  ""intent"": ""question|command|info_request|conversation"",
  ""requires_vision"": true/false,
  ""requires_web"": true/false,
  ""requires_file"": true/false,
  ""requires_app"": true/false,
  ""steps"": [
    {{""action"": ""describe what to do"", ""tool"": ""vision|web|file|app|respond""}}
  ],
  ""direct_response"": ""If this is a simple question, answer here. Otherwise leave empty.""
}}

EXAMPLES:
- ""What's on my screen?"" -> intent: question, requires_vision: true, steps: [{{action: ""capture screen"", tool: ""vision""}}]
- ""Open notepad"" -> intent: command, requires_app: true, steps: [{{action: ""open notepad.exe"", tool: ""app""}}]
- ""Search for AI news"" -> intent: info_request, requires_web: true, steps: [{{action: ""search google for AI news"", tool: ""web""}}]
- ""Hello"" -> intent: conversation, direct_response: ""Hello! How can I help you?""

JSON:";

            try
            {
                string response = await _gemini.GenerateText(prompt);
                
                // Clean response (remove markdown code blocks if present)
                response = response.Trim();
                if (response.StartsWith("```json")) response = response.Substring(7);
                if (response.StartsWith("```")) response = response.Substring(3);
                if (response.EndsWith("```")) response = response.Substring(0, response.Length - 3);
                response = response.Trim();

                var plan = JsonSerializer.Deserialize<OrchestrationPlan>(response, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
                
                return plan ?? new OrchestrationPlan { Intent = "conversation", DirectResponse = "I understand." };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Orchestrator] Parse error: {ex.Message}");
                return new OrchestrationPlan 
                { 
                    Intent = "error", 
                    DirectResponse = $"Planning failed: {ex.Message}" 
                };
            }
        }

        /// <summary>
        /// Determines if we should use the Orchestrator or go directly to Gemini.
        /// Simple queries bypass planning for speed.
        /// </summary>
        public bool ShouldUsePlanning(string userInput)
        {
            // Short conversational inputs don't need planning
            if (userInput.Length < 20) return false;
            
            // Keywords that suggest complex tasks
            string[] complexKeywords = { "search", "find", "open", "read", "check", "look up", "research", "download", "save", "analyze" };
            foreach (var keyword in complexKeywords)
            {
                if (userInput.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }
    }

    public class OrchestrationPlan
    {
        public string Intent { get; set; } = "conversation";
        public bool RequiresVision { get; set; }
        public bool RequiresWeb { get; set; }
        public bool RequiresFile { get; set; }
        public bool RequiresApp { get; set; }
        public List<OrchestrationStep>? Steps { get; set; }
        public string? DirectResponse { get; set; }
    }

    public class OrchestrationStep
    {
        public string Action { get; set; } = "";
        public string Tool { get; set; } = "";
    }
}
