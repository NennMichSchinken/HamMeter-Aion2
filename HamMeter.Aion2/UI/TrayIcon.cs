using System.Runtime.InteropServices;

namespace HamMeter.UI;

// HamMeter's icon in the notification area (like Discord, Steam or ACT overlays): the
// full-screen overlay has no taskbar button, so this is how the user reaches it.
//   left click  -> show / hide the meter
//   right click -> menu: meter, settings, quit
// Plain Win32 (Shell_NotifyIcon + a message-only window) instead of WinForms, which
// would add several MB of runtime for one icon. Clicks arrive on the tray thread and are
// raised as events; the overlay queues them onto its render thread.
public sealed class TrayIcon : IDisposable
{
    public enum Command
    {
        ToggleMeter,
        Settings,
        Quit,
    }

    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const int IdToggle = 1;
    private const int IdSettings = 2;
    private const int IdQuit = 3;

    private readonly Thread m_thread;
    private readonly ManualResetEventSlim m_ready = new();
    private readonly WndProc m_wndProc; // kept alive: the native side holds a pointer to it
    private readonly Func<bool> m_meterVisible;
    private IntPtr m_hwnd;
    private IntPtr m_icon;
    private uint m_taskbarCreated;

    public event Action<Command>? Clicked;

    public TrayIcon(Func<bool> meterVisible)
    {
        m_meterVisible = meterVisible;
        m_wndProc = this.WindowProc;
        m_thread = new Thread(this.Run) { IsBackground = true, Name = "HamMeter tray" };
        m_thread.SetApartmentState(ApartmentState.STA);
        m_thread.Start();
        m_ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (m_hwnd != IntPtr.Zero)
        {
            PostMessage(m_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        m_thread.Join(TimeSpan.FromSeconds(2));
        m_ready.Dispose();
    }

    private void Run()
    {
        IntPtr instance = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(m_wndProc),
            Instance = instance,
            ClassName = "HamMeterTray",
        };
        RegisterClassEx(ref wc);

        // Message-only window: never visible, only receives the tray callbacks.
        m_hwnd = CreateWindowEx(0, "HamMeterTray", "HamMeter", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
        m_taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        m_icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? string.Empty, 0);
        this.AddIcon();
        m_ready.Set();

        while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        this.RemoveIcon();
        if (m_icon.ToInt64() > 1)
        {
            DestroyIcon(m_icon);
        }

        UnregisterClass("HamMeterTray", instance);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event is in the low word of lParam. A left click
            // arrives as WM_LBUTTONUP *and* NIN_SELECT, so only NIN_SELECT toggles.
            switch (lParam.ToInt64() & 0xFFFF)
            {
                case NinSelect:
                case NinKeySelect:
                    this.Clicked?.Invoke(Command.ToggleMeter);
                    break;
                case WmContextMenu:
                    this.ShowMenu();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == m_taskbarCreated)
        {
            this.AddIcon(); // Explorer restarted: the icon is gone, add it again
            return IntPtr.Zero;
        }

        if (msg == WmClose)
        {
            DestroyWindow(hwnd);
            return IntPtr.Zero;
        }

        if (msg == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MfString | (m_meterVisible() ? MfChecked : 0), IdToggle, "Show meter");
        AppendMenu(menu, MfString, IdSettings, "Settings");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, IdQuit, "Quit HamMeter");

        GetCursorPos(out Point pt);
        SetForegroundWindow(m_hwnd); // so the menu closes when clicking elsewhere
        int cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign, pt.X, pt.Y, m_hwnd, IntPtr.Zero);
        PostMessage(m_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case IdToggle:
                this.Clicked?.Invoke(Command.ToggleMeter);
                break;
            case IdSettings:
                this.Clicked?.Invoke(Command.Settings);
                break;
            case IdQuit:
                this.Clicked?.Invoke(Command.Quit);
                break;
        }
    }

    private void AddIcon()
    {
        NotifyIconData data = this.Data();
        data.Flags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = m_icon;
        data.Tip = "HamMeter";
        Shell_NotifyIcon(NimAdd, ref data);

        data.Version = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private void RemoveIcon()
    {
        NotifyIconData data = this.Data();
        Shell_NotifyIcon(NimDelete, ref data);
    }

    private NotifyIconData Data() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Hwnd = m_hwnd,
        Id = 1,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    // ----- Win32 ------------------------------------------------------------------------

    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const long WmContextMenu = 0x007B;
    private const long NinSelect = 0x0400;
    private const long NinKeySelect = 0x0401;
    private const int NimAdd = 0;
    private const int NimDelete = 2;
    private const int NimSetVersion = 4;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;
    private const int NifShowTip = 0x80;
    private const int NotifyIconVersion4 = 4;
    private const uint MfString = 0x0;
    private const uint MfChecked = 0x8;
    private const uint MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100;
    private const uint TpmRightButton = 0x2;
    private const uint TpmBottomAlign = 0x20;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Hwnd;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
