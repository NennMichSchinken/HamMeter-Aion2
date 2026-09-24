using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using HamMeter.UI;
using ImGuiNET;

namespace HamMeter.Wizard;

// "Apps & Features" runs `HamMeter.exe --uninstall`. This page runs without elevation;
// only Inno's uninstaller (started hidden) asks for administrator rights.
internal sealed class UninstallFlow : WizardFlow
{
    private readonly SystemInfo.Installation? m_install = SystemInfo.Installed();
    private readonly string? m_npcapUninstaller = SystemInfo.NpcapUninstaller();

    private bool m_deleteData = true;
    private bool m_removeNpcap;
    private string? m_error;

    public UninstallFlow()
    {
        // Only suggest removing Npcap when HamMeter's setup installed it.
        m_removeNpcap = m_install?.NpcapByUs == true;
    }

    public override string Subtitle => Strings.Uninstall;

    public override IReadOnlyList<string> Steps => [Strings.StepUninstall];

    public override int Current => 0;

    public override string NextLabel => Strings.StepUninstall;

    public override bool NextEnabled => m_install is not null;

    public override ButtonKind NextKind => ButtonKind.Danger;

    public override void DrawPage(float width)
    {
        Widgets.Heading(Strings.UninstallTitle, 20f);
        Widgets.Hint(Strings.UninstallText);
        ImGui.Dummy(new Vector2(0f, 6f));

        Widgets.Checkbox(Strings.DeleteData, ref m_deleteData, Config.DataDirectory);
        ImGui.Dummy(new Vector2(0f, 4f));

        if (m_npcapUninstaller is not null)
        {
            Widgets.Checkbox(Strings.RemoveNpcap, ref m_removeNpcap, m_install?.NpcapByUs == true ? Strings.NpcapByUs : null);
            if (m_install?.NpcapByUs != true)
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                WizardHelpers.InfoBox(Strings.NpcapShared, width);
            }
        }

        if (m_error is not null)
        {
            ImGui.Dummy(new Vector2(0f, 6f));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
            ImGui.TextWrapped(m_error);
            ImGui.PopStyleColor();
        }
    }

    public override void Back()
    {
    }

    public override void Next()
    {
        if (m_install is null)
        {
            return;
        }

        if (SystemInfo.MeterRunning())
        {
            m_error = Strings.UninstallRunning;
            return;
        }

        string uninstaller = Path.Combine(m_install.Directory, "uninstall", "unins000.exe");
        try
        {
            // Returns once the UAC prompt is accepted; this process then exits right
            // away so HamMeter.exe is no longer in use when Inno removes the folder.
            WizardHelpers.RunElevated(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        }
        catch (Win32Exception)
        {
            m_error = Strings.UninstallCancelled;
            return;
        }

        if (m_deleteData)
        {
            try
            {
                if (Directory.Exists(Config.DataDirectory))
                {
                    Directory.Delete(Config.DataDirectory, true);
                }
            }
            catch (IOException)
            {
                // Leftovers in use: nothing sensitive stays readable (recordings are encrypted).
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        SystemInfo.DeleteSetupLeftovers();

        if (m_removeNpcap && m_npcapUninstaller is not null)
        {
            try
            {
                WizardHelpers.RunElevated(m_npcapUninstaller, string.Empty);
            }
            catch (Win32Exception)
            {
                // Declined: Npcap stays, it can be removed from Apps & Features later.
            }
        }

        this.Close();
    }
}
