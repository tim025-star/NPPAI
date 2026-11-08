using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using NPPAI.Config;
using NPPAI.Engine;
using NPPAI.Memory;
using NPPAI.Npp;

namespace NPPAI;

[StructLayout(LayoutKind.Sequential)]
public struct NppData { public nint Main, Primary, Secondary; }
[StructLayout(LayoutKind.Sequential)]
public struct Header { public nint Window, Id; public uint Code; }

public static unsafe class Exports
{
    static nint name = Marshal.StringToHGlobalUni("NPPAI");
    [UnmanagedCallersOnly(EntryPoint = "isUnicode", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int IsUnicode() => 1;
    [UnmanagedCallersOnly(EntryPoint = "getName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static nint GetName() => name;
    [UnmanagedCallersOnly(EntryPoint = "setInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
    [DNNE.C99DeclCode("struct NppData { void* main; void* primary; void* secondary; };")]
    public static void SetInfo([DNNE.C99Type("struct NppData")] NppData data) { try { Plugin.Init(data); } catch (Exception ex) { Plugin.Log(ex); } }
    [UnmanagedCallersOnly(EntryPoint = "getFuncsArray", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static nint GetFuncsArray(int* count) { *count = Plugin.CommandCount; return Plugin.Commands; }
    [UnmanagedCallersOnly(EntryPoint = "messageProc", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static nint MessageProc(uint msg, nint w, nint l) => 1;
    [UnmanagedCallersOnly(EntryPoint = "beNotified", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Notify(nint notification) { try { Plugin.Notify(Marshal.PtrToStructure<Header>(notification), notification); } catch (Exception ex) { Plugin.Log(ex); } }
}

public static class Plugin
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Command();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Item { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name; public nint Function; public int Id; public byte Checked; public nint Shortcut; }
    static readonly Command[] callbacks = { Toggle, Settings, Status, RequestNow };
    public const int CommandCount = 4;
    public static readonly nint Commands = MakeCommands();
    static nint MakeCommands()
    {
        string[] names = { "Enable / disable suggestions", "Settings and models...", "Status...", "Suggest now" };
        nint result = Marshal.AllocHGlobal(Marshal.SizeOf<Item>() * CommandCount);
        for (int i = 0; i < CommandCount; i++) Marshal.StructureToPtr(new Item { Name = names[i], Function = Marshal.GetFunctionPointerForDelegate(callbacks[i]) }, result + i * Marshal.SizeOf<Item>(), false);
        return result;
    }
    static NppData data;
    static PluginConfig config;
    static MemoryDb db;
    static LearningEngine learning;
    static StyleProfiler profiler;
    static OllamaClient client;
    static CorrectionService corrections;
    static Control dispatcher;
    static System.Windows.Forms.Timer debounce;
    static Ghost ghost;
    static EditorHook primary, secondary;
    static MainHook mainHook;
    static CancellationTokenSource pending;
    static long generation;
    static bool closed, inserting;
    static string suggestion = "", prefix = "", error = "", ghostHash;
    static nint ghostEditor, ghostDocument;
    static int ghostCaret, requests;
    static long latency;
    static string lastPosition;
    static long acceptedId = -1;
    static int acceptedStart, acceptedEnd;
    static nint acceptedDocument;
    static readonly ContextCollector collector = new();
    static string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPPAI");
    public static void Log(Exception ex) { try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "NPPAI.log"), DateTime.Now + " " + ex + Environment.NewLine); } catch { } }
    public static void Init(NppData value, string storageDirectory = null)
    {
        if (storageDirectory != null) directory = storageDirectory;
        data = value;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(directory);
        PluginConfig.SetBaseDirectory(directory);
        config = PluginConfig.Load();
        db = new MemoryDb(Path.Combine(directory, "memory.db"));
        learning = new LearningEngine(db); profiler = new StyleProfiler(db);
        client = new OllamaClient(config.endpoint, config.request_timeout_ms);
        dispatcher = new Control(); _ = dispatcher.Handle;
        ghost = new Ghost();
        debounce = new System.Windows.Forms.Timer { Interval = config.debounce_ms };
        debounce.Tick += (_, _) => { debounce.Stop(); RequestNow(); };
        primary = new EditorHook(data.Primary); secondary = new EditorHook(data.Secondary);
        mainHook = new MainHook(data.Main);
        corrections = new CorrectionService(Editor, config, Post, action =>
        {
            ClearPrediction(); inserting = true;
            try { action(); }
            finally { inserting = false; }
            if (config.enabled && config.enable_ghost_text) debounce.Start();
        }, () => db.GetProtectedTerms());
    }
    static nint Editor()
    {
        nint p = Marshal.AllocHGlobal(4);
        try { Marshal.WriteInt32(p, 0); ScintillaApi.Send(data.Main, 1024 + 1000 + 4, 0, p); return Marshal.ReadInt32(p) == 0 ? data.Primary : data.Secondary; }
        finally { Marshal.FreeHGlobal(p); }
    }
    public static void Notify(Header h, nint notification)
    {
        if (h.Code == 1009) { Shutdown(); return; } // NPPN_SHUTDOWN
        if (closed || config == null) return;
        if (h.Code == 1005) { corrections?.ForgetDocuments(); ClearPrediction(); return; } // NPPN_FILECLOSED
        if (h.Code == 1010 || h.Code == 1001) { Clear(); Schedule(); return; } // buffer activated / ready
        if ((h.Window == data.Primary || h.Window == data.Secondary) && !inserting && (h.Code == 2008 || h.Code == 2007))
        {
            if (h.Code == 2008)
            {
                // Only inserted/deleted text, not styling, markers or before-change events.
                if ((Marshal.ReadInt32(notification, 40) & 3) != 0)
                {
                    int position = (int)Marshal.ReadIntPtr(notification, 24);
                    corrections?.ObserveEdit(Marshal.ReadInt32(notification, 40), h.Window, position, (int)Marshal.ReadIntPtr(notification, 56));
                    if (config.enable_learning && acceptedId > 0 && ScintillaApi.Send(h.Window, 2357) == acceptedDocument && position >= acceptedStart && position < acceptedEnd)
                    { db.RecordEdit(acceptedId); acceptedId = -1; }
                    Schedule();
                }
            }
            else
            {
                string position = $"{h.Window}:{ScintillaApi.Send(h.Window, 2357)}:{ScintillaApi.Send(h.Window, 2008)}:{ScintillaApi.Send(h.Window, 2009)}";
                if (position != lastPosition) { lastPosition = position; Schedule(); }
            }
        }
    }
    static void Clear()
    {
        generation++; corrections?.Cancel(); ClearPrediction();
    }
    static void ClearPrediction()
    {
        generation++;
        var old = pending; pending = null;
        if (old != null) { old.Cancel(); old.Dispose(); }
        debounce?.Stop(); suggestion = ""; ghost?.Hide();
    }
    static void Schedule()
    {
        ClearPrediction();
        if (config.enabled && config.enable_ghost_text) { debounce.Interval = config.debounce_ms; debounce.Start(); }
        corrections?.Schedule();
    }
    static bool IsCurrent() => !closed && Editor() == ghostEditor && ScintillaApi.Send(ghostEditor, 2357) == ghostDocument && (int)ScintillaApi.Send(ghostEditor, 2008) == ghostCaret && ScintillaApi.Send(ghostEditor, 2009) == ghostCaret && collector.Collect(ghostEditor, config.context_chars).ContextHash == ghostHash;
    static void RequestNow()
    {
        if (closed || config == null || !config.enabled) return;
        ClearPrediction();
        nint sci = Editor();
        if (ScintillaApi.Send(sci, 2143) != ScintillaApi.Send(sci, 2145) || ScintillaApi.Send(sci, 2140) != 0 || ScintillaApi.Send(sci, 2570) != 1) return;
        var ctx = collector.Collect(sci, config.context_chars);
        if (!ctx.CaretAtLineEnd || string.IsNullOrWhiteSpace(ctx.Text)) return;
        long version = generation;
        nint document = ScintillaApi.Send(sci, 2357);
        var cts = new CancellationTokenSource(); pending = cts;
        var token = cts.Token;
        var model = config.model; var maxWords = config.max_prediction_words;
        var useMemory = config.enable_memory; var temperature = config.temperature;
        var activeClient = client;
        _ = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                if (useMemory) profiler.EnsureProfile();
                var phrases = useMemory ? db.GetPromisedPhrases(LearningEngine.MakeTrigger(ctx.Text), 5) : null;
                var terms = useMemory ? db.GetRecentTerms(100).Where(t => ctx.Text.Contains(t.term, StringComparison.OrdinalIgnoreCase)).Take(10).ToList() : null;
                var messages = PromptBuilder.Build(ctx, useMemory, phrases, useMemory ? profiler.GetProfile() : null, terms, config.writing_profile, maxWords);
                string streamed = "";
                long lastPaint = -100;
                Action<string, bool> display = (value, finished) => Post(() =>
                {
                    if (generation != version || closed || Editor() != sci || ScintillaApi.Send(sci, 2357) != document || collector.Collect(sci, config.context_chars).ContextHash != ctx.ContextHash || (int)ScintillaApi.Send(sci, 2008) != ctx.CaretPos) return;
                    if (finished) requests++;
                    latency = watch.ElapsedMilliseconds; error = "";
                    suggestion = PredictionResponse.Decode(value, ctx.Text, maxWords, config.writing_profile); prefix = ctx.Text; ghostEditor = sci; ghostDocument = document; ghostCaret = ctx.CaretPos; ghostHash = ctx.ContextHash;
                    if (suggestion.Length > 0) ghost.Display(sci, ctx.CaretPos, suggestion, config.GhostColorBgr());
                    else ghost.Hide();
                });
                string text = await activeClient.StreamChatAsync(model, messages, temperature, maxWords * 4 + 64, delta =>
                {
                    streamed += delta;
                    if (watch.ElapsedMilliseconds - lastPaint >= 50) { lastPaint = watch.ElapsedMilliseconds; display(streamed, false); }
                }, token, PredictionResponse.Format(ctx.Text));
                if (token.IsCancellationRequested) return;
                display(text, true);
            }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) Post(() => { if (generation == version) { ClearPrediction(); error = "Ollama request timed out."; } }); }
            catch (Exception ex) { Log(ex); Post(() => { if (generation == version) { ClearPrediction(); error = ex.Message; } }); }
            // Sources are released after the UI has detached them in Clear().
        });
    }
    static void Post(Action action) { try { if (!closed && dispatcher != null && !dispatcher.IsDisposed) dispatcher.BeginInvoke((Action)(() => { try { action(); } catch (Exception ex) { Log(ex); error = ex.Message; ClearPrediction(); } })); } catch (InvalidOperationException) { } }
    internal static bool Key(Keys key)
    {
        if (suggestion.Length == 0) return false;
        if (!IsCurrent()) { ClearPrediction(); return false; }
        if (key == Keys.Escape) { if (config.enable_learning) learning.RecordReject(prefix, suggestion); ClearPrediction(); return true; }
        if (key != Keys.Tab && key != (Keys.Control | Keys.Right)) return false;
        string accepted = key == Keys.Tab ? suggestion : Completion.NextWord(suggestion);
        string rest = suggestion.Substring(accepted.Length);
        string oldPrefix = prefix;
        ClearPrediction(); inserting = true;
        try { ScintillaApi.Send(ghostEditor, 2078); ScintillaApi.Insert(ghostEditor, accepted); }
        finally { ScintillaApi.Send(ghostEditor, 2079); inserting = false; }
        corrections?.ObserveEdit(1, ghostEditor, ghostCaret, ScintillaApi.EncodingFor(ghostEditor).GetByteCount(accepted));
        corrections?.Schedule();
        if (config.enable_learning)
        {
            acceptedId = learning.RecordAccept(oldPrefix, accepted, ghostHash);
            acceptedStart = ghostCaret; acceptedEnd = (int)ScintillaApi.Send(ghostEditor, 2008); acceptedDocument = ghostDocument;
        }
        if (rest.Length > 0)
        {
            var ctx = collector.Collect(ghostEditor, config.context_chars);
            suggestion = rest; prefix = ctx.Text; ghostCaret = ctx.CaretPos; ghostHash = ctx.ContextHash;
            ghost.Display(ghostEditor, ghostCaret, rest, config.GhostColorBgr());
        }
        return true;
    }
    static void Toggle() { try { config.enabled = !config.enabled; config.Save(); ClearPrediction(); } catch (Exception ex) { Log(ex); } }
    static void Status()
    {
        try
        {
            var stats=db.GetStats();
            string message=$"Prediction model: {config.model}\nSuggestions: {(config.enabled ? "enabled" : "disabled")}\nPrediction requests completed: {requests}\nLast prediction latency: {latency} ms\nPrediction error: {error}\n\nCorrection model: {config.correction_model}\nAutomatic corrections: {(config.enable_corrections ? "enabled" : "disabled")}\nCorrection state: {corrections.Phase}\nCorrection requests: {corrections.RequestsStarted} started / {corrections.RequestsCompleted} finished / {corrections.RequestsCancelled} cancelled\nLast correction request: {corrections.LastRequestMs} ms\nCorrections applied: {corrections.Applied}\nProposals skipped: {corrections.Rejected}\nLast correction result: {corrections.LastOutcome}\nCorrection error: {corrections.LastError}\n\nMemory: {stats.accepts} acceptances, {stats.promoted} trusted phrases\nData: {directory}";
            MessageBox.Show(message,"NPPAI");
        }
        catch(Exception ex){Log(ex);}
    }
    static async void Settings()
    {
        try
        {
            Clear();
            using var form = new Form { Text = "NPPAI settings", Width = 460, Height = 470, StartPosition = FormStartPosition.CenterScreen };
            var endpoint = new TextBox { Left = 120, Top = 20, Width = 300, Text = config.endpoint };
            var model = new ComboBox { Left = 120, Top = 60, Width = 300, Text = config.model };
            var debounceInput = new NumericUpDown { Left = 120, Top = 100, Minimum = 100, Maximum = 1000, Value = config.debounce_ms };
            var memory = new CheckBox { Left = 120, Top = 140, Width = 250, Text = "Use personal memory", Checked = config.enable_memory };
            var learn = new CheckBox { Left = 120, Top = 170, Width = 250, Text = "Learn from accepted suggestions", Checked = config.enable_learning };
            var correct = new CheckBox { Left = 120, Top = 205, Width = 290, Text = "Automatically correct completed words", Checked = config.enable_corrections };
            var correctionModel = new ComboBox { Left = 120, Top = 240, Width = 300, Text = config.correction_model };
            form.Controls.Add(new Label { Left = 10, Top = 243, Width = 110, Text = "Correction model" });
            var profile = new ComboBox { Left = 120, Top = 280, Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            profile.Items.AddRange(new object[] { "soc", "general" }); profile.SelectedItem = config.writing_profile;
            var words = new NumericUpDown { Left = 120, Top = 315, Minimum = 1, Maximum = 50, Value = config.max_prediction_words };
            form.Controls.Add(new Label { Left = 10, Top = 283, Width = 110, Text = "Writing profile" });
            form.Controls.Add(new Label { Left = 10, Top = 318, Width = 110, Text = "Prediction words" });
            var save = new Button { Left = 320, Top = 370, Text = "Save", DialogResult = DialogResult.OK };
            var refresh = new Button { Left = 120, Top = 370, Width = 150, Text = "Refresh models" };
            foreach (var pair in new[] { ("Ollama endpoint",20), ("Prediction model",60), ("Debounce (ms)",100) }) form.Controls.Add(new Label { Left = 10, Top = pair.Item2 + 3, Width = 110, Text = pair.Item1 });
            form.Controls.AddRange(new Control[] { endpoint, model, debounceInput, memory, learn, correct, correctionModel, profile, words, save, refresh });
            async Task LoadModels() { try { using var probe = new OllamaClient(endpoint.Text, 5000); var names = await probe.ListModelsAsync(); if (!form.IsDisposed) { model.Items.Clear(); model.Items.AddRange(names.Cast<object>().ToArray()); correctionModel.Items.Clear(); correctionModel.Items.AddRange(names.Cast<object>().ToArray()); } } catch (Exception ex) { if (!form.IsDisposed) MessageBox.Show(form, ex.Message, "Ollama connection"); } }
            refresh.Click += async (_, _) => await LoadModels();
            form.Shown += async (_, _) => await LoadModels();
            if (form.ShowDialog() != DialogResult.OK) return;
            if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var uri) || !uri.IsLoopback || (uri.Scheme != "http" && uri.Scheme != "https")) { MessageBox.Show("Use a local Ollama HTTP endpoint.", "NPPAI"); return; }
            config.endpoint = endpoint.Text; config.model = model.Text; config.debounce_ms = (int)debounceInput.Value; config.enable_memory = memory.Checked; config.enable_learning = learn.Checked; config.enable_corrections = correct.Checked; config.correction_model = correctionModel.Text;
            config.writing_profile = (string)profile.SelectedItem; config.max_prediction_words = (int)words.Value; config.Clamp(); config.Save();
            var oldClient = client;
            client = new OllamaClient(config.endpoint, config.request_timeout_ms);
            oldClient.Dispose();
        }
        catch (Exception ex) { Log(ex); }
        finally { if (!closed) corrections?.Schedule(); }
        await Task.CompletedTask;
    }
    static void Shutdown() { closed = true; Clear(); corrections?.Dispose(); primary?.ReleaseHandle(); secondary?.ReleaseHandle(); mainHook?.ReleaseHandle(); ghost?.Dispose(); debounce?.Dispose(); dispatcher?.Dispose(); client?.Dispose(); }
    sealed class MainHook : NativeWindow
    {
        public MainHook(nint handle) { AssignHandle(handle); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 3 || m.Msg == 5 || (m.Msg == 6 && (int)m.WParam == 0)) ClearPrediction();
            base.WndProc(ref m);
        }
    }
    sealed class EditorHook : NativeWindow
    {
        bool swallowTab;
        public EditorHook(nint handle) { AssignHandle(handle); }
        protected override void WndProc(ref Message m)
        {
            try
            {
                if (m.Msg == 0x100 && Key((Keys)(int)m.WParam | Control.ModifierKeys)) { swallowTab = (int)m.WParam == 9; m.Result = 0; return; }
                if (m.Msg == 0x102 && swallowTab && (int)m.WParam == 9) { swallowTab = false; m.Result = 0; return; }
                if (m.Msg == 0x100) swallowTab = false;
                if (m.Msg == 0x8 || m.Msg == 0x20A) ClearPrediction();
            }
            catch (Exception ex) { Log(ex); ClearPrediction(); }
            base.WndProc(ref m);
        }
    }
    sealed class Ghost : Form
    {
        string text = "";
        public Ghost() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; DoubleBuffered = true; }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000020; return p; } }
        [DllImport("user32.dll")] static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out Rect rect);
        public void Display(nint sci, int caret, string value, int color)
        {
            text = value.Replace("\r", " ").Replace("\n", " ");
            var point = new Point((int)ScintillaApi.Send(sci, 2164, 0, caret), (int)ScintillaApi.Send(sci, 2165, 0, caret));
            int height = (int)ScintillaApi.Send(sci, 2279, 0);
            if (point.X < 0 || point.Y < 0) { Hide(); return; }
            GetClientRect(sci, out var bounds);
            int availableWidth = bounds.Right - point.X - 2;
            if (availableWidth < 8 || point.Y + height > bounds.Bottom) { Hide(); return; }
            ClientToScreen(sci, ref point);
            int bg = (int)ScintillaApi.Send(sci, 2482, 32);
            BackColor = Color.FromArgb(bg & 255, (bg >> 8) & 255, (bg >> 16) & 255);
            ForeColor = Color.FromArgb(color & 255, (color >> 8) & 255, (color >> 16) & 255);
            var oldFont = Font;
            Font = new Font("Consolas", Math.Max(8, (int)ScintillaApi.Send(sci, 2485, 32)));
            if (oldFont != Control.DefaultFont) oldFont.Dispose();
            Bounds = new Rectangle(point.X + 1, point.Y, Math.Min(availableWidth, TextRenderer.MeasureText(text, Font).Width + 4), Math.Max(height, 15));
            if (!Visible) Show(new WindowOwner(data.Main));
            SetWindowPos(Handle, 0, Left, Top, Width, Height, 0x10 | 0x40); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) { TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, ForeColor, BackColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { m.Result = -1; return; } base.WndProc(ref m); }
        sealed class WindowOwner : IWin32Window { public nint Handle { get; } public WindowOwner(nint handle) { Handle = handle; } }
    }
}

public static class Completion
{
    public static string ForProfile(string raw, string context, int maxWords, string profile)
    {
        profile = PromptBuilder.ResolveProfile(context, profile);
        string text = Clean(raw, context, maxWords);
        if (profile == "soc")
        {
            foreach (System.Text.RegularExpressions.Match term in System.Text.RegularExpressions.Regex.Matches(text, @"\b[A-Z][A-Za-z0-9]+\b"))
            {
                if (term.Value == "VPN" || term.Value == "IP" || term.Value == "IPs") continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(context, @"\b" + System.Text.RegularExpressions.Regex.Escape(term.Value) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "";
            }
        }
        return text;
    }
    public static string WordPrefix(string context) => System.Text.RegularExpressions.Regex.Match(context ?? "", @"[\p{L}\p{M}\p{N}_]+(?:['’\-][\p{L}\p{M}\p{N}_]+)*$").Value;

    public static string Clean(string raw, string context, int maxWords)
    {
        context ??= "";
        string text = (raw ?? "").Replace("\r", "").Trim();
        if (text.Length == 0) return "";
        string word = WordPrefix(context);
        bool echoed = false;
        var candidates = new[] { context, context.Split('\n').LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "" };
        foreach (string candidate in candidates.Distinct())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string normalized = System.Text.RegularExpressions.Regex.Replace(candidate.Trim(), @"\s+", " ");
            string normalizedOutput = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            if (normalized.StartsWith(normalizedOutput, StringComparison.OrdinalIgnoreCase)) return "";
            string pattern = "^" + string.Join(@"\s+", normalized.Split(' ').Select(System.Text.RegularExpressions.Regex.Escape));
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            text = text.Substring(match.Length);
            if (context.Length > 0 && char.IsWhiteSpace(context[^1])) text = text.TrimStart();
            echoed = true; break;
        }
        if (!echoed)
        {
            // A rewritten copy of an earlier line belongs to the correction pass,
            // never to an append-only prediction (including small typo fixes).
            var outputWords = System.Text.RegularExpressions.Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();
            foreach (string line in context.Split('\n'))
            {
                var oldWords = System.Text.RegularExpressions.Regex.Matches(line, @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();
                if (oldWords.Length < 4) continue;
                int matching = oldWords.Zip(outputWords, (a,b) => string.Equals(a,b,StringComparison.OrdinalIgnoreCase) ? 1 : 0).Sum();
                if (matching >= Math.Max(3, oldWords.Length - 1)) return "";
            }
        }
        if (!echoed && word.Length > 0)
        {
            // The model returns the completed word. Remove only the prefix already
            // in the document; never guess whether an unrelated word is a suffix.
            if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return "";
            text = text.Substring(word.Length);
        }
        else if (!echoed && text.Length > 0 && context.Length > 0 && !char.IsWhiteSpace(context[^1]) && char.IsLetterOrDigit(text[0]))
            text = " " + text;
        text = text.Split("\n\n")[0];
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\S+");
        if (matches.Count > maxWords) text = text.Substring(0, matches[maxWords].Index).TrimEnd();
        return text;
    }
    public static string NextWord(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^\s*\S+\s*");
        return match.Success ? match.Value : text;
    }
}
