using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NPPAI;
using NPPAI.Engine;
using NPPAI.Memory;

static class Program
{
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
    static T Field<T>(string name) => (T)typeof(Plugin).GetField(name, flags)!.GetValue(null)!;
    static void Invoke(string name, params object[] args) => typeof(Plugin).GetMethod(name, flags)!.Invoke(null, args);
    static void PumpUntil(Func<bool> predicate, int timeout = 60000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && watch.ElapsedMilliseconds < timeout) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(predicate(), "asynchronous operation completes within timeout");
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.ThreadException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.Exit(2); };
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Assert(PromptBuilder.ResolveProfile("hello","soc")=="general","SOC mode does not force greetings into a security note");
            Assert(PromptBuilder.ResolveProfile("Entra MFA logs","soc")=="soc","SOC context retains its domain profile");
            Assert(PredictionResponse.Decode("{\"text\":\"hello, how are you?\"}","hello",6,"soc")==", how are you?","complete greeting gets a correctly joined continuation");
            Assert(PredictionResponse.Decode("{\"text\":\"hello\"}","hel",6,"soc")=="lo","partial word uses only its missing letters");
            Assert(PredictionResponse.Decode("{\"text\":\"other text\"}","hel",6,"soc")=="","invalid completed-word prefixes are rejected");
            var mixed = Spelling.ValidateEdits("SOusers is abse din UK with casuein and imposiable travel. ", new[] { new SpellingProposal("SOusers","user"), new SpellingProposal("abse din","based in"), new SpellingProposal("casuein","causing"), new SpellingProposal("imposiable","impossible") }, -1);
            Assert(mixed.Count == 3, "valid split-word and spelling edits survive an ambiguous sibling");
            Assert(Spelling.ValidateEdits("apperas to use loks ",new[]{new SpellingProposal("apperas","appears"),new SpellingProposal("loks","looks")},-1,new[]{"loks"}).Count==1,"custom dictionary protects names without suppressing other repairs");
            Assert(Spelling.ValidateEdits("abse din with imposiable travel. ",new[]{new SpellingProposal("abse din","based in"),new SpellingProposal("din","in"),new SpellingProposal("imposiable","impossible")},-1).Count==1,"overlapping proposals are rejected independently of unrelated valid edits");
            Assert(Spelling.ValidateEdits("abse din UK ", new[]{new SpellingProposal("abse","based"),new SpellingProposal("din","in")},-1).Count==2,"adjacent transposition typos are not over-penalised");
            var screenshotEdits=Spelling.ValidateEdits("on the piviot i can se that the ip was used to descovry a tish is indictivt of a benine alert.",new[]{new SpellingProposal("piviot","pivot"),new SpellingProposal("se","see"),new SpellingProposal("descovry","discovery"),new SpellingProposal("tish","this"),new SpellingProposal("indictivt","indicative"),new SpellingProposal("benine","benign")},-1);
            Assert(screenshotEdits.Count==6,"clear spelling errors in the new screenshot pass independent validation");
            Assert(Spelling.ValidateEdits("it is safe ",new[]{new SpellingProposal("is","in")},-1).Count==0,"short-word support cannot rewrite an already valid function word");
            var queue = new CorrectionQueue();
            var tracked = queue.Add(42,1,10,"apperas. ","");
            queue.Observe(42,30,0,4,Encoding.UTF8.GetByteCount);
            Assert(tracked.Valid && tracked.Start==10,"typing in a later sentence preserves an earlier correction job");
            queue.Observe(42,2,0,3,Encoding.UTF8.GetByteCount);
            Assert(tracked.Valid && tracked.Start==13,"earlier disjoint edits move the correction anchor");
            queue.Observe(42,14,1,0,Encoding.UTF8.GetByteCount);
            Assert(!tracked.Valid,"editing the queued source invalidates its snapshot");
            Assert(Completion.ForProfile("Windows device.","Entra logs: the user likely uses a ",6,"soc")=="","SOC predictions suppress new named products");
            Assert(Completion.ForProfile("proxy or VPN.","The user likely uses a ",6,"soc")=="proxy or VPN.","SOC predictions permit relevant network terminology");
            Assert(Spelling.ValidateEdits("Loks like BAU Entra is good, MFA passed IPs ", new[]{new SpellingProposal("Loks","Looks")}, 45).Count == 1, "spelling correction fixes typo while preserving SOC terminology");
            Assert(Spelling.ValidateEdits("Check Entra MFA ", new[]{new SpellingProposal("Entra","Entry")}, 16).Count == 0, "correction cannot rename product or acronym");
            Assert(Spelling.ValidateEdits("open loks.com ", new[]{new SpellingProposal("loks","looks")}, 14).Count == 0, "correction cannot alter a hostname");
            Assert(Spelling.ValidateEdits("check servr123 ", new[]{new SpellingProposal("servr","server")}, 15).Count == 0, "correction cannot alter an alphanumeric identifier");
            Assert(Spelling.ValidateEdits("I will inves", new[]{new SpellingProposal("inves","invest")}, 12).Count == 0, "correction leaves the word at the caret alone");
            Assert(Spelling.ValidateEdits("Loks good ", new[]{new SpellingProposal("Loks","Looks good and safe")}, 10).Count == 0, "correction cannot add claims or words");
            Assert(Completion.Clean("FP, Loks like BAU Entra is good, MFA passed IPs, but still need review.", "FP,\r\nLoks like BAU Entra is good, MFA passed IPs ", 20) == ", but still need review.", "multiline echo from screenshot is not appended twice");
            Assert(Completion.Clean("Loks like BAU Entra is good, MFA passed IPs", "FP,\r\nLoks like BAU Entra is good, MFA passed IPs ", 20) == "", "streamed current-line echoes remain hidden");
            Assert(Completion.Clean("Looks like BAU Entra is good, MFA passed IPs", "Loks like BAU Entra is good, MFA passed IPs ", 20) == "", "rewritten existing line cannot become an append-only suggestion");
            Assert(Completion.Clean("investigate the issue", "I will inves", 20) == "tigate the issue", "unfinished word is completed without inserting a space");
            Assert(Completion.Clean("for the update.", "Thanks for", 20) == " the update.", "completed word advances naturally to next word");
            Assert(Completion.Clean("the update.", "Thanks for", 20) == "", "unrelated output cannot be inserted into the current word");
            Assert(Completion.Clean("investigate", "I will investigate", 20) == "", "fully typed word is not repeated");
            Assert(Completion.Clean("inv", "I will inves", 20) == "", "incomplete streamed prefix waits for more tokens");
            Assert(Completion.Clean("I will investigate the issue", "I will inves", 20) == "tigate the issue", "full-context echoes preserve unfinished word boundary");
            Assert(Completion.Clean("répondre demain", "Je vais rép", 20) == "ondre demain", "Unicode word prefixes are completed correctly");
            Assert(Completion.Clean("the update.", "Thanks for ", 20) == "the update.", "space-ended context still predicts new words");
            Assert(Completion.Clean("received", "I have ", 20) == "received", "completion does not duplicate whitespace");
            Assert(Completion.NextWord(" the update is ready") == " the ", "partial acceptance takes one word");
            Assert(Completion.Clean("Hi Sarah", "Hi Sarah, Thanks for", 20) == "", "streamed prompt echoes are suppressed");
            string path = Path.Combine(Path.GetTempPath(), "nppai-test-" + Guid.NewGuid() + ".db");
            using (var memory = new MemoryDb(path))
            {
                for (int i = 0; i < 20; i++) memory.RecordAccept("thanks for", "Thanks for", " the update.", "hash");
                Assert(memory.GetStats().promoted == 1, "existing phrase is promoted at 20 accepts");
                memory.RecordAccept("thanks for", "Thanks for", " a different suggestion", "hash");
                Assert(memory.GetStats().promoted == 0, "replacement phrase cannot inherit another phrase's trust");
                long id = memory.RecordAccept("other trigger", "Other trigger", " accepted", "hash");
                memory.RecordEdit(id);
                Assert(memory.GetStats().edits == 1, "edit refers to acceptance ID rather than phrase ID");
            }
            using (var memory = new MemoryDb(path)) Assert(memory.GetStats().accepts == 22, "memory survives reopen");
            using var editor = new FakeEditor();
            using var main = new MainWindow();
            main.Width = 800; main.Show(); editor.Parent = main; editor.Width = 700; editor.Height = 100; editor.Show();
            Plugin.Init(new NppData { Main = main.Handle, Primary = editor.Handle, Secondary = editor.Handle }, Path.Combine(Path.GetTempPath(), "nppai-smoke-" + Guid.NewGuid()));
            var config = Field<NPPAI.Config.PluginConfig>("config"); config.enabled = true; config.model = "qwen2.5:3b"; config.temperature = 0; config.writing_profile = "general";
            TestCorrectionLifecycle(editor,config);
            if(args.Contains("--lifecycle")) {Invoke("Shutdown"); return 0;}
            config.writing_profile="soc"; editor.TextValue="hello";
            int greetingRequests=Field<int>("requests");
            Invoke("Schedule");
            PumpUntil(()=>Field<int>("requests")>greetingRequests || Field<string>("error").Length>0);
            string greeting=Field<string>("suggestion");
            Console.WriteLine("hello prediction: ["+greeting+"]");
            Assert(greeting.Length>0,"typing hello produces a live suggestion with SOC mode selected");
            Assert(Field<Form>("ghost").Visible,"hello suggestion window is actually shown");
            Assert(editor.TextValue=="hello","showing the hello suggestion does not change the document");
            Invoke("Key",Keys.Tab);
            Assert(editor.TextValue=="hello"+greeting,"hello suggestion can be accepted without repeating the word");
            config.writing_profile="general";
            editor.TextValue = "Hi Sarah, Thanks for";
            var context = new ContextCollector().Collect(editor.Handle, 1000);
            Assert(context.Text == editor.TextValue && context.CaretAtLineEnd, "context reader uses actual native buffer protocol");
            editor.TextValue = "Café 日本語 😀";
            Assert(new ContextCollector().Collect(editor.Handle, 1000).Text == editor.TextValue, "UTF-8 context round trips safely");
            editor.TextValue = "Hi Sarah, Thanks for";
            Invoke("RequestNow");
            Console.WriteLine("Request scheduled: " + (Field<System.Threading.CancellationTokenSource>("pending") != null));
            int beforeRequest=greetingRequests+1;
            PumpUntil(() => Field<int>("requests") > beforeRequest || Field<string>("error").Length > 0);
            Assert(Field<string>("error").Length == 0, "real Ollama request succeeds: " + Field<string>("error"));
            string prediction = Field<string>("suggestion");
            Console.WriteLine("Prediction: [" + prediction + "]");
            Assert(prediction.Length > 0, "qwen2.5:3b returns a continuation");
            Assert(editor.TextValue == "Hi Sarah, Thanks for", "ghost leaves document unchanged");
            Invoke("Key", Keys.Control | Keys.Right);
            Assert(editor.TextValue == "Hi Sarah, Thanks for" + Completion.NextWord(prediction), "Ctrl+Right inserts exactly one word");
            if (Field<string>("suggestion").Length > 0) Invoke("Key", Keys.Tab);
            Assert(editor.TextValue == "Hi Sarah, Thanks for" + prediction, "Tab inserts remaining prediction");
            typeof(Plugin).GetField("suggestion", flags)!.SetValue(null, " a longer fixture");
            var fixtureContext = new ContextCollector().Collect(editor.Handle, 1000);
            typeof(Plugin).GetField("ghostCaret", flags)!.SetValue(null, fixtureContext.CaretPos);
            typeof(Plugin).GetField("ghostHash", flags)!.SetValue(null, fixtureContext.ContextHash);
            string before = editor.TextValue;
            Invoke("Key", Keys.Tab);
            Assert(editor.TextValue == before + " a longer fixture", "Tab accepts a complete multiword suggestion");
            typeof(Plugin).GetField("suggestion", flags)!.SetValue(null, " rejected fixture");
            var newContext = new ContextCollector().Collect(editor.Handle, 1000);
            typeof(Plugin).GetField("ghostCaret", flags)!.SetValue(null, newContext.CaretPos);
            typeof(Plugin).GetField("ghostHash", flags)!.SetValue(null, newContext.ContextHash);
            before = editor.TextValue;
            Invoke("Key", Keys.Escape);
            Assert(editor.TextValue == before && Field<string>("suggestion").Length == 0, "Escape dismisses without inserting");
            editor.TextValue = "I will inves";
            int beforePartialRequest = Field<int>("requests");
            nint notification = Marshal.AllocHGlobal(64);
            try
            {
                Marshal.Copy(new byte[64], 0, notification, 64);
                Marshal.WriteIntPtr(notification, 24, Encoding.UTF8.GetByteCount(editor.TextValue) - 1);
                Marshal.WriteInt32(notification, 40, 1); // SC_MOD_INSERTTEXT, no space typed
                Plugin.Notify(new Header { Window = editor.Handle, Code = 2008 }, notification);
            }
            finally { Marshal.FreeHGlobal(notification); }
            PumpUntil(() => Field<int>("requests") > beforePartialRequest || Field<string>("error").Length > 0);
            string wordSuffix = Field<string>("suggestion");
            Console.WriteLine("Inside-word prediction: [" + wordSuffix + "]");
            Assert(wordSuffix.Length > 0 && !char.IsWhiteSpace(wordSuffix[0]), "typing without a space triggers a live word completion");
            Invoke("Key", Keys.Tab);
            Assert(editor.TextValue == "I will inves" + wordSuffix, "Tab joins the live completion directly to the unfinished word");
            Invoke("RequestNow"); Invoke("Clear");
            int previous = Field<int>("requests");
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 1200) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(Field<string>("suggestion").Length == 0 && Field<int>("requests") == previous, "cancelled request cannot display stale text");
            Console.WriteLine("Latency: " + Field<long>("latency") + " ms");
            config.enable_ghost_text = false;
            config.correction_delay_ms = 500;
            editor.TextValue = "Loks like BAU Entra is good, MFA passed IPs ";
            var corrector = Field<CorrectionService>("corrections");
            corrector.ForgetDocuments();
            int beforeCorrection=corrector.Applied;
            Invoke("Schedule");
            PumpUntil(() => corrector.Applied > beforeCorrection || corrector.LastError.Length > 0, 100000);
            Assert(corrector.LastError.Length == 0, "larger correction model request succeeds: " + corrector.LastError);
            Assert(editor.TextValue == "Looks like BAU Entra is good, MFA passed IPs ", "background correction replaces existing typo and preserves SOC terms");
            corrector.ObserveEdit(0x20|1); corrector.ObserveEdit(0x10|1);
            editor.TextValue="hello James, this is an Fp because hte entra logs loojks good and there are no issues with MFA or IP locations.";
            editor.CaretByte=0;
            corrector.Schedule();
            PumpUntil(()=>editor.TextValue.Contains("because the entra") && System.Text.RegularExpressions.Regex.IsMatch(editor.TextValue,@"logs looks? good"),90000);
            Assert(editor.TextValue.Contains("James") && editor.TextValue.Contains("MFA"),"screenshot spelling errors are corrected with the caret at the start of the line");
            editor.CaretByte=null;
            corrector.ForgetDocuments();
            editor.TextValue="James user. Connected to microsoft via palo IP. command shell showed python error\n\non the piviot i can se that the ip was used to descovry a tish is indictivt of a benine alert.\n";
            corrector.Schedule();
            try {PumpUntil(()=>editor.TextValue.Contains("pivot") && editor.TextValue.Contains("see") && editor.TextValue.Contains("discover") && editor.TextValue.Contains("this") && editor.TextValue.Contains("indicative") && editor.TextValue.Contains("benign"),90000);}
            catch {Console.WriteLine("Latest screenshot result: "+editor.TextValue);Console.WriteLine("Correction state: "+corrector.Phase+" / "+corrector.LastOutcome+" / "+corrector.LastError);throw;}
            Assert(editor.TextValue.Contains("James") && editor.TextValue.Contains("palo IP"),"latest screenshot is repaired by the live model without renaming existing entities");
            corrector.ForgetDocuments();
            editor.TextValue="FP. Entra logs look okay, IPs are clean.\nusers john apperas to be using software to log in rom locations of AU, NZ and UK.\nAU is palo, NZ is RBNZ ip and UK is ISP.\nSOusers is abse din UK and likely uses a proxy or other software casuein the imposiable travel.\n";
            corrector.Schedule();
            try { PumpUntil(()=>editor.TextValue.Contains("appears") && editor.TextValue.Contains("from locations") && editor.TextValue.Contains("based in UK") && editor.TextValue.Contains("causing") && editor.TextValue.Contains("impossible"),60000); }
            catch { Console.WriteLine("Corrected sample: "+editor.TextValue); Console.WriteLine("Correction error: "+corrector.LastError); throw; }
            Assert(editor.TextValue.Contains("SOusers") && editor.TextValue.Contains("Entra") && editor.TextValue.Contains("RBNZ"),"full user sample repairs clear errors while preserving ambiguous identifiers");
            editor.TextValue = "Loks like BAU ";
            Invoke("Schedule");
            Invoke("Clear");
            editor.TextValue = "newer text ";
            var correctionWait = System.Diagnostics.Stopwatch.StartNew();
            while (correctionWait.ElapsedMilliseconds < 800) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(editor.TextValue == "newer text ", "cancelled correction cannot overwrite newer text");
            editor.TextValue = "Loks good ";
            corrector.ObserveEdit(0x20 | 1);
            Invoke("Schedule");
            var undoWait = System.Diagnostics.Stopwatch.StartNew();
            while (undoWait.ElapsedMilliseconds < 800) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(editor.TextValue == "Loks good ", "undo pauses autocorrection until the next direct user edit");
            corrector.ObserveEdit(0x10 | 1);
            using(var server = new DelayedOllama("apperas","appears"))
            {
                config.endpoint=server.Endpoint;
                editor.TextValue="The user apperas legitimate.\n";
                corrector.Schedule();
                PumpUntil(()=>server.Received.Task.IsCompleted);
                int at=Encoding.UTF8.GetByteCount(editor.TextValue);
                editor.TextValue+="Newer sentence being typed ";
                corrector.ObserveEdit(0x10 | 1,editor.Handle,at,Encoding.UTF8.GetByteCount("Newer sentence being typed "));
                corrector.Schedule();
                int oldApplied=corrector.Applied;
                server.Release();
                PumpUntil(()=>corrector.Applied>oldApplied);
                Assert(editor.TextValue=="The user appears legitimate.\nNewer sentence being typed ","in-flight earlier-sentence correction survives typing on the next line without overwriting it");
                corrector.Cancel();
            }
            corrector.ObserveEdit(0x20 | 1); corrector.ObserveEdit(0x10 | 1);
            using(var server = new DelayedOllama("apperas","appears"))
            {
                config.endpoint=server.Endpoint;
                editor.TextValue="The user apperas legitimate.\n";
                corrector.Schedule(); PumpUntil(()=>server.Received.Task.IsCompleted);
                int at=Encoding.UTF8.GetByteCount("The user ");
                editor.TextValue="The user is legitimate.\n";
                corrector.ObserveEdit(0x10 | 2,editor.Handle,at,7);
                server.Release();
                var staleWait=System.Diagnostics.Stopwatch.StartNew();
                while(staleWait.ElapsedMilliseconds<800){Application.DoEvents();Thread.Sleep(10);}
                Assert(editor.TextValue=="The user is legitimate.\n","in-flight correction is discarded when its original words change");
            }
            Invoke("Shutdown");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void TestCorrectionLifecycle(FakeEditor editor, NPPAI.Config.PluginConfig config)
    {
        var corrector=Field<CorrectionService>("corrections");
        string endpoint=config.endpoint; int delay=config.correction_delay_ms, timeout=config.correction_timeout_ms;
        config.enable_ghost_text=false; config.correction_delay_ms=500;
        using(var server=new DelayedOllama("apperas","appears"))
        {
            config.endpoint=server.Endpoint; editor.TextValue="The account apperas normal.\n";
            corrector.Schedule();
            NPPAI.Npp.ScintillaApi.Send(editor.Handle,8); // WM_KILLFOCUS, e.g. opening Status
            PumpUntil(()=>server.Received.Task.IsCompleted,3000);
            NPPAI.Npp.ScintillaApi.Send(editor.Parent.Handle,6,0); // WM_ACTIVATE / inactive
            server.Release();
            PumpUntil(()=>editor.TextValue.Contains("appears"),3000);
            Assert(editor.TextValue=="The account appears normal.\n","correction continues across editor and window focus changes");
        }
        corrector.ForgetDocuments(); config.correction_timeout_ms=1000;
        using(var server=new DelayedOllama("apperas","appears"))
        {
            config.endpoint=server.Endpoint; editor.TextValue="This account apperas normal.\n";
            corrector.Schedule(); PumpUntil(()=>server.Received.Task.IsCompleted,3000);
            PumpUntil(()=>corrector.LastError.Contains("timed out",StringComparison.OrdinalIgnoreCase),4000);
            Assert(!editor.TextValue.Contains("appears"),"timed-out correction cannot change the document");
        }
        config.correction_timeout_ms=timeout;
        using(var server=new DelayedOllama("apperas","appears"))
        {
            config.endpoint=server.Endpoint; editor.TextValue="Another account apperas normal.\n";
            corrector.Schedule(); PumpUntil(()=>server.Received.Task.IsCompleted,3000); server.Release();
            PumpUntil(()=>editor.TextValue.Contains("appears"),3000);
            Assert(editor.TextValue=="Another account appears normal.\n","correction worker recovers after a provider timeout");
        }
        corrector.ForgetDocuments(); config.endpoint=endpoint; config.correction_delay_ms=delay; config.enable_ghost_text=true;
    }
    sealed class MainWindow : Form
    {
        protected override void WndProc(ref Message m) { if (m.Msg == 2028) { Marshal.WriteInt32(m.LParam, 0); m.Result = 1; return; } base.WndProc(ref m); }
    }
    sealed class FakeEditor : Control
    {
        public string TextValue = "";
        public int? CaretByte;
        int targetStart, targetEnd;
        protected override void WndProc(ref Message m)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(TextValue);
            int caret=CaretByte ?? bytes.Length;
            switch (m.Msg)
            {
                case 2006: case 2136: m.Result = bytes.Length; return;
                case 2008: case 2143: case 2145: case 2009: m.Result = caret; return;
                case 2166: case 2140: m.Result = 0; return;
                case 2570: m.Result = 1; return;
                case 2357: m.Result = 42; return;
                case 2137: m.Result = 65001; return;
                case 2670: m.Result = Math.Max(0, (int)m.WParam + (int)m.LParam); return;
                case 2039:
                    int start = (int)Marshal.ReadIntPtr(m.LParam), end = (int)Marshal.ReadIntPtr(m.LParam, 8);
                    Marshal.Copy(bytes, start, Marshal.ReadIntPtr(m.LParam, 16), end - start); m.Result = end - start; return;
                case 2170: TextValue += Marshal.PtrToStringUTF8(m.LParam); m.Result = 0; return;
                case 2686: targetStart = (int)m.WParam; targetEnd = (int)m.LParam; m.Result = 0; return;
                case 2194:
                    byte[] replacement = new byte[(int)m.WParam];
                    Marshal.Copy(m.LParam, replacement, 0, replacement.Length);
                    TextValue = Encoding.UTF8.GetString(bytes.Take(targetStart).Concat(replacement).Concat(bytes.Skip(targetEnd)).ToArray());
                    m.Result = replacement.Length; return;
                case 2164: m.Result = 100; return;
                case 2165: m.Result = 10; return;
                case 2279: m.Result = 20; return;
                case 2485: m.Result = 11; return;
                case 2482: m.Result = 0xffffff; return;
            }
            base.WndProc(ref m);
        }
    }
}
