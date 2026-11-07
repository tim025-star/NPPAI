using System;
using System.Text;
using System.Text.RegularExpressions;

namespace NPPAI.Memory
{
    /// <summary>
    /// Accept/reject/edit tracking on top of MemoryDb.
    /// </summary>
    public class LearningEngine
    {
        private readonly MemoryDb _db;

        public LearningEngine(MemoryDb db) => _db = db;

        /// <summary>
        /// Build a phrase trigger key from the typed prefix:
        /// last 2-4 words, normalized (lowercase, punctuation stripped).
        /// </summary>
        public static string MakeTrigger(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return "";
            var words = Regex.Split(prefix, @"\s+");
            var tail = new StringBuilder();
            int start = Math.Max(0, words.Length - 4);
            for (int i = start; i < words.Length; i++)
            {
                string w = Regex.Replace(words[i], @"[^\p{L}\p{N}_]", "", RegexOptions.CultureInvariant);
                if (w.Length == 0) continue;
                if (tail.Length > 0) tail.Append(' ');
                tail.Append(w.ToLowerInvariant());
            }
            string t = tail.ToString();
            // Need at least 2 words to be a useful trigger.
            if (t.Split(' ').Length < 2) return "";
            return t;
        }

        public long RecordAccept(string prefix, string suggestion, string contextHash)
        {
            string trigger = MakeTrigger(prefix);
            if (trigger.Length == 0)
                return -1;
            try
            {
                return _db.RecordAccept(trigger, prefix, suggestion, contextHash);
            }
            catch
            {
                return -1;
            }
        }

        public void RecordReject(string prefix, string suggestion)
        {
            string trigger = MakeTrigger(prefix);
            if (trigger.Length == 0) return;
            try { _db.RecordReject(trigger, suggestion); }
            catch { }
        }

        public void RecordEdit(string prefixFragment)
        {
            try
            {
                long id = _db.FindRecentCompletionByPrefix(prefixFragment);
                if (id > 0) _db.RecordEdit(id);
            }
            catch { }
        }
    }
}
