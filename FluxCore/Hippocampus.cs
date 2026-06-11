using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluxCore
{
    public class ReflexionItem
    {
        public string Trigger { get; set; } = "";
        public string Lesson { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int UseCount { get; set; } = 0;

        // Structured metadata — tracks lesson reliability
        public bool FromSuccess { get; set; } = true;
        public float Confidence { get; set; } = 0.5f;
        public int SuccessCount { get; set; } = 0;
        public int FailCount { get; set; } = 0;
    }

    /// <summary>
    /// Hippocampus: The Long-Term Memory Manager for Davos.
    /// Manages persistent storage of "Reflexions" (Lessons learned from past tasks).
    /// </summary>
    public class Hippocampus
    {
        private readonly string _memoryPath;
        private List<ReflexionItem> _memories = new List<ReflexionItem>();

        public Hippocampus()
        {
            // Store memory next to the executable for portability
            _memoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "knowledge.json");
            Load();
        }

        private void Load()
        {
            if (!File.Exists(_memoryPath))
            {
                // Seed initial knowledge
                _memories = new List<ReflexionItem>
                {
                    new ReflexionItem { Trigger = "instagram", Lesson = "Navigate to instagram.com/direct/inbox/ for DMs. Use CLICK:x,y coordinates from the element list, NOT CLICK:name (names can be ambiguous).", Confidence = 0.9f, SuccessCount = 3 },
                    new ReflexionItem { Trigger = "chrome", Lesson = "To focus address bar use KEYS:CTRL+L then TYPE:url. Never CLICK the address bar by name.", Confidence = 0.9f, SuccessCount = 3 },
                    new ReflexionItem { Trigger = "click", Lesson = "ALWAYS use [[CLICK:x,y]] coordinates from the VISIBLE UI ELEMENTS list. Never use CLICK:name when multiple elements share the same name.", Confidence = 0.9f, SuccessCount = 5 },
                    new ReflexionItem { Trigger = "close", Lesson = "To close popups/modals/stories use [[KEYS:ESCAPE]]. CLICK:Закрыть/Close may hit the browser X button instead!", Confidence = 0.9f, SuccessCount = 3 },
                    new ReflexionItem { Trigger = "edge", Lesson = "User prefers Chrome over Edge. Use OPEN_APP:chrome or RUN_SHELL:start chrome.", Confidence = 0.8f, SuccessCount = 2 }
                };
                Save();
                return;
            }

            try
            {
                string json = File.ReadAllText(_memoryPath);
                _memories = JsonSerializer.Deserialize<List<ReflexionItem>>(json) ?? new List<ReflexionItem>();
                
                // Version check: if old seeds are missing new lessons, regenerate
                bool hasNewSeeds = _memories.Any(m => m.Trigger == "close") && 
                                   _memories.Any(m => m.Trigger == "click" && m.Lesson.Contains("coordinates"));
                if (!hasNewSeeds)
                {
                    // Outdated seeds — keep user-learned memories, add new seeds
                    var userLearned = _memories.Where(m => m.UseCount > 1 || m.SuccessCount > 0 || m.FailCount > 0).ToList();
                    _memories = new List<ReflexionItem>
                    {
                        new ReflexionItem { Trigger = "instagram", Lesson = "Navigate to instagram.com/direct/inbox/ for DMs. Use CLICK:x,y coordinates from the element list, NOT CLICK:name (names can be ambiguous).", Confidence = 0.9f, SuccessCount = 3 },
                        new ReflexionItem { Trigger = "chrome", Lesson = "To focus address bar use KEYS:CTRL+L then TYPE:url. Never CLICK the address bar by name.", Confidence = 0.9f, SuccessCount = 3 },
                        new ReflexionItem { Trigger = "click", Lesson = "ALWAYS use [[CLICK:x,y]] coordinates from the VISIBLE UI ELEMENTS list. Never use CLICK:name when multiple elements share the same name.", Confidence = 0.9f, SuccessCount = 5 },
                        new ReflexionItem { Trigger = "close", Lesson = "To close popups/modals/stories use [[KEYS:ESCAPE]]. CLICK:Закрыть/Close may hit the browser X button instead!", Confidence = 0.9f, SuccessCount = 3 },
                        new ReflexionItem { Trigger = "edge", Lesson = "User prefers Chrome over Edge. Use OPEN_APP:chrome or RUN_SHELL:start chrome.", Confidence = 0.8f, SuccessCount = 2 }
                    };
                    _memories.AddRange(userLearned);
                    Save();
                }
            }
            catch 
            {
                _memories = new List<ReflexionItem>();
            }
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_memories, options);
                File.WriteAllText(_memoryPath, json);
            }
            catch { /* Best effort save */ }
        }

        public async Task SaveAsync()
        {
            await Task.Run(() => Save());
        }

        // Track last-recalled lessons for reinforcement after task completion
        private List<ReflexionItem> _lastRecalled = new List<ReflexionItem>();

        /// <summary>
        /// Recalls relevant lessons based on the current context (User Goal).
        /// Orders by Confidence (best lessons first), then by UseCount.
        /// </summary>
        public List<string> Recall(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) return new List<string>();

            var contextLower = context.ToLower();
            var relevant = _memories
                // Confidence floor: lessons that kept failing (or were stored from
                // one-off failures and never confirmed) must stop being injected as
                // "MANDATORY LESSONS" — they actively mislead the agent.
                .Where(m => m.Confidence >= 0.2f && contextLower.Contains(m.Trigger.ToLower()))
                .OrderByDescending(m => m.Confidence)
                .ThenByDescending(m => m.UseCount)
                .Take(5)
                .ToList();

            // Bump usage count for recalled memories
            foreach (var mem in relevant)
            {
                mem.UseCount++;
            }

            // Track for reinforcement
            _lastRecalled = relevant;

            // Fire and forget save to update counts
            _ = SaveAsync();

            return relevant.Select(m => m.Lesson).ToList();
        }

        /// <summary>
        /// Commits a new lesson to long-term memory.
        /// </summary>
        public async Task LearnAsync(string trigger, string lesson)
        {
            // Avoid duplicates
            var existing = _memories.FirstOrDefault(m => m.Trigger.ToLower() == trigger.ToLower() && m.Lesson.ToLower() == lesson.ToLower());
            if (existing != null)
            {
                existing.UseCount++; // Reinforce existing memory
            }
            else
            {
                _memories.Add(new ReflexionItem { Trigger = trigger, Lesson = lesson });
            }

            await SaveAsync();
        }

        /// <summary>
        /// Commits a structured lesson with success/failure metadata.
        /// Checks for near-duplicates via fuzzy matching and reinforces if found.
        /// </summary>
        public async Task LearnStructuredAsync(string trigger, string lesson, bool fromSuccess)
        {
            if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(lesson)) return;

            trigger = trigger.ToLower().Trim();
            string lessonLower = lesson.ToLower();

            // Check for near-duplicate: same trigger + lesson overlap > 60%
            var duplicate = _memories.FirstOrDefault(m =>
                m.Trigger.ToLower() == trigger &&
                FuzzyMatch(m.Lesson.ToLower(), lessonLower) > 0.6);

            if (duplicate != null)
            {
                // Reinforce existing lesson
                duplicate.UseCount++;
                if (fromSuccess) duplicate.SuccessCount++;
                else duplicate.FailCount++;
                // Recalculate confidence based on success ratio
                int total = duplicate.SuccessCount + duplicate.FailCount;
                if (total > 0)
                    duplicate.Confidence = (float)duplicate.SuccessCount / total;
            }
            else
            {
                // Store new lesson
                _memories.Add(new ReflexionItem
                {
                    Trigger = trigger,
                    Lesson = lesson,
                    FromSuccess = fromSuccess,
                    Confidence = fromSuccess ? 0.6f : 0.3f, // success lessons start higher
                    SuccessCount = fromSuccess ? 1 : 0,
                    FailCount = fromSuccess ? 0 : 1
                });
            }

            await SaveAsync();
        }

        /// <summary>
        /// Reinforces the last-recalled lessons based on task outcome.
        /// Creates a feedback loop: good lessons gain confidence, bad ones decay.
        /// </summary>
        public void ReinforceLessons(bool taskSucceeded)
        {
            if (_lastRecalled.Count == 0) return;

            foreach (var mem in _lastRecalled)
            {
                if (taskSucceeded)
                {
                    mem.SuccessCount++;
                    mem.Confidence = Math.Min(1.0f, mem.Confidence + 0.05f);
                }
                else
                {
                    mem.FailCount++;
                    mem.Confidence = Math.Max(0.0f, mem.Confidence - 0.05f);
                }
            }

            _lastRecalled.Clear();
            _ = SaveAsync();
        }

        /// <summary>
        /// Simple fuzzy string similarity (word overlap ratio).
        /// Returns 0.0 to 1.0 — how many words overlap between two strings.
        /// </summary>
        private static double FuzzyMatch(string a, string b)
        {
            var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            if (wordsA.Count == 0 || wordsB.Count == 0) return 0;

            int overlap = wordsA.Intersect(wordsB).Count();
            int maxLen = Math.Max(wordsA.Count, wordsB.Count);
            return (double)overlap / maxLen;
        }
    }
}
