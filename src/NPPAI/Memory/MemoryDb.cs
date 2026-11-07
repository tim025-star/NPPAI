using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace NPPAI.Memory
{
    /// <summary>
    /// SQLite-backed memory store. Single connection, WAL mode, all access
    /// serialized through one lock (SQLite connections are not thread-safe).
    /// </summary>
    public class MemoryDb : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly object _lock = new();

        public string DbPath { get; }

        public MemoryDb(string dbPath)
        {
            DbPath = dbPath;
            _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared;Pooling=True");
            _conn.Open();
            InitSchema();
        }

        private void InitSchema()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=2000;

CREATE TABLE IF NOT EXISTS accepted_completions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  phrase_id INTEGER,
  context_hash TEXT,
  prefix TEXT,
  suggestion TEXT,
  accepted_at TEXT,
  edited INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS phrase_memory (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  trigger TEXT UNIQUE,
  preferred_text TEXT,
  accepted INTEGER DEFAULT 0,
  rejected INTEGER DEFAULT 0,
  promoted INTEGER DEFAULT 0,
  updated_at TEXT
);

CREATE TABLE IF NOT EXISTS style_patterns (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  key TEXT UNIQUE,
  value TEXT,
  weight INTEGER DEFAULT 1
);

CREATE TABLE IF NOT EXISTS custom_dictionary (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  term TEXT UNIQUE,
  expansion TEXT,
  category TEXT
);

CREATE TABLE IF NOT EXISTS user_profile (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  key TEXT UNIQUE,
  value TEXT
);
";
            cmd.ExecuteNonQuery();

            // Seed the dictionary on first run.
            using (var cmd2 = _conn.CreateCommand())
            {
                cmd2.CommandText = "SELECT COUNT(*) FROM custom_dictionary";
                var count = (long)cmd2.ExecuteScalar();
                if (count == 0)
                {
                    var seeds = new (string term, string expansion)[]
                    {
                        ("NFA", "No further action required"),
                        ("FP", "False Positive"),
                        ("TP", "True Positive"),
                        ("IR", "Incident response"),
                        ("SOC", "Security operations center")
                    };
                    foreach (var (term, expansion) in seeds)
                    {
                        using var ins = _conn.CreateCommand();
                        ins.CommandText = "INSERT OR IGNORE INTO custom_dictionary (term, expansion, category) VALUES (@t, @e, 'seed')";
                        ins.Parameters.AddWithValue("@t", term);
                        ins.Parameters.AddWithValue("@e", expansion);
                        ins.ExecuteNonQuery();
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Phrase memory
        // ------------------------------------------------------------------

        /// <summary>
        /// Record an acceptance. Upserts the phrase keyed by trigger.
        /// Returns the phrase_memory row id.
        /// </summary>
        public long RecordAccept(string trigger, string prefix, string suggestion, string contextHash)
        {
            lock (_lock)
            {
                using var tx = _conn.BeginTransaction();
                try
                {
                    using var upsert = _conn.CreateCommand();
                    upsert.Transaction = tx;
                    upsert.CommandText = @"
INSERT INTO phrase_memory (trigger, preferred_text, accepted, rejected, promoted, updated_at)
VALUES (@t, @s, 1, 0, 0, @now)
ON CONFLICT(trigger) DO UPDATE SET
  accepted = accepted + 1,
  preferred_text = excluded.preferred_text,
  updated_at = @now;
SELECT last_insert_rowid();";
                    upsert.Parameters.AddWithValue("@t", trigger);
                    upsert.Parameters.AddWithValue("@s", suggestion);
                    upsert.Parameters.AddWithValue("@now", Now());
                    long phraseId = (long)upsert.ExecuteScalar();

                    using var ins = _conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = @"
INSERT INTO accepted_completions (phrase_id, context_hash, prefix, suggestion, accepted_at)
VALUES (@pid, @ch, @p, @s, @now)";
                    ins.Parameters.AddWithValue("@pid", phraseId);
                    ins.Parameters.AddWithValue("@ch", contextHash ?? "");
                    ins.Parameters.AddWithValue("@p", prefix);
                    ins.Parameters.AddWithValue("@s", suggestion);
                    ins.Parameters.AddWithValue("@now", Now());
                    ins.ExecuteNonQuery();

                    CheckPromotion(phraseId, tx);
                    tx.Commit();
                    return phraseId;
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        public void RecordReject(string trigger, string suggestion)
        {
            lock (_lock)
            {
                using var tx = _conn.BeginTransaction();
                try
                {
                    using var upsert = _conn.CreateCommand();
                    upsert.Transaction = tx;
                    upsert.CommandText = @"
INSERT INTO phrase_memory (trigger, preferred_text, accepted, rejected, promoted, updated_at)
VALUES (@t, @s, 0, 1, 0, @now)
ON CONFLICT(trigger) DO UPDATE SET
  rejected = rejected + 1,
  updated_at = @now;";
                    upsert.Parameters.AddWithValue("@t", trigger);
                    upsert.Parameters.AddWithValue("@s", suggestion);
                    upsert.Parameters.AddWithValue("@now", Now());
                    upsert.ExecuteNonQuery();

                    CheckPromotionByTrigger(trigger, tx);
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        public void RecordEdit(long completionId)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE accepted_completions SET edited = 1 WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", completionId);
                cmd.ExecuteNonQuery();
            }
        }

        private void CheckPromotion(long phraseId, SqliteTransaction tx)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE phrase_memory
SET promoted = CASE
    WHEN accepted >= 20 AND accepted > rejected * 3 THEN 1
    WHEN rejected > accepted THEN 0
    ELSE promoted
END
WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", phraseId);
            cmd.ExecuteNonQuery();
        }

        private void CheckPromotionByTrigger(string trigger, SqliteTransaction tx)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE phrase_memory
SET promoted = CASE
    WHEN accepted >= 20 AND accepted > rejected * 3 THEN 1
    WHEN rejected > accepted THEN 0
    ELSE promoted
END
WHERE trigger = @t";
            cmd.Parameters.AddWithValue("@t", trigger);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Promoted phrases whose trigger contains the given word(s),
        /// most-accepted first.
        /// </summary>
        public List<(string trigger, string text)> GetPromisedPhrases(string lastWords, int limit = 5)
        {
            lock (_lock)
            {
                var result = new List<(string, string)>();
                if (string.IsNullOrWhiteSpace(lastWords)) return result;

                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
SELECT trigger, preferred_text FROM phrase_memory
WHERE promoted = 1 AND trigger LIKE '%' || @w || '%'
ORDER BY accepted DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@w", lastWords);
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add((r.GetString(0), r.GetString(1)));
                return result;
            }
        }

        public List<(string trigger, string text)> GetTopPhrases(int limit = 10)
        {
            lock (_lock)
            {
                var result = new List<(string, string)>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
SELECT trigger, preferred_text FROM phrase_memory
WHERE accepted > 0 ORDER BY accepted DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add((r.GetString(0), r.GetString(1)));
                return result;
            }
        }

        // ------------------------------------------------------------------
        // Style profile
        // ------------------------------------------------------------------

        public void SetProfile(string key, string value)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO user_profile (key, value) VALUES (@k, @v)
ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value);
                cmd.ExecuteNonQuery();
            }
        }

        public string GetProfile(string key)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM user_profile WHERE key = @k";
                cmd.Parameters.AddWithValue("@k", key);
                var v = cmd.ExecuteScalar();
                return v == null ? null : v.ToString();
            }
        }

        // ------------------------------------------------------------------
        // Dictionary
        // ------------------------------------------------------------------

        public void AddDictionaryTerm(string term, string expansion, string category = "learned")
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
INSERT OR IGNORE INTO custom_dictionary (term, expansion, category) VALUES (@t, @e, @c)";
                cmd.Parameters.AddWithValue("@t", term);
                cmd.Parameters.AddWithValue("@e", expansion);
                cmd.Parameters.AddWithValue("@c", category);
                cmd.ExecuteNonQuery();
            }
        }

        public List<(string term, string expansion)> GetRecentTerms(int limit = 20)
        {
            lock (_lock)
            {
                var result = new List<(string, string)>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT term, expansion FROM custom_dictionary ORDER BY id DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add((r.GetString(0), r.GetString(1)));
                return result;
            }
        }

        // ------------------------------------------------------------------
        // Stats
        // ------------------------------------------------------------------

        public (int phrases, int promoted, int accepts, int edits) GetStats()
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
SELECT
  (SELECT COUNT(*) FROM phrase_memory),
  (SELECT COUNT(*) FROM phrase_memory WHERE promoted = 1),
  (SELECT COUNT(*) FROM accepted_completions),
  (SELECT COUNT(*) FROM accepted_completions WHERE edited = 1)";
                using var r = cmd.ExecuteReader();
                r.Read();
                return ((int)r.GetInt32(0), (int)r.GetInt32(1), (int)r.GetInt32(2), (int)r.GetInt32(3));
            }
        }

        /// <summary>Last N accepted completions (for style profiling).</summary>
        public List<(string prefix, string suggestion, string accepted_at, int edited)>
            GetRecentAccepts(int limit = 200)
        {
            lock (_lock)
            {
                var result = new List<(string, string, string, int)>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
SELECT prefix, suggestion, accepted_at, edited
FROM accepted_completions ORDER BY id DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
                return result;
            }
        }

        /// <summary>
        /// Find a recently accepted completion whose text range contains the
        /// edited position (for edit detection).
        /// </summary>
        public long FindRecentCompletionByPrefix(string prefixFragment, int withinSeconds = 10)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
SELECT id FROM accepted_completions
WHERE prefix LIKE @frag || '%'
  AND accepted_at >= datetime('now', '@off')
ORDER BY id DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@frag", prefixFragment ?? "");
                cmd.Parameters.AddWithValue("@off", $"-{withinSeconds} seconds");
                var v = cmd.ExecuteScalar();
                return v == null ? -1 : (long)v;
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        public void Dispose()
        {
            lock (_lock)
            {
                try { _conn.Close(); } catch { }
            }
            _conn.Dispose();
        }
    }
}
