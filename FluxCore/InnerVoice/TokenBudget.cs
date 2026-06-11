using System;

namespace FluxCore.InnerVoice
{
    /// <summary>
    /// Daily character budget for inner monologue LLM calls.
    /// Approximates token cost as chars/4 (rough estimate, good enough for throttling).
    ///
    /// Soft limit (50% consumed): doubles the inner loop interval.
    /// Hard limit (80% consumed): suspends autonomous loops entirely until midnight reset.
    ///
    /// This prevents runaway Swarm chains or curiosity spirals from burning the Gemini quota.
    /// </summary>
    public class TokenBudget
    {
        private readonly AppSettings _settings;
        private readonly InnerState  _state;

        public TokenBudget(AppSettings settings, InnerState state)
        {
            _settings = settings;
            _state    = state;
        }

        /// <summary>
        /// Record characters consumed by one LLM call (prompt + response combined).
        /// Resets the counter automatically at midnight.
        /// </summary>
        public void RecordUsage(string prompt, string response)
        {
            EnsureTodayWindow();
            _state.DailyCharsUsed += prompt.Length + response.Length;
            _state.Save();
        }

        /// <summary>Current budget status — drives loop interval and suspension.</summary>
        public BudgetStatus GetStatus()
        {
            EnsureTodayWindow();

            long budget = _settings.InnerVoiceBudgetDailyChars;
            if (budget <= 0) return BudgetStatus.Normal;   // uncapped

            float pct = (float)_state.DailyCharsUsed / budget;
            return pct switch
            {
                >= 0.80f => BudgetStatus.HardLimit,
                >= 0.50f => BudgetStatus.SoftLimit,
                _        => BudgetStatus.Normal
            };
        }

        /// <summary>Characters remaining in today's budget (0 if uncapped or exhausted).</summary>
        public long Remaining()
        {
            EnsureTodayWindow();
            long budget = _settings.InnerVoiceBudgetDailyChars;
            return budget <= 0 ? long.MaxValue : Math.Max(0L, budget - _state.DailyCharsUsed);
        }

        private void EnsureTodayWindow()
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (_state.BudgetDate != today)
            {
                _state.DailyCharsUsed = 0;
                _state.BudgetDate     = today;
            }
        }
    }

    public enum BudgetStatus
    {
        Normal,     // Loop runs at normal cadence
        SoftLimit,  // 50–80% consumed — double the interval
        HardLimit   // 80%+ consumed — suspend autonomous loops until midnight
    }
}
