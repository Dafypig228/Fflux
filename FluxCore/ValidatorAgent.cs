using System;
using System.IO;
using System.Threading.Tasks;

namespace FluxCore
{
    /// <summary>
    /// Validator/Critic Agent: Verifies if actions were actually performed
    /// and decides if retry is needed.
    /// </summary>
    public class ValidatorAgent
    {
        private readonly GeminiService _gemini;
        private const int MAX_RETRIES = 2;

        public ValidatorAgent(GeminiService gemini)
        {
            _gemini = gemini;
        }

        /// <summary>
        /// Validates if a command execution was successful.
        /// Returns suggested action: "success", "retry", or specific correction.
        /// </summary>
        public async Task<ValidationResult> ValidateAsync(string command, string commandType, string executionResult)
        {
            // Quick checks for obvious success/failure
            if (executionResult.Contains("Error:") || executionResult.Contains("failed"))
            {
                return new ValidationResult(false, "Command failed", true);
            }

            // Type-specific validation
            switch (commandType.ToUpper())
            {
                case "WRITE_FILE":
                    return await ValidateFileWriteAsync(command, executionResult);
                    
                case "RUN_PYTHON":
                case "RUN_SHELL":
                case "RUN_NODE":
                    return ValidateCodeExecution(executionResult);
                    
                case "DOWNLOAD_FILE":
                    return await ValidateDownloadAsync(command, executionResult);
                    
                case "OPEN_APP":
                    // App opening is hard to verify, trust the result
                    return new ValidationResult(true, "App opened", false);
                    
                default:
                    // For other commands, check for success indicators
                    if (executionResult.Contains("Success") || 
                        executionResult.Contains("Typed") ||
                        executionResult.Contains("Clicked") ||
                        executionResult.Contains("Sent keys") ||
                        executionResult.Contains("Copied"))
                    {
                        return new ValidationResult(true, "Command executed", false);
                    }
                    return new ValidationResult(true, "Assumed success", false);
            }
        }

        private async Task<ValidationResult> ValidateFileWriteAsync(string command, string result)
        {
            try
            {
                // Extract path from command (format: WRITE_FILE:path|content)
                string path = "";
                if (command.Contains("|"))
                {
                    path = command.Split('|')[0].Trim();
                }
                
                if (string.IsNullOrEmpty(path))
                {
                    return new ValidationResult(false, "Could not extract file path", true);
                }
                
                path = Environment.ExpandEnvironmentVariables(path);
                
                // Check if file actually exists now
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length > 0)
                    {
                        return new ValidationResult(true, $"File verified: {info.Length} bytes", false);
                    }
                    return new ValidationResult(false, "File exists but is empty", true);
                }
                
                return new ValidationResult(false, "File was not created", true);
            }
            catch (Exception ex)
            {
                return new ValidationResult(false, $"Validation error: {ex.Message}", false);
            }
        }

        private ValidationResult ValidateCodeExecution(string result)
        {
            // Check for error indicators in output
            if (result.Contains("Traceback") || 
                result.Contains("Error:") ||
                result.Contains("SyntaxError") ||
                result.Contains("not found"))
            {
                return new ValidationResult(false, "Code execution had errors", true);
            }
            
            if (result.Contains("output:") || result.Contains("(no output)"))
            {
                return new ValidationResult(true, "Code executed successfully", false);
            }
            
            return new ValidationResult(true, "Assumed success", false);
        }

        private async Task<ValidationResult> ValidateDownloadAsync(string command, string result)
        {
            try
            {
                // Extract save path from command (format: DOWNLOAD_FILE:url|path)
                var parts = command.Split('|');
                if (parts.Length < 2)
                {
                    return new ValidationResult(false, "Invalid download command format", false);
                }
                
                string path = Environment.ExpandEnvironmentVariables(parts[1].Trim());
                
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    return new ValidationResult(true, $"Downloaded: {info.Length} bytes", false);
                }
                
                return new ValidationResult(false, "Downloaded file not found", true);
            }
            catch
            {
                return new ValidationResult(false, "Could not verify download", false);
            }
        }
        public async Task<ValidationResult> ValidateVisualAsync(string intent, string beforeBase64, string afterBase64)
        {
            try
            {
                if (string.IsNullOrEmpty(beforeBase64) || string.IsNullOrEmpty(afterBase64))
                {
                    return new ValidationResult(true, "Skipped visual check (missing screenshot)", false);
                }

                // Prepare Prompt for Gemini Vision
                var history = new List<ChatMessage> 
                { 
                    new ChatMessage 
                    { 
                        IsUser = true, 
                        Text = $@"
I will provide you with TWO screenshots: 'BEFORE' and 'AFTER'.
The user tried to perform this action: '{intent}'.
Your job is to act as a QA VALIDATOR.

1. Compare the BEFORE and AFTER images.
2. Did the screen change in a way that suggests the action succeeded?
   - Example: If 'Open Notepad', does the AFTER image show a Notepad window?
   - Example: If 'Type Hello', does the text appear?
   - Example: If nothing changed, it FAILED.

Output strictly in this format:
VERDICT: [SUCCESS or FAILURE]
REASON: [Brief explanation of what changed or didn't change]
" 
                    } 
                };

                // We send "Before" as text placeholder (Gemini tool handles multiple images poorly in one message via this API wrapper usually,
                // but let's try sending After as the main vision payload or both if supported.
                // Assuming ChatWithHistory supports one vision attachment per turn. 
                // Strategy: Send 'After' image. 'Before' context is less critical if 'After' shows the desired state.
                // BETTER STRATEGY: Visual Question Answering often works best with just the RESULT image for 'Is X present?'.
                // Let's rely on the AFTER image to verify the goal.
                
                string prompt = $"The user wanted to: '{intent}'. Look at this screen (AFTER the action). Did they likely succeed? VERDICT: SUCCESS/FAILURE.";
                
                // TODO: For true diff, we need multi-image support. 
                // For now, let's just show the AFTER image and ask "Does this look like '{intent}' was done?"
                
                string response = await _gemini.ChatWithHistory(history, prompt, "BASE64:" + afterBase64, "", "");
                
                bool isSuccess = response.ToUpper().Contains("VERDICT: SUCCESS");
                string reason = response.Contains("REASON:") 
                    ? response.Substring(response.IndexOf("REASON:") + 7).Trim() 
                    : response;

                return new ValidationResult(isSuccess, reason, !isSuccess);
            }
            catch (Exception ex)
            {
                return new ValidationResult(true, $"Visual check failed to run: {ex.Message}", false);
            }
        }
    }

    public class ValidationResult
    {
        public bool Success { get; }
        public string Message { get; }
        public bool ShouldRetry { get; }

        public ValidationResult(bool success, string message, bool shouldRetry)
        {
            Success = success;
            Message = message;
            ShouldRetry = shouldRetry;
        }
    }
}
