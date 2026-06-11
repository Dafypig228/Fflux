using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FluxCore.InnerVoice
{
    /// <summary>
    /// Guards Davos's inner monologue from processing sensitive personal observations.
    /// The rule: Davos should NOT spontaneously comment on the user's personal finances,
    /// health, or relationships — those are private zones.
    ///
    /// Work-context exemption: "bank API integration" is coding, not personal finance.
    /// The filter checks for code/tech terms before blocking to avoid false positives.
    /// </summary>
    public class PrivacyFilter
    {
        // ── PII pattern detection ─────────────────────────────────────────────────
        private static readonly Regex[] _piiPatterns =
        [
            new(@"\b\d{4}[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}\b"),          // card numbers
            new(@"\b\d{3}[\s\-]\d{2}[\s\-]\d{4}\b"),                          // SSN (US format)
            new(@"\b(password|passwd|secret|token|api[\s_-]?key)\s*[:=]\s*\S+",
                RegexOptions.IgnoreCase),                                       // credential assignments
            new(@"\bIBAN\s*[:\s][A-Z]{2}\d{2}[A-Z0-9]{4,30}\b",
                RegexOptions.IgnoreCase),                                       // IBAN
        ];

        // ── Known banking / healthcare URL domains to suppress ───────────────────
        private static readonly HashSet<string> _sensitiveDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            // Central Asian / Russian banks
            "online.kaspi.kz", "my.halykbank.kz", "bcc.kz", "homebank.kz",
            "alfabank.kz", "sberbank.ru", "vtb.ru", "alfa-bank.ru", "tbank.ru",
            // International banks
            "chase.com", "bankofamerica.com", "wellsfargo.com", "citibank.com",
            "hsbc.com", "barclays.co.uk", "santander.com",
            // Healthcare
            "myhealth.va.gov", "patient.portal", "epic.com", "mychart.com",
            // Tax / government
            "irs.gov", "nalog.ru", "egov.kz",
        };

        // ── Sensitive keyword groups ──────────────────────────────────────────────
        // Only checked when the content does NOT appear to be a work/coding context.
        private static readonly string[] _financialKeywords =
        [
            "bank statement", "account balance", "wire transfer", "routing number",
            "overdraft", "loan application", "credit score", "my salary", "my wage",
            "my paycheck", "my rent", "my mortgage", "my debt",
        ];

        private static readonly string[] _medicalKeywords =
        [
            "my diagnosis", "my prescription", "my medication", "my therapy",
            "my doctor said", "my symptoms", "my test results", "my blood",
            "my surgery", "my condition",
        ];

        private static readonly string[] _relationshipKeywords =
        [
            "my girlfriend", "my boyfriend", "my wife", "my husband",
            "my ex ", "we broke up", "our relationship", "our argument",
            "she left me", "he left me",
        ];

        // ── Work-context exemption keywords ──────────────────────────────────────
        private static readonly string[] _workContextKeywords =
        [
            "api", "sdk", "endpoint", "library", "class ", "function ", "method ",
            "interface ", "module", "package", "framework", "integration", "webhook",
            "repository", "git", "commit", "docker", "kubernetes",
        ];

        /// <summary>
        /// Filters an observation for the inner monologue.
        /// Returns null if the observation should be suppressed (too personal/sensitive).
        /// Returns the original content if it is safe to process.
        /// </summary>
        public string? Filter(string source, string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            // 1. PII regex patterns — always block regardless of context
            foreach (var pattern in _piiPatterns)
                if (pattern.IsMatch(content)) return null;

            // 2. Chrome URL — check domain
            if (source == "chrome" && IsSensitiveDomain(content)) return null;

            // 3. Clipboard with long private-looking content
            if (source == "clipboard" && content.Length > 250 && LooksLikePrivateData(content))
                return null;

            // 4. Keyword matching — only if NOT a work/coding context
            if (!IsWorkContext(content))
            {
                if (ContainsAny(content, _financialKeywords)) return null;
                if (ContainsAny(content, _medicalKeywords))   return null;
                if (ContainsAny(content, _relationshipKeywords)) return null;
            }

            return content;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static bool IsSensitiveDomain(string content)
        {
            foreach (var domain in _sensitiveDomains)
                if (content.Contains(domain, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsWorkContext(string content)
        {
            foreach (var kw in _workContextKeywords)
                if (content.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool LooksLikePrivateData(string content)
        {
            // High ratio of uppercase letters, digits, or special chars → likely credentials/keys
            int suspicious = 0;
            foreach (char c in content)
                if (char.IsUpper(c) || char.IsDigit(c) || c == '=' || c == '+' || c == '/')
                    suspicious++;
            return (float)suspicious / content.Length > 0.35f;
        }

        private static bool ContainsAny(string content, string[] keywords)
        {
            foreach (var kw in keywords)
                if (content.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
