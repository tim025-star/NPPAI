using System;
using System.Runtime.InteropServices;

namespace NPPAI.Ui
{
    /// <summary>
    /// Marshals work onto the Notepad++ UI thread via a message-only window
    /// created on that thread during NPP_HOOK.
    ///
    /// Usage:
    ///   UiDispatcher.Init();            // UI thread, during NPP_HOOK
    ///   UiDispatcher.Post(() => ...);    // any thread
    ///   UiDispatcher.Shutdown();         // UI thread, during NPP_UNHOOK
    /// </summary>
    public static class UiDispatcher
    {
        private const string ClassName = "NPPAI_MsgWnd_v1";
        private const int WM_APP_ACTION = 0x8001; // WM_APP + 1

        private static IntPtr _hwnd;
        private static bool _initialized;

        // Rooted delegates — must outlive the window.
        private static readonly GCHandle _procHandle;
        private static readonly WndProcDelegate _proc;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static UiDispatcher()
        {
            _proc = WndProc;
            _procHandle = GCHandle.Alloc(_proc);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterClassExW(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_APP_ACTION)
            {
                var handle = (GCHandle)wParam;
                if (handle.IsAllocated)
                {
                    var action = handle.Target as Action;
                    handle.Free();
                    action?.Invoke();
                }
                return IntPtr.Zero;
            }
            if (msg == 0x0010) // WM_DESTROY
            {
                _initialized = false;
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>Create the message-only window. Must run on the UI thread.</summary>
        public static void Init()
        {
            if (_initialized) return;

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = IntPtr.Zero,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = ClassName,
                hIconSm = IntPtr.Zero
            };
            RegisterClassExW(ref wc);

            // HWND_MESSAGE parent => message-only window.
            _hwnd = CreateWindowExW(
                0x80, // WS_EX_TOOLWINDOW
                ClassName, "NPPAI", 0,
                -1, -1, 0, 0,
                new IntPtr(-3), // HWND_MESSAGE
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            _initialized = _hwnd != IntPtr.Zero;
        }

        /// <summary>Post an action to the UI thread. Safe from any thread.</summary>
        public static void Post(Action action)
        {
            if (!_initialized || _hwnd == IntPtr.Zero || action == null) return;
            var handle = GCHandle.Alloc(action);
            PostMessage(_hwnd, WM_APP_ACTION, (IntPtr)handle, IntPtr.Zero);
        }

        /// <summary>Destroy the window. UI thread.</summary>
        public static void Shutdown()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            _initialized = false;
        }
    }
}
