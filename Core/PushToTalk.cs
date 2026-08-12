using System.Runtime.InteropServices;

namespace ClaudeScord;

// The push-to-talk key, watched globally.
//
// A low-level keyboard hook rather than RegisterHotKey: PTT has to work while the game you are
// playing has focus, and it must observe key-DOWN and key-UP separately, which RegisterHotKey
// cannot do — it only fires on press. The hook is passive; it never swallows the key, so the
// binding keeps working in whatever app is in front.
static class PushToTalk
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    static IntPtr _hook;
    // Held as a field: the delegate is passed to native code, and a local would be collected while
    // the hook is still installed — which crashes on the next keystroke rather than at install.
    static HookProc? _proc;

    /// Virtual-key code to watch.
    public static int Key = 0xA2;   // left Ctrl

    static bool _enabled;
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value) Install(); else Remove();
            if (!value) Down = false;
        }
    }

    /// True while the bound key is held. Read by the capture loop.
    public static bool Down { get; private set; }

    /// Raised on each edge, so the UI can show the "transmitting" state.
    public static event Action<bool>? Changed;

    static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("ptt", "hook failed: " + Marshal.GetLastWin32Error());
    }

    static void Remove()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            if (vk == Key)
            {
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = msg is WM_KEYUP or WM_SYSKEYUP;
                if ((down || up) && Down != down)
                {
                    Down = down;
                    VoiceClient.Current?.SetPttDown(down);
                    Changed?.Invoke(down);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// Human-readable name for the bound key, for the settings row.
    public static string KeyName(int vk) => vk switch
    {
        0xA0 => "Left Shift", 0xA1 => "Right Shift",
        0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
        0xA4 => "Left Alt", 0xA5 => "Right Alt",
        0x20 => "Space", 0x09 => "Tab", 0x14 => "Caps Lock",
        0x04 => "Mouse 4", 0x05 => "Mouse 5",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x5A => ((char)vk).ToString(),
        _ => "Key " + vk,
    };
}
