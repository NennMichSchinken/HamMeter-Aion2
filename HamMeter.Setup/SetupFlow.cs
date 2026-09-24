using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using HamMeter.UI;
using HamMeter.Wizard;
using ImGuiNET;

namespace HamMeter.Setup;

internal sealed class SetupFlow : WizardFlow
{
    private enum Page
    {
        Update,
        Welcome,
        Mode,
        Npcap,
        Install,
        Done,
    }

    private readonly SystemInfo.Installation? m_existing = SystemInfo.Installed();
    private readonly NpcapState m_npcapAtStart = SystemInfo.Npcap();
    private readonly string m_version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private Page m_page = Page.Welcome;
    private bool m_license;
    private bool m_useNpcap = true;
    private NpcapState? m_npcapChecked;
    private bool m_startMenu = true;
    private bool m_desktop;
    private bool m_launch = true;

    private volatile bool m_installing;
    private string? m_message;
    private bool m_messageIsError;

    // An existing install gets the short path: one "Update" page that reuses the
    // previous options. "Change options" switches to the full wizard.
    private bool m_quickUpdate;

    // preview: jump straight to a page (development screenshots, e.g. --preview=npcap).
    public SetupFlow(string? preview = null)
    {
        if (m_npcapAtStart == NpcapState.Ok)
        {
            m_npcapChecked = NpcapState.Ok;
        }

        if (m_existing is not null)
        {
            // Updates keep the license acceptance and the options of the install.
            m_license = true;
            m_quickUpdate = true;
            m_page = Page.Update;
            this.LoadPreviousOptions(m_existing);
        }

        if (preview is not null && Enum.TryParse(preview, true, out Page page))
        {
            m_license = true;
            m_quickUpdate = page == Page.Update;
            m_page = page;
        }
    }

    private void LoadPreviousOptions(SystemInfo.Installation install)
    {
        if (install.Tasks is { } tasks)
        {
            string[] t = tasks.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            m_startMenu = t.Contains("startmenu", StringComparer.OrdinalIgnoreCase);
            m_desktop = t.Contains("desktop", StringComparer.OrdinalIgnoreCase);
            m_useNpcap = !t.Contains("firewall", StringComparer.OrdinalIgnoreCase);
            return;
        }

        // Installs made before the options were recorded: read them off the system.
        m_startMenu = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "HamMeter.lnk"));
        m_desktop = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "HamMeter.lnk"));
        m_useNpcap = m_npcapAtStart == NpcapState.Ok;
    }

    private bool NpcapReady => m_npcapChecked == NpcapState.Ok;

    private List<Page> Pages =>
        m_quickUpdate ? [Page.Update, Page.Done]
        : m_useNpcap && m_npcapAtStart != NpcapState.Ok
            ? [Page.Welcome, Page.Mode, Page.Npcap, Page.Install, Page.Done]
            : [Page.Welcome, Page.Mode, Page.Install, Page.Done];

    public override string Subtitle => Strings.Setup;

    public override IReadOnlyList<string> Steps => this.Pages.Select(p => p switch
    {
        Page.Update => Strings.StepUpdate,
        Page.Welcome => Strings.StepWelcome,
        Page.Mode => Strings.StepMode,
        Page.Npcap => Strings.StepNpcap,
        Page.Install => Strings.StepInstall,
        _ => Strings.StepDone,
    }).ToList();

    public override int Current => this.Pages.IndexOf(m_page);

    public override bool CanGoBack => m_page is not (Page.Update or Page.Welcome or Page.Done) && !m_installing;

    public override bool CanClose => !m_installing;

    public override string NextLabel => m_page switch
    {
        Page.Update => m_messageIsError ? Strings.Retry : this.VersionCompare > 0 ? Strings.UpdateButton : Strings.ReinstallButton,
        Page.Install => m_messageIsError ? Strings.Retry : Strings.Install,
        Page.Done => m_quickUpdate ? Strings.Close : Strings.Finish,
        _ => Strings.Next,
    };

    public override bool NextEnabled => m_page switch
    {
        Page.Welcome => m_license,
        Page.Npcap => this.NpcapReady,
        Page.Update or Page.Install => !m_installing && Payload.Available,
        _ => true,
    };

    public override void Back()
    {
        int i = this.Current;
        if (i > 0)
        {
            m_page = this.Pages[i - 1];
            m_message = null;
        }
    }

    public override void Next()
    {
        switch (m_page)
        {
            case Page.Update:
            case Page.Install:
                this.StartInstall();
                return;
            case Page.Done:
                // After a quick update HamMeter was already started again.
                if (m_launch && !m_quickUpdate)
                {
                    this.Launch();
                }

                this.Close();
                return;
            default:
                m_page = this.Pages[this.Current + 1];
                return;
        }
    }

    public override void DrawPage(float width)
    {
        switch (m_page)
        {
            case Page.Update:
                this.DrawUpdate(width);
                break;
            case Page.Welcome:
                this.DrawWelcome();
                break;
            case Page.Mode:
                this.DrawMode(width);
                break;
            case Page.Npcap:
                this.DrawNpcap();
                break;
            case Page.Install:
                this.DrawInstall(width);
                break;
            case Page.Done:
                this.DrawDone(width);
                break;
        }
    }

    // ----- Pages ------------------------------------------------------------------------

    private void DrawWelcome()
    {
        Widgets.Heading(Strings.WelcomeTitle, 20f);
        Widgets.Hint(Strings.WelcomeText);
        ImGui.Dummy(new Vector2(0f, 6f));

        if (m_existing is not null)
        {
            ImGui.TextWrapped(Strings.UpdateText(m_existing.Version, m_version));
            return;
        }

        Widgets.Checkbox(Strings.AcceptLicense, ref m_license);
        ImGui.Dummy(new Vector2(0f, 2f));
        if (Widgets.Button(Strings.ViewLicense, ButtonKind.Normal, true, 0f, Icon.ExternalLink))
        {
            SystemInfo.OpenInBrowser("https://www.gnu.org/licenses/gpl-3.0.html");
        }
    }

    private void DrawMode(float width)
    {
        Widgets.Heading(Strings.ModeTitle, 20f);
        Widgets.Hint(Strings.ModeText);
        ImGui.Dummy(new Vector2(0f, 4f));

        if (Widgets.OptionCard("np", Icon.ShieldCheck, Strings.ModeNpcap, Strings.Recommended, Strings.ModeNpcapText, m_useNpcap, width))
        {
            m_useNpcap = true;
        }

        if (Widgets.OptionCard("raw", Icon.Zap, Strings.ModeRaw, null, Strings.ModeRawText, !m_useNpcap, width))
        {
            m_useNpcap = false;
        }

        if (m_useNpcap && m_npcapAtStart == NpcapState.Ok)
        {
            StatusLine(true, Strings.NpcapAlreadyInstalled);
        }
    }

    private void DrawNpcap()
    {
        Widgets.Heading(Strings.NpcapTitle, 20f);
        Widgets.Hint(Strings.NpcapText);
        ImGui.Dummy(new Vector2(0f, 4f));

        // 1: the browser downloads the exact installer file straight from npcap.com
        // (the download page lists SDK, symbols and source next to it).
        StepNumber(1);
        ImGui.BeginGroup();
        ImGui.TextUnformatted(Strings.NpcapStep1);
        if (Widgets.Button(Strings.NpcapDownload(SystemInfo.NpcapFile), ButtonKind.Primary, true, 0f, Icon.Download))
        {
            SystemInfo.OpenInBrowser(SystemInfo.NpcapDirectUrl);
        }

        Widgets.Hint(Strings.NpcapDownloadHint(SystemInfo.NpcapFile));
        if (Widgets.Link(Strings.NpcapNoDownload, Strings.NpcapOpenPageLink(SystemInfo.NpcapVersion)))
        {
            SystemInfo.OpenInBrowser(SystemInfo.NpcapPageUrl);
        }

        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0f, 4f));

        // 2: preview of the two options in Npcap's own installer (not clickable here).
        StepNumber(2);
        ImGui.BeginGroup();
        ImGui.TextUnformatted(Strings.NpcapStep2);
        OptionPreview(true, "Install Npcap in WinPcap API-compatible Mode", Strings.NpcapOptionOn);
        OptionPreview(false, "Restrict Npcap driver's access to Administrators only", Strings.NpcapOptionOff);
        Widgets.Hint(Strings.NpcapStep2Hint);
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0f, 4f));

        StepNumber(3);
        ImGui.BeginGroup();
        if (Widgets.Button(Strings.NpcapStep3))
        {
            m_npcapChecked = SystemInfo.Npcap();
        }

        if (m_npcapChecked is { } state)
        {
            ImGui.SameLine(0f, 12f);
            StatusLine(state == NpcapState.Ok, state switch
            {
                NpcapState.Ok => Strings.NpcapOk,
                NpcapState.NoWinPcapMode => Strings.NpcapNoCompat,
                NpcapState.AdminOnly => Strings.NpcapAdminOnly,
                _ => Strings.NpcapMissing,
            });
        }

        ImGui.EndGroup();
    }

    private void DrawInstall(float width)
    {
        Widgets.Heading(Strings.InstallTitle, 20f);
        Widgets.Hint(Strings.InstallText);
        ImGui.Dummy(new Vector2(0f, 4f));

        // Install folder (fixed: Program Files is admin-only, so nothing can be planted there).
        string dir = m_existing?.Directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HamMeter");
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetTextLineHeight() + 16f;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + h), Widgets.Col(Theme.Frame), 6f);
        float iconSize = ImGui.GetTextLineHeight();
        Icons.Draw(dl, Icon.Folder, new Vector2(p.X + 10f, p.Y + 8f), iconSize, Widgets.Col(Theme.Muted));
        dl.AddText(new Vector2(p.X + 18f + iconSize, p.Y + 8f), Widgets.Col(Theme.Text), dir);
        ImGui.Dummy(new Vector2(width, h));
        ImGui.Dummy(new Vector2(0f, 2f));

        if (!m_installing)
        {
            Widgets.Checkbox(Strings.StartMenu, ref m_startMenu);
            Widgets.Checkbox(Strings.Desktop, ref m_desktop);
        }

        ImGui.Dummy(new Vector2(0f, 6f));
        this.DrawProgress(width, Strings.Installing);
    }

    // Newer, same or older than what is installed (versions like 0.1.2).
    private int VersionCompare =>
        Version.TryParse(m_version, out Version? mine) && Version.TryParse(m_existing?.Version, out Version? installed)
            ? mine.CompareTo(installed)
            : 1;

    private void DrawUpdate(float width)
    {
        int cmp = this.VersionCompare;
        Widgets.Heading(cmp > 0 ? Strings.UpdateTitle : cmp == 0 ? Strings.ReinstallTitle : Strings.OlderVersionTitle, 20f);
        ImGui.TextUnformatted(Strings.UpdateVersions(m_existing?.Version ?? "?", m_version));
        ImGui.Dummy(new Vector2(0f, 2f));
        Widgets.Hint(cmp < 0 ? Strings.OlderVersionText : Strings.UpdateKeep);
        ImGui.Dummy(new Vector2(0f, 6f));

        // What stays: capture mode and shortcuts, in one quiet box.
        string options = Strings.CaptureMode(m_useNpcap)
            + (m_startMenu ? " · " + Strings.StartMenu : string.Empty)
            + (m_desktop ? " · " + Strings.Desktop : string.Empty);
        WizardHelpers.InfoBox(options, width);
        ImGui.Dummy(new Vector2(0f, 4f));

        if (!m_installing && Widgets.Link(Strings.ChangeOptionsLead, Strings.ChangeOptions))
        {
            m_quickUpdate = false;
            m_page = Page.Welcome;
            m_message = null;
            m_messageIsError = false;
            return;
        }

        ImGui.Dummy(new Vector2(0f, 10f));
        this.DrawProgress(width, Strings.Updating);
    }

    // Progress bar plus the status line under it (running / error / ready).
    private void DrawProgress(float width, string runningText)
    {
        ProgressBar(width, m_installing);

        if (m_installing)
        {
            Widgets.Hint(runningText);
        }
        else if (m_message is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, m_messageIsError ? Theme.Danger : Theme.Muted);
            ImGui.TextWrapped(m_message);
            ImGui.PopStyleColor();
        }
        else if (!Payload.Available)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
            ImGui.TextWrapped(Strings.PayloadBroken);
            ImGui.PopStyleColor();
        }
        else
        {
            Widgets.Hint(Strings.Ready);
        }
    }

    private void DrawDone(float width)
    {
        const float icon = 44f;
        ImGui.Dummy(new Vector2(0f, 18f));
        Vector2 p = ImGui.GetCursorScreenPos();
        Icons.Draw(ImGui.GetWindowDrawList(), Icon.CircleCheck, new Vector2(p.X + ((width - icon) * 0.5f), p.Y), icon, Widgets.Col(Theme.Success));
        ImGui.Dummy(new Vector2(0f, icon + 8f));

        Centered(m_quickUpdate ? Strings.UpdatedTitle : Strings.DoneTitle, 20f, Theme.Text, width);
        ImGui.Dummy(new Vector2(0f, 2f));
        Widgets.Hint(m_quickUpdate ? Strings.UpdatedText : Strings.DoneText);
        if (!m_quickUpdate)
        {
            ImGui.Dummy(new Vector2(0f, 8f));
            Widgets.Checkbox(Strings.LaunchNow, ref m_launch);
        }
    }

    // ----- Actions ----------------------------------------------------------------------

    private void StartInstall()
    {
        // Updating while the meter runs would hit locked files.
        if (SystemInfo.MeterRunning())
        {
            m_message = Strings.UninstallRunning;
            m_messageIsError = true;
            return;
        }

        List<string> tasks = new();
        if (m_startMenu)
        {
            tasks.Add("startmenu");
        }

        if (m_desktop)
        {
            tasks.Add("desktop");
        }

        if (!m_useNpcap)
        {
            tasks.Add("firewall");
        }

        // Remember whether this setup brought Npcap, so uninstall can offer to remove it.
        bool npcapByUs = (m_useNpcap && m_npcapAtStart != NpcapState.Ok && this.NpcapReady) || m_existing?.NpcapByUs == true;

        string args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"
            + $" /TASKS=\"{string.Join(',', tasks)}\""
            + (npcapByUs ? " /npcapbyus=1" : string.Empty);

        m_installing = true;
        m_message = null;
        m_messageIsError = false;
        Task.Run(() =>
        {
            (PayloadResult result, int code) = Payload.Run(args);
            m_messageIsError = result != PayloadResult.Success;
            m_message = result switch
            {
                PayloadResult.Success => null,
                PayloadResult.Cancelled => Strings.InstallCancelled,
                PayloadResult.Damaged => Strings.PayloadBroken,
                _ => Strings.InstallFailed(code),
            };
            m_installing = false;
            if (result == PayloadResult.Success)
            {
                // A quick update restarts HamMeter right away, like before the update.
                if (m_quickUpdate)
                {
                    this.Launch();
                }

                m_page = Page.Done;
            }
        });
    }

    private void Launch()
    {
        string? dir = SystemInfo.Installed()?.Directory;
        string exe = dir is null ? string.Empty : Path.Combine(dir, "HamMeter.exe");
        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
        }
    }

    // ----- Drawing helpers ------------------------------------------------------------

    private static void StepNumber(int n)
    {
        const float r = 11f;
        Vector2 p = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 c = new(p.X + r, p.Y + r);
        dl.AddCircleFilled(c, r, Widgets.Col(Theme.Frame));
        dl.AddCircle(c, r, Widgets.Col(Theme.Border), 0, 1.5f);
        string s = n.ToString();
        float size = ImGui.GetFontSize() * 0.8f;
        float w = Widgets.TextWidth(s, size);
        dl.AddText(ImGui.GetFont(), size, new Vector2(c.X - (w * 0.5f), c.Y - (size * 0.5f)), Widgets.Col(Theme.Text), s);
        ImGui.Dummy(new Vector2(r * 2f, r * 2f));
        ImGui.SameLine(0f, 12f);
    }

    // A row that looks like the option in Npcap's installer, with a "tick" / "leave off"
    // tag. Purely an illustration, so it does not react to clicks.
    private static void OptionPreview(bool on, string label, string tag)
    {
        const float box = 16f;
        const float pad = 8f;
        float width = ImGui.GetContentRegionAvail().X;
        float lineH = ImGui.GetTextLineHeight();
        Vector2 p = ImGui.GetCursorScreenPos();
        Vector2 size = new(width, lineH + (pad * 2f));
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + size, Widgets.Col(Theme.Frame), 6f);

        Vector2 b1 = new(p.X + pad, p.Y + ((size.Y - box) * 0.5f));
        Vector2 b2 = b1 + new Vector2(box, box);
        if (on)
        {
            dl.AddRectFilled(b1, b2, Widgets.Col(Theme.Accent), 3f);
            uint w = Widgets.Col(new Vector4(1f, 1f, 1f, 1f));
            dl.AddLine(b1 + new Vector2(box * 0.26f, box * 0.52f), b1 + new Vector2(box * 0.43f, box * 0.70f), w, 2f);
            dl.AddLine(b1 + new Vector2(box * 0.43f, box * 0.70f), b1 + new Vector2(box * 0.76f, box * 0.30f), w, 2f);
        }
        else
        {
            dl.AddRect(b1, b2, Widgets.Col(Theme.Muted), 3f, ImDrawFlags.None, 1.5f);
        }

        float small = ImGui.GetFontSize() * 0.85f;
        float tagW = Widgets.TextWidth(tag, small) + 14f;
        float labelWrap = width - box - (pad * 4f) - tagW;
        dl.AddText(ImGui.GetFont(), small, new Vector2(b2.X + pad, p.Y + ((size.Y - small) * 0.5f)), Widgets.Col(Theme.Text), label, labelWrap);

        Vector4 tagCol = on ? Theme.Success : Theme.Danger;
        Vector2 t1 = new(p.X + width - pad - tagW, p.Y + ((size.Y - small - 4f) * 0.5f));
        Vector2 t2 = t1 + new Vector2(tagW, small + 4f);
        dl.AddRectFilled(t1, t2, Widgets.Col(new Vector4(tagCol.X, tagCol.Y, tagCol.Z, 0.16f)), 5f);
        dl.AddText(ImGui.GetFont(), small, new Vector2(t1.X + 7f, t1.Y + 2f), Widgets.Col(tagCol), tag);

        ImGui.Dummy(size);
    }

    private static void StatusLine(bool ok, string text)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float size = ImGui.GetTextLineHeight();
        Vector4 col = ok ? Theme.Success : Theme.Danger;
        float y = p.Y + ((ImGui.GetFrameHeight() - size) * 0.5f);
        Icons.Draw(ImGui.GetWindowDrawList(), ok ? Icon.CircleCheck : Icon.CircleX, new Vector2(p.X, y), size, Widgets.Col(col));
        ImGui.SetCursorScreenPos(new Vector2(p.X + size + 6f, y));
        ImGui.PushStyleColor(ImGuiCol.Text, col);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    // Indeterminate bar while running (Inno reports no progress in silent mode).
    private static void ProgressBar(float width, bool running)
    {
        const float h = 6f;
        Vector2 p = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + h), Widgets.Col(Theme.Track), h * 0.5f);
        if (running)
        {
            float seg = width * 0.3f;
            float t = (float)(ImGui.GetTime() % 1.4) / 1.4f;
            float x0 = p.X + (((width + seg) * t) - seg);
            float a = MathF.Max(p.X, x0);
            float b = MathF.Min(p.X + width, x0 + seg);
            if (b > a)
            {
                dl.AddRectFilled(new Vector2(a, p.Y), new Vector2(b, p.Y + h), Widgets.Col(Theme.Accent), h * 0.5f);
            }
        }

        ImGui.Dummy(new Vector2(width, h));
    }

    private static void Centered(string text, float size, Vector4 col, float width)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float w = Widgets.TextWidth(text, size);
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), size, new Vector2(p.X + ((width - w) * 0.5f), p.Y), Widgets.Col(col), text);
        ImGui.Dummy(new Vector2(width, size));
    }
}
