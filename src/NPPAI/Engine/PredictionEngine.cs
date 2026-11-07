using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NPPAI.Config;
using NPPAI.Memory;

namespace NPPAI.Engine
{
    public enum EngineState { Idle, Waiting, Requesting, GhostActive }

    public class PredictionResult
    {
        public string Text;
        public string ContextHash; // the context this result was computed from
        public long ElapsedMs;
        public bool Success;
        public string Error;
    }

    /// <summary>
    /// Debounced, cancellable prediction pipeline.
    ///
    /// Threading: Request() is called from the UI thread (via notification
    /// handlers). The Ollama call runs on a worker thread. Results are
    /// delivered via the onResult callback, which the caller marshals to the
    /// UI thread.
    /// </summary>
    public class PredictionEngine : IDisposable
    {
        private readonly OllamaClient _ollama;
        private readonly PluginConfig _config;
        private readonly LearningEngine _learning;
        private readonly StyleProfiler _profiler;
        private readonly MemoryDb _db;
        private readonly ContextCollector _collector;

        private CancellationTokenSource _cts;
        private Timer _debounce;
        private EngineState _state = EngineState.Idle;
        private string _pendingContextHash;
        private string _pendingContext;
        private DateTime _lastRequestEnd = DateTime.MinValue;
        private int _consecutiveFailures;
        private DateTime _backoffUntil = DateTime.MinValue;

        public event Action<PredictionResult> Result;
        public event Action<string> Chunk; // incremental text (UI marshalling is the listener's job)

        public int RequestsOk { get; private set; }
        public int RequestsFailed { get; private set; }
        public long LastLatencyMs { get; private set; }

        public EngineState State => _state;

        public PredictionEngine(
            OllamaClient ollama,
            PluginConfig config,
            LearningEngine learning,
            StyleProfiler profiler,
            MemoryDb db,
            ContextCollector collector)
        {
            _ollama = ollama;
            _config = config;
            _learning = learning;
            _profiler = profiler;
            _db = db;
            _collector = collector;
        }

        /// <summary>
        /// Schedule a prediction. `context` is the already-collected context
        /// (UI thread). Cancels any in-flight request.
        /// </summary>
        public void Request(CollectedContext context)
        {
            Cancel();
            if (!_config.enabled || !_config.enable_ghost_text) return;

            _pendingContext = context.Text;
            _pendingContextHash = context.ContextHash;
            _state = EngineState.Waiting;

            _debounce?.Dispose();
            _debounce = new Timer(_ => OnDebounceFired(), null,
                Math.Max(100, _config.debounce_ms), Timeout.Infinite);
        }

        public void Cancel()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            if (_state == EngineState.Requesting || _state == EngineState.Waiting)
                _state = EngineState.Idle;
        }

        private void OnDebounceFired()
        {
            if (_state != EngineState.Waiting) return;

            if (DateTime.UtcNow < _backoffUntil)
            {
                _state = EngineState.Idle;
                return;
            }

            var ctxText = _pendingContext;
            var ctxHash = _pendingContextHash;
            if (string.IsNullOrWhiteSpace(ctxText))
            {
                _state = EngineState.Idle;
                return;
            }

            _state = EngineState.Requesting;
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            Task.Run(() => RunRequest(ctxText, ctxHash, sw), _cts.Token);
        }

        private void RunRequest(string contextText, string contextHash, Stopwatch sw)
        {
            try
            {
                _profiler.EnsureProfile();
                var profile = _profiler.GetProfile();

                // Last words of the context, for phrase retrieval.
                string lastWords = "";
                var lastNonWs = Regex.Match(contextText, @"\S+\s+\S+$");
                if (lastNonWs.Success) lastWords = lastNonWs.Value.Trim();
                else
                {
                    var single = Regex.Match(contextText, @"\S+$");
                    if (single.Success) lastWords = single.Value;
                }

                List<(string, string)> phrases = null;
                List<(string, string)> terms = null;
                if (_config.enable_memory)
                {
                    phrases = _db.GetPromisedPhrases(lastWords, 5);
                    if (phrases.Count == 0)
                        phrases = _db.GetTopPhrases(5);
                    terms = _db.GetRecentTerms(20);
                }

                var messages = PromptBuilder.Build(
                    new CollectedContext { Text = contextText, ContextHash = contextHash },
                    _config.enable_memory, phrases,
                    _config.enable_memory ? profile : null, terms);

                string full = "";
                var sw2 = Stopwatch.StartNew();
                full = _ollama.StreamChatAsync(
                    _config.model,
                    messages,
                    _config.temperature,
                    _config.max_prediction_words * 2,
                    onChunk: delta =>
                    {
                        full += delta;
                        Chunk?.Invoke(full);
                    },
                    _cts.Token).GetAwaiter().GetResult();

                sw2.Stop();
                full = TrimCompletion(full, contextText);

                _state = EngineState.Idle;
                RequestsOk++;
                LastLatencyMs = sw.ElapsedMilliseconds;

                Result?.Invoke(new PredictionResult
                {
                    Text = full,
                    ContextHash = contextHash,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Success = true
                });
            }
            catch (OperationCanceledException)
            {
                _state = EngineState.Idle;
                // cancelled — not a failure
            }
            catch (Exception ex)
            {
                _state = EngineState.Idle;
                RequestsFailed++;
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3)
                    _backoffUntil = DateTime.UtcNow.AddSeconds(30);

                Result?.Invoke(new PredictionResult
                {
                    Text = null,
                    ContextHash = contextHash,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Clean up raw model output: strip leading whitespace/newlines,
        /// drop anything after a blank line, cap word count, strip a leading
        /// character that repeats the last typed character.
        /// </summary>
        public static string TrimCompletion(string raw, string context)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.TrimStart();
            int blank = s.IndexOf("\n\n", StringComparison.Ordinal);
            if (blank >= 0) s = s.Substring(0, blank);
            s = s.Trim();

            // Cap at max words.
            // (caller passes max via config; here we cap at 20 as a hard max)
            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 20)
                s = string.Join(" ", words[0..20]);

            // If the first char equals the last non-space char of the context,
            // the model likely repeated the cursor char.
            if (s.Length > 0 && context != null && context.Length > 0)
            {
                char last = context[context.Length - 1];
                if (!char.IsWhiteSpace(last) && s[0] == last)
                    s = s.Substring(1).TrimStart();
            }
            return s;
        }

        public void Dispose()
        {
            Cancel();
            _debounce?.Dispose();
        }
    }
}
