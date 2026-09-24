using System.Runtime.InteropServices;
using ClickableTransparentOverlay;
using HamMeter.Combat;
using ImGuiNET;

namespace HamMeter.UI;

// A transparent, always-on-top window covering the screen. Only the ImGui windows
// (meter, settings) take mouse input; everywhere else clicks go through to the game.
// Aion 2 has to run in borderless windowed mode for the overlay to sit on top of it.
public sealed class HamMeterOverlay : Overlay
{
    // Microsoft JhengHei ships with Windows and covers Latin plus Traditional Chinese,
    // which Taiwan-server character names need.
    private static readonly string[] FontCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msjh.ttc"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
    ];

    private const int FontSize = 18;

    private readonly Config m_config;
    private readonly EncounterTracker m_tracker;
    private readonly SettingsWindow m_settings;
    private readonly MeterWindow m_meter;
    private readonly Update.UpdateController m_updates;
    private readonly UpdateWindows m_updateWindows;
    private readonly Dictionary<string, IntPtr> m_icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<TrayIcon.Command> m_trayCommands = new();
    private TrayIcon? m_tray;
    private IntPtr m_iniPath;

    public HamMeterOverlay(Config config, EncounterTracker tracker, Func<string?> status, Update.UpdateController updates)
        : base("HamMeter", true)
    {
        m_config = config;
        m_tracker = tracker;
        m_updates = updates;
        m_updateWindows = new UpdateWindows(updates);
        m_settings = new SettingsWindow(config, updates);
        m_meter = new MeterWindow(config, m_settings, tracker, this.ClassIcon, status);
        m_settings.QuitRequested += this.Close;
    }

    public void ShowSettingsPreview(bool openTextureList) => m_settings.ShowBarsSection(openTextureList);

    public void ShowCornerPreview() => m_meter.PreviewCorner = true;

    // Raised after a setting changed (so the host can forward e.g. packet recording).
    public event Action? SettingsChanged
    {
        add => m_settings.Changed += value;
        remove => m_settings.Changed -= value;
    }

    protected override unsafe Task PostInitialized()
    {
        this.VSync = true;

        // Cover every monitor. The library's default window is small, which clipped the
        // meter and settings at its edges and left no room to dock the settings beside
        // the meter. Everything outside the ImGui windows stays click-through.
        this.Position = new System.Drawing.Point(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen));
        m_meter.PrimaryOrigin = new System.Numerics.Vector2(-this.Position.X, -this.Position.Y);
        m_updateWindows.PrimaryCenter = m_meter.PrimaryOrigin + new System.Numerics.Vector2(GetSystemMetrics(0) * 0.5f, GetSystemMetrics(1) * 0.5f);
        this.Size = new System.Drawing.Size(GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

        BarStyles.Textures = this.BarTexture;

        // Like other game overlays: no taskbar button (its preview would show the whole
        // transparent screen) and not in Alt+Tab. The tray icon is the way in.
        HideFromTaskbar(this.window.Handle);
        m_tray = new TrayIcon(() => m_meter.Visible);
        m_tray.Clicked += m_trayCommands.Enqueue;

        // Keep window positions next to the config instead of the working directory.
        m_iniPath = Marshal.StringToCoTaskMemUTF8(Path.Combine(Config.DataDirectory, "imgui.ini"));
        ImGui.GetIO().NativePtr->IniFilename = (byte*)m_iniPath;

        string? font = FontCandidates.FirstOrDefault(File.Exists);
        if (font is not null)
        {
            FontGlyphRangeType ranges = font.EndsWith("msjh.ttc", StringComparison.OrdinalIgnoreCase)
                ? FontGlyphRangeType.ChineseFull
                : FontGlyphRangeType.English;
            this.ReplaceFont(font, FontSize, ranges);
        }

        // One request to GitHub when enabled; offline simply means no popup.
        m_updates.CheckAtStartup();
        return Task.CompletedTask;
    }

    protected override void Render()
    {
        while (m_trayCommands.TryDequeue(out TrayIcon.Command cmd))
        {
            switch (cmd)
            {
                case TrayIcon.Command.ToggleMeter:
                    m_meter.Visible = !m_meter.Visible;
                    if (!m_meter.Visible)
                    {
                        m_settings.Visible = false;
                    }

                    break;
                case TrayIcon.Command.Settings:
                    m_meter.Visible = true;
                    m_settings.Visible = true;
                    break;
                case TrayIcon.Command.Quit:
                    this.Close();
                    return;
            }
        }

        m_tracker.CombatTimeoutSeconds = m_config.CombatTimeout;
        m_tracker.Tick(DateTime.Now);

        m_settings.Draw();
        m_meter.Draw();
        m_updateWindows.Draw();

        // A verified update was started: quit so it can replace the files.
        if (m_updates.QuitRequested)
        {
            this.Close();
        }
    }

    // Bar textures are embedded resources (Assets/Bars), uploaded to the GPU on first use.
    private IntPtr BarTexture(string file)
    {
        string key = "bar:" + file;
        if (m_icons.TryGetValue(key, out IntPtr handle))
        {
            return handle;
        }

        handle = IntPtr.Zero;
        try
        {
            using Stream? s = typeof(HamMeterOverlay).Assembly.GetManifestResourceStream("HamMeter.Bars." + file);
            if (s is not null)
            {
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(s);
                this.AddOrGetImagePointer(key, image, false, out handle);
            }
        }
        catch (Exception)
        {
            handle = IntPtr.Zero; // drawn flat instead
        }

        m_icons[key] = handle;
        return handle;
    }

    private IntPtr ClassIcon(string job)
    {
        if (m_icons.TryGetValue(job, out IntPtr handle))
        {
            return handle;
        }

        handle = IntPtr.Zero;
        string? path = ClassInfo.IconPath(job);
        if (path is not null && File.Exists(path))
        {
            try
            {
                this.AddOrGetImagePointer(path, false, out handle, out _, out _);
            }
            catch (Exception)
            {
                handle = IntPtr.Zero;
            }
        }

        m_icons[job] = handle;
        return handle;
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static void HideFromTaskbar(IntPtr hwnd)
    {
        const int gwlExStyle = -20;
        const long wsExToolWindow = 0x00000080;
        const long wsExAppWindow = 0x00040000;

        ShowWindow(hwnd, 0);
        long style = GetWindowLongPtr(hwnd, gwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, gwlExStyle, new IntPtr((style | wsExToolWindow) & ~wsExAppWindow));
        ShowWindow(hwnd, 8); // SW_SHOWNA: show without stealing focus from the game
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    protected override void Dispose(bool disposing)
    {
        m_tray?.Dispose();
        m_tray = null;
        base.Dispose(disposing);
        if (m_iniPath != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(m_iniPath);
            m_iniPath = IntPtr.Zero;
        }
    }
}
