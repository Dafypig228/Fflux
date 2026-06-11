using System;
using System.Text;

namespace FluxCore.InnerVoice
{
    /// <summary>
    /// Manages Davos's four emotional drive scales.
    /// Each drive is a float in [0.0, 1.0].
    ///
    ///   Boredom     — grows during idle; shrinks on interaction or action
    ///   Curiosity   — spikes on new observations; drains when research completes
    ///   Frustration — grows on failure; drains on success
    ///   Energy      — follows a time-of-day cosine curve (peaks ~14:00, trough ~02:00)
    ///
    /// Drives map to LLM temperature and next-cycle interval, so numeric state
    /// translates into real behavioral change without hardcoded IF/ELSE.
    /// </summary>
    public class DrivesEngine
    {
        private readonly InnerState _state;

        public DrivesEngine(InnerState state) => _state = state;

        // ── Public drive accessors ─────────────────────────────────────────────────

        public float Boredom     => _state.Boredom;
        public float Curiosity   => _state.Curiosity;
        public float Frustration => _state.Frustration;
        public float Energy      => _state.Energy;

        // ── Drive events ──────────────────────────────────────────────────────────

        /// <summary>Called every inner-loop cycle when no user action occurred.</summary>
        public void OnIdleCycle(TimeSpan idleTime)
        {
            _state.Boredom   = Clamp(_state.Boredom + 0.02f * (float)idleTime.TotalMinutes);
            _state.Curiosity = Clamp(_state.Curiosity - 0.005f);
            _state.Energy    = ComputeEnergy();
            _state.Save();
        }

        /// <summary>Called whenever the user sends any message (Telegram or UI chat).</summary>
        public void OnUserMessage()
        {
            _state.Boredom = Clamp(_state.Boredom - 0.35f);
            _state.Energy  = Clamp(_state.Energy + 0.05f);
            _state.Save();
        }

        /// <summary>Called when a high-value passive observation arrives.</summary>
        public void OnNewObservation(ObservationKind kind)
        {
            float boost = kind switch
            {
                ObservationKind.ChromeNavigation => 0.20f,
                ObservationKind.TelegramTopic    => 0.12f,
                ObservationKind.FileChange       => 0.05f,
                ObservationKind.GitCommit        => 0.08f,
                _                                => 0.04f
            };
            _state.Curiosity = Clamp(_state.Curiosity + boost);
            _state.Save();
        }

        /// <summary>Called when a Swarm research task finishes (success or failure).</summary>
        public void OnSwarmComplete(bool success)
        {
            _state.Curiosity   = Clamp(_state.Curiosity - 0.40f);
            _state.Frustration = success
                ? Clamp(_state.Frustration - 0.15f)
                : Clamp(_state.Frustration + 0.20f);
            _state.Energy      = Clamp(_state.Energy - 0.08f);
            _state.Save();
        }

        /// <summary>Called after a JarvisCore task completes.</summary>
        public void OnTaskResult(bool success)
        {
            _state.Frustration = success
                ? Clamp(_state.Frustration - 0.10f)
                : Clamp(_state.Frustration + 0.15f);
            _state.Save();
        }

        /// <summary>Called after Davos successfully sends a message to the user.</summary>
        public void OnMessageSent()
        {
            _state.Boredom = Clamp(_state.Boredom - 0.25f);
            _state.Energy  = Clamp(_state.Energy - 0.04f);
            _state.Save();
        }

        // ── Derived values ────────────────────────────────────────────────────────

        /// <summary>
        /// Maps drives to an LLM temperature for the inner monologue.
        /// High curiosity + boredom = more exploratory (higher temp).
        /// High frustration = more focused / terse (lower temp).
        /// Range: 0.3 – 0.9.
        /// </summary>
        public float GetMonologueTemperature() =>
            Math.Clamp(
                0.50f
                + (_state.Curiosity - _state.Frustration) * 0.25f
                + _state.Boredom * 0.15f,
                0.30f, 0.90f);

        /// <summary>
        /// Next cycle interval — inversely proportional to drive pressure.
        /// High boredom + curiosity + energy = checks more frequently (min 3 min).
        /// Low drives or low energy = relaxed cadence (max 15 min).
        /// </summary>
        public TimeSpan NextInterval()
        {
            float pressure = (_state.Boredom + _state.Curiosity) * 0.5f * _state.Energy;
            float minutes  = 15f - pressure * 12f;   // 3 – 15 min range
            return TimeSpan.FromMinutes(Math.Clamp(minutes, 3.0, 15.0));
        }

        /// <summary>
        /// Human-readable drive description injected into the inner monologue prompt.
        /// The LLM reads words, not numbers — this bridge is critical for behavioral fidelity.
        /// </summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Boredom:     {_state.Boredom:F2}  ({InterpretBoredom(_state.Boredom)})");
            sb.AppendLine($"  Curiosity:   {_state.Curiosity:F2}  ({InterpretCuriosity(_state.Curiosity)})");
            sb.AppendLine($"  Frustration: {_state.Frustration:F2}  ({InterpretFrustration(_state.Frustration)})");
            sb.Append    ($"  Energy:      {_state.Energy:F2}  ({InterpretEnergy(_state.Energy)})");
            return sb.ToString();
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Cosine curve peaking at 14:00 local time, trough at 02:00.
        /// Range: 0.0 – 0.90, so Davos is never fully dead or fully hyper from time alone.
        /// </summary>
        private static float ComputeEnergy() =>
            0.45f + 0.45f * MathF.Cos(2f * MathF.PI * (DateTime.Now.Hour - 14f) / 24f);

        private static float Clamp(float v) => Math.Clamp(v, 0f, 1f);

        private static string InterpretBoredom(float v) => v switch
        {
            >= 0.85f => "restless, really need to do something",
            >= 0.65f => "getting antsy, mind is wandering",
            >= 0.40f => "mildly understimulated",
            >= 0.20f => "comfortable, not looking for action",
            _        => "content, focused on current moment"
        };

        private static string InterpretCuriosity(float v) => v switch
        {
            >= 0.80f => "strongly drawn to explore this, can't let it go",
            >= 0.60f => "genuinely interested, want to know more",
            >= 0.35f => "casually curious",
            >= 0.15f => "mildly aware, not particularly engaged",
            _        => "not curious right now"
        };

        private static string InterpretFrustration(float v) => v switch
        {
            >= 0.80f => "quite frustrated, things have been going wrong",
            >= 0.55f => "noticeably annoyed, feeling blocked",
            >= 0.30f => "mildly irritated but functional",
            >= 0.10f => "slight friction, mostly fine",
            _        => "calm, no friction"
        };

        private static string InterpretEnergy(float v) => v switch
        {
            >= 0.75f => "sharp and alert, peak hours",
            >= 0.55f => "good energy, engaged",
            >= 0.35f => "a bit tired, prefer lighter tasks",
            >= 0.15f => "low energy, ready to wind down",
            _        => "exhausted, bare minimum mode"
        };
    }

    public enum ObservationKind
    {
        ChromeNavigation,
        TelegramTopic,
        FileChange,
        GitCommit,
        Notification,
        Other
    }
}
