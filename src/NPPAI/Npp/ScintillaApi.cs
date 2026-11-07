using System;
using System.Runtime.InteropServices;

namespace NPPAI.Npp
{
    /// <summary>
    /// Scintilla message constants and a thin SendMessage wrapper.
    /// All calls must be made on the Notepad++ UI thread.
    /// </summary>
    public static class ScintillaApi
    {
        // --- Scintilla message constants (verified against NPP's ScintillaMessages.h) ---
        public const uint SCI_INSERTTEXT              = 2003;
        public const uint SCI_GETLENGTH               = 2006;
        public const uint SCI_GETCHARAT               = 2007;
        public const uint SCI_GETCURRENTPOS           = 2008;
        public const uint SCI_GETANCHOR               = 2009;
        public const uint SCI_SETUNDOCOLLECTION       = 2012;
        public const uint SCI_BEGINUNDOACTION         = 2078;
        public const uint SCI_ENDUNDOACTION           = 2079;
        public const uint SCI_STYLESETFORE            = 2051;
        public const uint SCI_STYLESETBACK            = 2052;
        public const uint SCI_STYLESETSIZE            = 2055;
        public const uint SCI_STYLESETFONT            = 2056;
        public const uint SCI_STYLEGETFORE            = 2481;
        public const uint SCI_STYLEGETBACK            = 2482;
        public const uint SCI_STYLEGETSIZE            = 2485;
        public const uint SCI_STYLEGETFONT            = 2486;
        public const uint SCI_GETCOLUMN               = 2129;
        public const uint SCI_GETLINEENDPOSITION      = 2136;
        public const uint SCI_SETCURRENTPOS           = 2141;
        public const uint SCI_GETSELECTIONSTART       = 2143;
        public const uint SCI_GETSELECTIONEND         = 2145;
        public const uint SCI_GETLINE                 = 2153;
        public const uint SCI_GETLINECOUNT            = 2154;
        public const uint SCI_SETSEL                  = 2160;
        public const uint SCI_LINEFROMPOSITION        = 2166;
        public const uint SCI_POSITIONFROMLINE        = 2167;
        public const uint SCI_REPLACERSEL             = 2170;
        public const uint SCI_SETTEXT                 = 2181;
        public const uint SCI_GETTEXT                 = 2182;
        public const uint SCI_SETTARGETSTART          = 2190;
        public const uint SCI_GETTARGETSTART          = 2191;
        public const uint SCI_SETTARGETEND            = 2192;
        public const uint SCI_GETTARGETEND            = 2193;
        public const uint SCI_SETTARGETRANGE          = 2686;
        public const uint SCI_WORDSTARTPOSITION       = 2266;
        public const uint SCI_WORDENDPOSITION         = 2267;
        public const uint SCI_APPENDTEXT              = 2282;
        public const uint SCI_LINELENGTH              = 2350;
        public const uint SCI_SETMARGINTYPEN          = 2240;
        public const uint SCI_SETMARGINWIDTHN         = 2242;
        public const uint SCI_GETMARGINWIDTHN         = 2243;
        public const uint SCI_SETMARGINS              = 2252;
        public const uint SCI_GETMARGINS              = 2253;
        public const uint SCI_SETMARGINLEFT           = 2155;
        public const uint SCI_GETMARGINLEFT           = 2156;
        public const uint SCI_SETMARGINRIGHT          = 2157;
        public const uint SCI_GETMARGINRIGHT          = 2158;
        public const uint SCI_SETFOLDMARGINCLOUR      = 2290;
        public const uint SCI_SETFOLDMARGINHICOLOUR   = 2291;
        public const uint SCI_ANNOTATIONSETTEXT       = 2540;
        public const uint SCI_ANNOTATIONGETTEXT       = 2541;
        public const uint SCI_ANNOTATIONSETSTYLE      = 2542;
        public const uint SCI_ANNOTATIONGETSTYLE      = 2543;
        public const uint SCI_ANNOTATIONSETSTYLES     = 2544;
        public const uint SCI_ANNOTATIONGETSTYLES     = 2545;
        public const uint SCI_ANNOTATIONGETLINES      = 2546;
        public const uint SCI_ANNOTATIONCLEARALL      = 2547;
        public const uint SCI_ANNOTATIONSETVISIBLE    = 2548;
        public const uint SCI_ANNOTATIONGETVISIBLE    = 2549;
        public const uint SCI_ANNOTATIONSETSTYLEOFFSET = 2550;
        public const uint SCI_ANNOTATIONGETSTYLEOFFSET = 2551;
        public const uint SCI_SETEMPTYSELECTION       = 2556;
        public const uint SCI_SETSELECTION            = 2572;
        public const uint SCI_MARGINSETTEXT           = 2530;
        public const uint SCI_MARGINGETTEXT           = 2531;

        public const int STYLE_DEFAULT = 33;   // NPP's default style
        public const int GHOST_STYLE   = 67;   // our private annotation style

        // SCN notification codes (verified against Scintilla.iface)
        public const int SCN_STYLENEEDED = 2000;
        public const int SCN_CHARADDED   = 2001;
        public const int SCN_KEY         = 2005;
        public const int SCN_DOUBLECLICK = 2006;
        public const int SCN_UPDATEUI    = 2007;
        public const int SCN_MODIFIED    = 2008;
        public const int SCN_MARGINCLICK = 2010;
        public const int SCN_USERLISTSELECTION = 2014;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, bool lParam);

        /// <summary>Send a raw message with pointer-sized params.</summary>
        public static IntPtr Send(IntPtr sci, uint msg, IntPtr w = default, IntPtr l = default)
            => SendMessage(sci, msg, w, l);

        /// <summary>Send a message returning an int.</summary>
        public static int SendInt(IntPtr sci, uint msg, int w = 0, int l = 0)
            => (int)SendMessage(sci, msg, w, l);

        /// <summary>
        /// Send a message returning a malloc'd string (SCI_GETTEXT, SCI_GETLINE).
        /// Caller receives a managed copy; the native buffer is freed here.
        /// </summary>
        public static string SendString(IntPtr sci, uint msg, int w = 0, int l = 0)
        {
            IntPtr p = SendMessage(sci, msg, w, l);
            if (p == IntPtr.Zero) return null;
            string s = Marshal.PtrToStringAnsi(p);
            Marshal.FreeHGlobal(p);
            return s;
        }

        /// <summary>Send a message with a string lParam (inserts / sets text).</summary>
        public static void SendStringParam(IntPtr sci, uint msg, int w, string s)
            => SendMessage(sci, msg, w, s);

        /// <summary>Send a message with a bool lParam.</summary>
        public static void SendBool(IntPtr sci, uint msg, int w, bool v)
            => SendMessage(sci, msg, w, v);
    }
}
