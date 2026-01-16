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
    }

    /// <summary>
    /// Hippocampus: The Long-Term Memory Manager for Fluxoria.
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
                    new ReflexionItem { Trigger = "instagram", Lesson = "Use TYPE:instagram.com for websites, do not use OPEN_APP:Instagram." },
                    new ReflexionItem { Trigger = "chrome", Lesson = "To open Chrome, use OPEN_APP:chrome (it handles 'Google Chrome' alias automatically)." },
                    new ReflexionItem { Trigger = "click", Lesson = "If unsure of coordinates, look for the element NAME and use [[CLICK:Name]]." }
                };
                Save();
                return;
            }

            try
            {
                string json = File.ReadAllText(_memoryPath);
                _memories = JsonSerializer.Deserialize<List<ReflexionItem>>(json) ?? new List<ReflexionItem>();
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

        /// <summary>
        /// Recalls relevant lessons based on the current context (User Goal).
        /// </summary>
        public List<string> Recall(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) return new List<string>();

            var contextLower = context.ToLower();
            var relevant = _memories
                .Where(m => contextLower.Contains(m.Trigger.ToLower()))
                .OrderByDescending(m => m.UseCount)
                .Take(3)
                .ToList();

            // Bump usage count for recalled memories
            foreach (var mem in relevant)
            {
                mem.UseCount++;
            }
            
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
    }
}
