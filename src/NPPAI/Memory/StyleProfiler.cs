using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NPPAI.Memory
{
    /// <summary>
    /// Computes a lightweight writing-style profile from accepted completions
    /// and stores it in the user_profile table.
    /// </summary>
    public class StyleProfiler
    {
        private readonly MemoryDb _db;
        private DateTime _lastComputed = DateTime.MinValue;
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

        public StyleProfiler(MemoryDb db) => _db = db;

        /// <summary>
        /// Recompute the profile if stale. Returns true if the profile is
        /// available (freshly computed or cached in DB).
        /// </summary>
        public bool EnsureProfile()
        {
            if (DateTime.UtcNow - _lastComputed < MinInterval)
                return true; // use cached profile from DB

            var accepts = _db.GetRecentAccepts(200);
            if (accepts.Count == 0)
                return false; // not enough data yet

            string allText = "";
            foreach (var (prefix, suggestion, at, edited) in accepts)
                if (edited == 0) allText += (suggestion ?? "") + "\n";

            // --- sentence length ---
            var sentences = Regex.Split(allText, @"[.!?]+\s+|\n");
            int wordCount = 0, sentenceCount = 0;
            foreach (var s in sentences)
            {
                var words = Regex.Split(s, @"\s+");
                int n = 0;
                foreach (var w in words)
                    if (Regex.IsMatch(w, @"[\p{L}\p{N}]")) n++;
                if (n > 0)
                {
                    wordCount += n;
                    sentenceCount++;
                }
            }
            double avgLen = sentenceCount > 0 ? (double)wordCount / sentenceCount : 0;
            string lengthLabel = avgLen < 12 ? "short" : avgLen < 25 ? "medium" : "long";

            // --- formality heuristic ---
            int contractions = Regex.Matches(allText, @"\b(\w+['’])(n't|re|ve|ll|d|s)\b").Count;
            double contractionsPer100 = wordCount > 0 ? contractions * 100.0 / wordCount : 0;
            int exclamations = Regex.Matches(allText, @"!").Count;
            double exclamPer100 = wordCount > 0 ? exclamations * 100.0 / wordCount : 0;

            string formality;
            if (contractionsPer100 > 4 || exclamPer100 > 1) formality = "casual";
            else if (contractionsPer100 > 1) formality = "medium";
            else formality = "formal";

            // --- greeting / closing patterns ---
            string greeting = MostFrequentLineStart(allText,
                @"^(Hi|Hello|Dear|Hey|Good (?:morning|afternoon|evening))\b");
            string closing = MostFrequentLineStart(allText,
                @"^(NFA|Regards|Best|Thanks|Cheers|Sincerely|Thank you)\b");

            _db.SetProfile("avg_sentence_len", lengthLabel);
            _db.SetProfile("formality", formality);
            if (greeting != null) _db.SetProfile("preferred_greeting", greeting);
            if (closing != null) _db.SetProfile("preferred_closing", closing);
            _db.SetProfile("profile_updated_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

            _lastComputed = DateTime.UtcNow;
            return true;
        }

        private static string MostFrequentLineStart(string text, string pattern)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), pattern);
                if (m.Success)
                {
                    string key = m.Value.Trim();
                    counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                }
            }
            string best = null;
            int bestCount = 1; // need at least 2 occurrences
            foreach (var kv in counts)
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
            return best;
        }

        /// <summary>Read profile values for prompt injection.</summary>
        public (string formality, string length, string greeting, string closing) GetProfile()
        {
            return (
                _db.GetProfile("formality"),
                _db.GetProfile("avg_sentence_len"),
                _db.GetProfile("preferred_greeting"),
                _db.GetProfile("preferred_closing")
            );
        }
    }
}
