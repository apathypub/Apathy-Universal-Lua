using System.Runtime.InteropServices;

namespace Apathy.Services;

internal static class Native
{
    // ---- SendInput structures ----

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KF_KEYUP = 0x0002;          // same value as KEYEVENTF_KEYUP
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;
    private const int WHEEL_DELTA = 120;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const uint KEYEVENTF_UNICODE  = 0x0004;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    /// <summary>True while the key / mouse button is physically held, regardless of which window has focus.</summary>
    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Keeps Windows 11 from rounding the corners of the borderless window.</summary>
    public static void DisableRoundedCorners(IntPtr hwnd)
    {
        int preference = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>Makes a window click-through, never focused, and hidden from Alt+Tab.</summary>
    public static void MakeOverlay(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Hides a window from Alt+Tab and keeps it from taking focus, but leaves it clickable.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Taps a virtual-key (press + release), used for media keys like play/pause/next/prev.</summary>
    public static void TapKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static bool WindowExists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>Process id owning the current foreground (focused) window, or 0.</summary>
    public static int ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out uint processId);
        return (int)processId;
    }

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>On/off state of a toggle key such as Caps Lock, Num Lock or Scroll Lock.</summary>
    public static bool IsKeyToggled(int virtualKey) => (GetKeyState(virtualKey) & 0x1) != 0;

    /// <summary>Mouse cursor position in physical screen pixels.</summary>
    public static (int X, int Y) CursorPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>Size of the primary screen in physical pixels.</summary>
    public static (int Width, int Height) ScreenSize() => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>Time since the last keyboard or mouse input anywhere in Windows.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)); // wraps like TickCount
    }

    /// <summary>Encrypts with DPAPI for the current Windows user (only that account can decrypt). Null on failure.</summary>
    public static byte[]? Protect(byte[] data) => Dpapi(data, protect: true);

    /// <summary>Reverses <see cref="Protect"/>. Null when the data is corrupt or belongs to another user.</summary>
    public static byte[]? Unprotect(byte[] data) => Dpapi(data, protect: false);

    private static byte[]? Dpapi(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = pinned.AddrOfPinnedObject() };
            DATA_BLOB output;
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) return null;

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>The window's client area (without title bar/borders) in physical screen pixels.</summary>
    public static System.Windows.Int32Rect? ClientBoundsOnScreen(IntPtr hwnd)
    {
        var origin = new POINT();
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref origin)) return null;
        return new System.Windows.Int32Rect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ---- Low-level keyboard hook ----

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint LLKHF_INJECTED = 0x10;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    /// <summary>
    /// Installs a low-level keyboard hook on the calling thread and pumps messages until <paramref name="stop"/>
    /// is cancelled. <paramref name="onKey"/> (vk, down) sees only physical keystrokes — SendInput-injected ones
    /// are skipped — and must return fast, since Windows holds every keystroke until it does.
    /// False when the hook couldn't be installed.
    /// </summary>
    public static bool RunPhysicalKeyHook(Action<int, bool> onKey, CancellationToken stop)
    {
        LowLevelKeyboardProc callback = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & LLKHF_INJECTED) == 0)
                    onKey((int)info.vkCode, wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };

        // The thread needs a message queue before WM_QUIT can be posted to it.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        uint threadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookExW(WH_KEYBOARD_LL, callback, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero) return false;
        try
        {
            using var quit = stop.Register(() => PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero));
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
            return true;
        }
        finally
        {
            UnhookWindowsHookEx(hook);
            GC.KeepAlive(callback);
        }
    }

    // ---- Screen capture ----

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>
    /// Copies a rectangle of the screen (physical pixels), enlarged <paramref name="scale"/> times with smoothing,
    /// as top-down BGRA bytes. Null if the capture failed.
    /// </summary>
    public static byte[]? CaptureScreen(int x, int y, int width, int height, int scale)
    {
        int outWidth = width * scale, outHeight = height * scale;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = outWidth,
            biHeight = -outHeight, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr bitmap = CreateDIBSection(memory, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) return null;
            IntPtr previous = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, HALFTONE);
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            bool copied = StretchBlt(memory, 0, 0, outWidth, outHeight, screen, x, y, width, height, SRCCOPY | CAPTUREBLT);
            GdiFlush();
            SelectObject(memory, previous);
            if (!copied) return null;

            var pixels = new byte[outWidth * outHeight * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---- Input posted to one window (the real cursor / keyboard state don't change) ----

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const uint WM_KEYUP = 0x0101;
    private const int MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>Clicks at a screen point inside a window by posting mouse messages to it. False if the post failed.</summary>
    public static bool PostClick(IntPtr hwnd, int screenX, int screenY, bool right)
    {
        var point = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(hwnd, ref point)) return false;
        IntPtr position = (point.Y << 16) | (point.X & 0xFFFF);
        return PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, position)
            && PostMessageW(hwnd, right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN, right ? MK_RBUTTON : MK_LBUTTON, position)
            && PostMessageW(hwnd, right ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, position);
    }

    /// <summary>Taps a key in one window by posting key messages to it.</summary>
    public static bool PostKey(IntPtr hwnd, int virtualKey)
    {
        int scan = (int)MapVirtualKeyW((uint)virtualKey, MAPVK_VK_TO_VSC);
        IntPtr down = 1 | (scan << 16);
        IntPtr up = unchecked(1 | (scan << 16) | (1 << 30) | (int)0x80000000);
        return PostMessageW(hwnd, (uint)WM_KEYDOWN, virtualKey, down) && PostMessageW(hwnd, WM_KEYUP, virtualKey, up);
    }

    // ---- High-resolution sleep ----

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x001F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion,
        IntPtr completionArg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>
    /// A timer for <see cref="PreciseSleep"/>. Thread.Sleep(1) really sleeps ~15.6 ms at Windows' default timer
    /// resolution; this one wakes within a fraction of a millisecond. Zero when unavailable (pre-1803 Windows).
    /// </summary>
    public static IntPtr CreatePreciseTimer() =>
        CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

    public static void PreciseSleep(IntPtr timer, double milliseconds)
    {
        long due = -(long)(milliseconds * 10_000); // negative = relative, in 100 ns units
        if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(timer, INFINITE);
        else
            Thread.Sleep((int)Math.Ceiling(milliseconds));
    }

    public static void CloseTimer(IntPtr timer)
    {
        if (timer != IntPtr.Zero) CloseHandle(timer);
    }

    private static readonly INPUT[] JumpTap =
    [
        new() { type = INPUT_KEYBOARD, union = new InputUnion { ki = new KEYBDINPUT { wVk = 0x20 } } },
        new() { type = INPUT_KEYBOARD, union = new InputUnion { ki = new KEYBDINPUT { wVk = 0x20, dwFlags = KF_KEYUP } } },
    ];

    /// <summary>Taps SPACE (down + up) in one SendInput call.</summary>
    public static void SendJumpInput() => SendInput((uint)JumpTap.Length, JumpTap, INPUT.Size);

    /// <summary>Types a string by injecting KEYEVENTF_UNICODE events — no clipboard needed.</summary>
    public static void TypeString(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            var ki = new KEYBDINPUT { wVk = 0, wScan = text[i], dwFlags = KEYEVENTF_UNICODE };
            inputs[i * 2]     = new INPUT { type = INPUT_KEYBOARD, union = new InputUnion { ki = ki } };
            inputs[i * 2 + 1] = new INPUT { type = INPUT_KEYBOARD, union = new InputUnion { ki = ki with { dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
        }
        SendInput((uint)inputs.Length, inputs, INPUT.Size);
    }

    /// <summary>Presses or releases a key via SendInput.</summary>
    public static void SendKey(int virtualKey, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)virtualKey, dwFlags = down ? 0u : KF_KEYUP } }
        };
        SendInput(1, [input], INPUT.Size);
    }

    /// <summary>Moves the mouse cursor to absolute screen coordinates.</summary>
    public static void MoveMouse(int x, int y) => SetCursorPos(x, y);

    /// <summary>Presses or releases a mouse button. button: 1=left, 2=right, 3=middle, 4=X1, 5=X2.</summary>
    public static void SendMouseButton(int button, bool down)
    {
        uint flags = button switch
        {
            1 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            3 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            4 => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            5 => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            _ => 0u
        };
        if (flags == 0) return;
        uint mouseData = button == 4 ? XBUTTON1 : button == 5 ? XBUTTON2 : 0u;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } }
        };
        SendInput(1, [input], INPUT.Size);
    }

    /// <summary>Scrolls the mouse wheel. delta > 0 = up, delta &lt; 0 = down. 1 unit = one notch.</summary>
    public static void ScrollMouse(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(delta * WHEEL_DELTA)), dwFlags = MOUSEEVENTF_WHEEL } }
        };
        SendInput(1, [input], INPUT.Size);
    }
}
