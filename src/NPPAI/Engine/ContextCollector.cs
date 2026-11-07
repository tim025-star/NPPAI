using System;
using System.Security.Cryptography;
using System.Text;
using NPPAI.Npp;

namespace NPPAI.Engine
{
    public class CollectedContext
    {
        public string Text;        // document text up to the caret (truncated)
        public string ContextHash; // SHA1 of the collected text
        public int CaretPos;
        public int CaretLine;
        public bool CaretAtLineEnd;
        public string LineText;    // the line the caret is on (for prefix extraction)
    }

    /// <summary>
    /// Collects the text preceding the caret from the Scintilla document.
    /// Must be called on the UI thread.
    /// </summary>
    public class ContextCollector
    {
        private const int LargeDocThreshold = 2 * 1024 * 1024; // 2 MB

        /// <summary>
        /// Collect context up to the caret. Falls back to line-by-line reads
        /// for very large documents.
        /// </summary>
        public CollectedContext Collect(IntPtr sci, int contextChars)
        {
            var ctx = new CollectedContext();

            // Caret position: use selection start (works for caret or selection).
            int caret = ScintillaApi.SendInt(sci, ScintillaApi.SCI_GETSELECTIONSTART);
            if (caret < 0) caret = ScintillaApi.SendInt(sci, ScintillaApi.SCI_GETCURRENTPOS);
            ctx.CaretPos = caret;

            int lineCount = ScintillaApi.SendInt(sci, ScintillaApi.SCI_GETLINECOUNT);
            ctx.CaretLine = ScintillaApi.SendInt(sci, ScintillaApi.SCI_LINEFROMPOSITION, caret, 0);
            if (ctx.CaretLine < 0 || ctx.CaretLine >= lineCount)
                ctx.CaretLine = Math.Max(0, Math.Min(lineCount - 1, ctx.CaretLine));

            int linePos = ScintillaApi.SendInt(sci, ScintillaApi.SCI_POSITIONFROMLINE, ctx.CaretLine, 0);
            int lineLen = ScintillaApi.SendInt(sci, ScintillaApi.SCI_LINELENGTH, ctx.CaretLine, 0);
            ctx.CaretAtLineEnd = caret >= linePos + lineLen;

            // The current line's text (for the "prefix" used in learning).
            ctx.LineText = ScintillaApi.SendString(sci, ScintillaApi.SCI_GETLINE, ctx.CaretLine, 0) ?? "";

            // Document text up to caret.
            string full;
            int docLen = ScintillaApi.SendInt(sci, ScintillaApi.SCI_GETLENGTH);
            bool largeDoc = docLen > LargeDocThreshold;

            if (largeDoc)
            {
                // Line-by-line fallback: last 50 lines before caret + caret line.
                var sb = new StringBuilder();
                int startLine = Math.Max(0, ctx.CaretLine - 50);
                for (int i = startLine; i <= ctx.CaretLine; i++)
                {
                    string l = ScintillaApi.SendString(sci, ScintillaApi.SCI_GETLINE, i, 0);
                    if (l != null) sb.Append(l);
                    if (i < ctx.CaretLine) sb.Append('\n');
                }
                full = sb.ToString();
                // Trim to contextChars ending at caret.
                if (full.Length > contextChars)
                    full = full.Substring(full.Length - contextChars);
            }
            else
            {
                string all = ScintillaApi.SendString(sci, ScintillaApi.SCI_GETTEXT, 0, -1);
                if (all == null) all = "";
                if (caret > all.Length) caret = all.Length;
                int start = Math.Max(0, caret - contextChars);
                full = all.Substring(start, caret - start);
            }

            ctx.Text = full;
            ctx.ContextHash = Sha1(full);
            return ctx;
        }

        private static string Sha1(string s)
        {
            using var sha = SHA1.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
