using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NPPAI.Npp;

public static class ScintillaApi
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern nint Send(nint window, uint message, nint w = 0, nint l = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Range { public nint Start, End, Text; }

    public static Encoding EncodingFor(nint sci)
    {
        int cp = (int)Send(sci, 2137);
        return cp == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(cp == 0 ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage : cp);
    }

    public static string Read(nint sci, int start, int end)
    {
        if (end <= start) return "";
        nint buffer = Marshal.AllocHGlobal(end - start + 1);
        nint range = Marshal.AllocHGlobal(Marshal.SizeOf<Range>());
        try
        {
            Marshal.StructureToPtr(new Range { Start = start, End = end, Text = buffer }, range, false);
            Send(sci, 2039, 0, range); // SCI_GETTEXTRANGEFULL
            byte[] bytes = new byte[end - start];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return EncodingFor(sci).GetString(bytes);
        }
        finally { Marshal.FreeHGlobal(range); Marshal.FreeHGlobal(buffer); }
    }

    public static void Insert(nint sci, string text)
    {
        byte[] bytes = EncodingFor(sci).GetBytes(text + "\0");
        nint buffer = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, buffer, bytes.Length); Send(sci, 2170, 0, buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static void ReplaceRange(nint sci, int start, int end, string text)
    {
        byte[] bytes = EncodingFor(sci).GetBytes(text);
        nint buffer = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Send(sci, 2686, start, end);
            Send(sci, 2194, bytes.Length, buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
