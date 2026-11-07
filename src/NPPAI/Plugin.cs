using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NPPAI.Config;
using NPPAI.Engine;
using NPPAI.Memory;
using NPPAI.Npp;
using NPPAI.Ui;

namespace NPPAI
{
    /// <summary>
    /// Plugin lifecycle, menu, notification routing and the state machine
    /// that ties context collection, prediction and ghost text together.
    /// All public methods run on the Notepad++ UI thread.
    /// </summary>
    public sealed class Plugin
    {
        // NPP internal messages (not exposed in NppApi.cs).
        private const int NPPM_ADDTOPLUGINMENU = 6000;
        private const int NPPM_GETNPPVERSION = 6001;
        private const int NPPM_GETCURRENTDOC = 6002;
        private const int NPPM_SETMENU = 6003;
        private const int NPPM_GETPLUGINSMENU = 6005;

        // Plugin menu item IDs.
        private const int ID_TOGGLE_AI = 101;
        private const int ID_TEST_CONNECTION = 102;
        private const int ID_SHOW_STATUS = 103;
        private const int ID_OPEN_CONFIG = 104;
        private const int ID_SEPARATOR = 105;
        private const int ID_ABOUT = 106;

        private const string GhostStyleFaceFallback = "Consolas";
        private const int GhostStyle = ScintillaApi.GHOST_STYLE;

        private IntPtr _nppHandle = IntPtr.Zero;
        private IntPtr _sciHandle = IntPtr.Zero;
        private NPP_FUNCTION_LIST _fn;
        private bool _hooked;

        private PluginConfig _config;
        private MemoryDb _db;
        private LearningEngine _learning;
        private StyleProfiler _profiler;
        private OllamaClient _ollama;
        private ContextCollector _collector;
        private PredictionEngine _engine;
        private GhostText _ghost;
        private KeyboardHook _hook;

        // Ghost state (UI thread only).
        private string _ghostHash;          // context hash the ghost belongs to
        private long _lastCompletionId = -1;
        private DateTime _lastCaretMove = DateTime.UtcNow;
        private DateTime _lastTextChange = DateTime.UtcNow;

        // Rooted delegates (must outlive the UI thread calls into them).
        private SetScintillaNotifierDelegate _notifierDelegate;
        private readonly GCHandle _notifierHandle;

        public static Plugin Instance { get; } = new Plugin();

        private Plugin()
        {
            _notifierDelegate = OnScintillaNotification;
            _notifierHandle = GCHandle.Alloc(_notifierDelegate);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public int Hook(NPP_DATA data)
        {
            if (_hooked) return 1;

            _nppHandle = data.nppHandle;
            _sciHandle = data.sciHandle;
            if (data.pNppFunctionList != IntPtr.Zero)
            {
                var p = (NPP_FUNCTION_LIST)Marshal.PtrToStructure(
                    data.pNppFunctionList, typeof(NPP_FUNCTION_LIST));
                _fn = p;
            }

            // Base directory = folder that contains this DLL (plugins dir).
            try
            {
                string dir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir))
                    PluginConfig.SetBaseDirectory(dir);
            }
            catch
            {
                // Fall back to AppContext.BaseDirectory (PluginConfig default).
            }

            _config = PluginConfig.Load();

            string baseDir = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = AppContext.BaseDirectory;

            // Memory (SQLite next to the DLL).
            string dbPath = Path.Combine(baseDir, "NPPAI.db");
            _db = new MemoryDb(dbPath);
            _learning = new LearningEngine(_db);
            _profiler = new StyleProfiler(_db);

            // Prediction pipeline.
            _ollama = new OllamaClient(_config.endpoint, _config.request_timeout_ms);
            _collector = new ContextCollector();
            _engine = new PredictionEngine(
                _ollama, _config, _learning, _profiler, _db, _collector);
            _engine.Result += OnEngineResult;
            _engine.Chunk += OnEngineChunk;

            // UI.
            _ghost = new GhostText(_sciHandle, _config.ghost_color);
            _ghost.EnsureStyle();

            UiDispatcher.Init();

            _hook = new KeyboardHook();
            _hook.Install(
                _nppHandle,
                isActive: () => _hooked && _config.enabled && _ghost.HasActive,
                onTab: () => AcceptGhost(all: true),
                onCtrlRight: () => AcceptGhost(all: false),
                onEsc: () => DismissGhost());

            // Scintilla notifications (SCN_MODIFIED, SCN_UPDATEUI, ...).
            if (_fn.SetScintillaNotifier != IntPtr.Zero)
            {
                _fn.SetScintillaNotifier(data, _notifierDelegate);
            }

            _hooked = true;
            return 1;
        }

        public int Unhook()
        {
            if (!_hooked) return 0;
            _hooked = false;

            try { _engine?.Cancel(); } catch { }
            try { _hook?.Uninstall(); } catch { }
            try { UiDispatcher.Shutdown(); } catch { }

            return 1;
        }

        public int BeforeShow() => 1;

        public int AfterShow() => 1;

        public int Shutdown()
        {
            if (!_hooked) return 0;
            _hooked = false;

            try { _engine?.Dispose(); } catch { }
            try { _ollama?.Dispose(); } catch { }
            try { _db?.Dispose(); } catch { }

            _notifierHandle.Free();
            _engine = null;
            _ollama = null;
            _db = null;
            _learning = null;
            _profiler = null;
            _collector = null;
            _ghost = null;
            _hook = null;

            return 1;
        }

        // ------------------------------------------------------------------
        // Plugin menu (Plugins > NPPAI)
        // ------------------------------------------------------------------

        public int CmdMenu(int item, int p)
        {
            if (p == 0) return 0;
            var scn = (SCN_MENU)Marshal.PtrToStructure(p, typeof(SCN_MENU));
            if (scn.pMenu == IntPtr.Zero || scn.items <= 0) return 0;

            int size = Marshal.SizeOf(typeof(MENUITEMINFO));
            int count = Math.Min(scn.items, 6);

            for (int i = 0; i < count; i++)
            {
                IntPtr slot = IntPtr.Add(scn.pMenu, i * size);
                var mi = (MENUITEMINFO)Marshal.PtrToStructure(slot, typeof(MENUITEMINFO));

                mi.cbSize = (uint)size;
                mi.fMask = 0x02 | 0x10; // MIIM_ID | MIIM_TYPE
                mi.wID = 0;

                switch (i)
                {
                    case 0:
                        mi.wID = ID_TOGGLE_AI;
                        mi.fType = 0; // MFT_STRING
                        mi.dwTypeData = "Toggle AI Assist";
                        break;
                    case 1:
                        mi.wID = ID_TEST_CONNECTION;
                        mi.fType = 0;
                        mi.dwTypeData = "Test Connection";
                        break;
                    case 2:
                        mi.wID = ID_SHOW_STATUS;
                        mi.fType = 0;
                        mi.dwTypeData = "Show Status";
                        break;
                    case 3:
                        mi.wID = ID_OPEN_CONFIG;
                        mi.fType = 0;
                        mi.dwTypeData = "Open Config";
                        break;
                    case 4:
                        mi.wID = ID_SEPARATOR;
                        mi.fType = 0x800; // MFT_SEPARATOR
                        mi.dwTypeData = null;
                        break;
                    case 5:
                        mi.wID = ID_ABOUT;
                        mi.fType = 0;
                        mi.dwTypeData = "About NPPAI";
                        break;
                }

                Marshal.StructureToPtr(mi, slot, false);
            }

            return count;
        }

        public int MenuCommand(int item, int p)
        {
            if (!_hooked) return 0;

            switch (item)
            {
                case ID_TOGGLE_AI:
                    ToggleAi();
                    break;
                case ID_TEST_CONNECTION:
                    TestConnection();
                    break;
                case ID_SHOW_STATUS:
                    ShowStatus();
                    break;
                case ID_OPEN_CONFIG:
                    OpenConfig();
                    break;
                case ID_ABOUT:
                    Dialogs.About(_nppHandle);
                    break;
            }
            return 1;
        }

        // ------------------------------------------------------------------
        // Menu actions
        // ------------------------------------------------------------------

        private void ToggleAi()
        {
            _config.enabled = !_config.enabled;
            try { _config.Save(); } catch { }
            if (!_config.enabled)
            {
                _engine?.Cancel();
                _ghost?.Clear();
                _ghostHash = null;
            }
            else
            {
                MaybeRequest();
            }
        }

        private void TestConnection()
        {
            string result;
            try
            {
                var client = new OllamaClient(_config.endpoint, 5000);
                var sw = Stopwatch.StartNew();
                string version = client.PingAsync().GetAwaiter().GetResult();
                sw.Stop();
                var models = client.ListModelsAsync().GetAwaiter().GetResult();
                result = "Ollama reachable at " + _config.endpoint +
                         " (v" + version + ", " + sw.ElapsedMilliseconds + " ms).\n" +
                         "Models: " + (models.Count > 0 ? string.Join(", ", models) : "(none)") +
                         "\n\nConfigured model: " + _config.model;
                client.Dispose();
            }
            catch (Exception ex)
            {
                result = "Ollama not reachable at " + _config.endpoint +
                         "\n" + ex.Message +
                         "\n\nIs Ollama running? (ollama serve)";
            }
            Dialogs.Status(_nppHandle, result);
        }

        private void ShowStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("NPPAI status");
            sb.AppendLine("Model: " + _config.model);
            sb.AppendLine("Endpoint: " + _config.endpoint);
            sb.AppendLine("AI Assist: " + (_config.enabled ? "ON" : "OFF"));
            if (_engine != null)
            {
                sb.AppendLine("Requests: " + _engine.RequestsOk + " ok / " +
                              _engine.RequestsFailed + " failed");
                sb.AppendLine("Last latency: " + _engine.LastLatencyMs + " ms");
            }
            try
            {
                if (_db != null)
                {
                    var s = _db.GetStats();
                    sb.AppendLine("Memory: " + s.phrases + " phrases (" +
                                  s.promoted + " promoted), " + s.accepts +
                                  " accepts, " + s.edits + " edits");
                }
            }
            catch { }
            Dialogs.Status(_nppHandle, sb.ToString().TrimEnd());
        }

        private void OpenConfig()
        {
            try
            {
                string path = PluginConfig.ConfigPath;
                if (!File.Exists(path))
                {
                    _config.Save();
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Dialogs.Status(_nppHandle, "Could not open config:\n" + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Scintilla notifications
        // ------------------------------------------------------------------

        private void OnScintillaNotification(IntPtr pNppData, IntPtr pScn)
        {
            if (!_hooked || pScn == IntPtr.Zero) return;

            SCNotification scn;
            try
            {
                scn = (SCNotification)Marshal.PtrToStructure(pScn, typeof(SCNotification));
            }
            catch
            {
                return;
            }

            switch (scn.iCode)
            {
                case ScintillaApi.SCN_MODIFIED:
                    OnModified(scn);
                    break;

                case ScintillaApi.SCN_UPDATEUI:
                    OnUpdateUi(scn);
                    break;
            }
        }

        private void OnModified(SCNotification scn)
        {
            // SCN_MODIFIED: wParam = modification bitmask (SC_MOD_*),
            // lParam = SCModified* { int modification; int modifiedTextType; SC_TEXTCHANGED* text; }
            bool textChanged = false;
            try
            {
                if (scn.lParam != IntPtr.Zero)
                {
                    var mod = (SCModified)Marshal.PtrToStructure(scn.lParam, typeof(SCModified));
                    textChanged = mod.modifiedTextType != 0;
                }
            }
            catch
            {
                textChanged = true; // be safe: treat as text change
            }

            if (!textChanged) return;

            _lastTextChange = DateTime.UtcNow;
            InvalidateGhost();

            // Restart the debounce pipeline.
            MaybeRequest();
        }

        private void OnUpdateUi(SCNotification scn)
        {
            // Caret / selection / scroll changed.
            try
            {
                int caret = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_GETSELECTIONSTART);
                int selEnd = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_GETSELECTIONEND);
                int line = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_LINEFROMPOSITION, caret, 0);

                bool caretMoved = caret != _lastCaretPos || line != _lastCaretLine;
                _lastCaretPos = caret;
                _lastCaretLine = line;

                if (caretMoved)
                {
                    _lastCaretMove = DateTime.UtcNow;

                    // Ghost only at the end of the caret line.
                    if (_ghost != null && _ghost.HasActive && line != _ghost.ActiveLine)
                    {
                        InvalidateGhost();
                    }
                }

                // Selections are not a place for ghost text.
                if (caret != selEnd)
                {
                    InvalidateGhost();
                }
                else if (caretMoved)
                {
                    MaybeRequest();
                }
            }
            catch
            {
                // Never let a notification handler take NPP down.
            }
        }

        private int _lastCaretPos = -1;
        private int _lastCaretLine = -1;

        // ------------------------------------------------------------------
        // Ghost lifecycle
        // ------------------------------------------------------------------

        private void MaybeRequest()
        {
            if (!_hooked || _engine == null || _ghost == null) return;
            if (!_config.enabled || !_config.enable_ghost_text) return;

            // Idle guard: no requests if the caret hasn't moved and no text
            // changed for 60 s.
            if (_lastCaretMove.AddSeconds(60) < DateTime.UtcNow &&
                _lastTextChange.AddSeconds(60) < DateTime.UtcNow)
            {
                return;
            }

            var ctx = _collector.Collect(_sciHandle, _config.context_chars);
            if (ctx == null) return;

            // Ghost only at the end of the caret line.
            if (!ctx.CaretAtLineEnd)
            {
                if (_ghost.HasActive) InvalidateGhost();
                return;
            }

            // Don't re-request for the exact same context.
            if (ctx.ContextHash == _ghostHash) return;

            _engine.Request(ctx);
        }

        private void InvalidateGhost()
        {
            _engine?.Cancel();
            _ghost?.Clear();
            _ghostHash = null;
        }

        private void OnEngineChunk(string fullText)
        {
            // Worker thread -> UI thread. Coalesced by the dispatcher.
            UiDispatcher.Post(() =>
            {
                if (!_hooked || _ghost == null) return;
                if (string.IsNullOrEmpty(fullText)) return;

                int line = ScintillaApi.SendInt(_sciHandle,
                    ScintillaApi.SCI_LINEFROMPOSITION,
                    ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_GETSELECTIONSTART), 0);

                _ghost.Set(line, fullText);
            });
        }

        private void OnEngineResult(PredictionResult r)
        {
            // Worker thread -> UI thread.
            UiDispatcher.Post(() =>
            {
                if (!_hooked || _ghost == null || _engine == null) return;

                if (!r.Success)
                {
                    _engine.Cancel();
                    _ghost.Clear();
                    _ghostHash = null;
                    return;
                }

                // Stale guard: context changed while the request was in flight.
                if (r.ContextHash != null && r.ContextHash != _pendingHash)
                {
                    _ghost.Clear();
                    return;
                }

                if (string.IsNullOrEmpty(r.Text))
                {
                    _ghost.Clear();
                    _ghostHash = null;
                    return;
                }

                int caret = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_GETSELECTIONSTART);
                int line = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_LINEFROMPOSITION, caret, 0);

                // Ghost only at the end of the caret line.
                int linePos = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_POSITIONFROMLINE, line, 0);
                int lineLen = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_LINELENGTH, line, 0);
                if (caret < linePos + lineLen)
                {
                    _ghost.Clear();
                    return;
                }

                _ghost.Set(line, r.Text);
                _ghostHash = r.ContextHash;
            });
        }

        // The context hash the in-flight request was made for.
        private string _pendingHash;

        // ------------------------------------------------------------------
        // Accept / dismiss (called from the keyboard hook, UI thread)
        // ------------------------------------------------------------------

        private void AcceptGhost(bool all)
        {
            if (_ghost == null || !_ghost.HasActive) return;

            string text = all ? _ghost.ActiveText : _ghost.NextWord();
            if (string.IsNullOrEmpty(text)) return;

            int caret = ScintillaApi.SendInt(_sciHandle, ScintillaApi.SCI_GETSELECTIONSTART);

            // Insert at the caret (no undo grouping needed for a single insert).
            ScintillaApi.SendStringParam(_sciHandle, ScintillaApi.SCI_INSERTTEXT, caret, text);

            // Learning: record the acceptance.
            try
            {
                if (_config.enable_learning && _db != null)
                {
                    var ctx = _collector.Collect(_sciHandle, _config.context_chars);
                    string prefix = ctx != null ? ctx.LineText : "";
                    string trigger = LearningEngine.MakeTrigger(prefix);
                    _lastCompletionId = _learning.RecordAccept(trigger, prefix, text, ctx != null ? ctx.ContextHash : null);
                }
            }
            catch
            {
                // Learning must never break the editor.
            }

            _ghost.Clear();
            _ghostHash = null;
        }

        private void DismissGhost()
        {
            _engine?.Cancel();
            _ghost?.Clear();
            _ghostHash = null;
        }
    }
}
