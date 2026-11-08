using System;
using System.Security.Cryptography;
using System.Text;
using NPPAI.Npp;

namespace NPPAI.Engine;
public class CollectedContext
{
    public string Text, ContextHash, LineText;
    public int CaretPos, CaretLine;
    public bool CaretAtLineEnd;
}
public class ContextCollector
{
    public CollectedContext Collect(nint sci, int chars)
    {
        int caret = (int)ScintillaApi.Send(sci, 2008);
        int line = (int)ScintillaApi.Send(sci, 2166, caret);
        int end = (int)ScintillaApi.Send(sci, 2136, line);
        // Move by characters so a UTF-8 sequence is never split.
        int start = (int)ScintillaApi.Send(sci, 2670, caret, -Math.Clamp(chars, 100, 4000));
        start = Math.Max(0, start);
        string text = ScintillaApi.Read(sci, start, caret);
        return new CollectedContext { Text = text, ContextHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), CaretPos = caret, CaretLine = line, CaretAtLineEnd = caret == end, LineText = text };
    }
}
