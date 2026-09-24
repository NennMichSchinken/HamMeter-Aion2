using HamMeter.Wizard;
using Microsoft.Extensions.Logging;

namespace HamMeter.Update;

// Update state shared by the popup, the settings pill and "What's new". Checks run on
// the thread pool; the render thread only reads the fields below.
public sealed class UpdateController
{
    private readonly UpdateService m_service;
    private readonly Config m_config;
    private readonly ILogger<UpdateController> m_log;

    public UpdateController(UpdateService service, Config config, ILogger<UpdateController> log)
    {
        m_service = service;
        m_config = config;
        m_log = log;
    }

    public volatile UpdateInfo? Available;
    public volatile bool Checking;
    public volatile bool Busy;

    // Settings line under "Check for updates"; null when there is nothing to say.
    public volatile string? Status;

    // Popup line while downloading / after a failure.
    public volatile string? PopupStatus;
    public volatile bool PopupError;

    public bool PopupOpen { get; set; }

    public bool WhatsNewOpen { get; set; }

    // Picked up by the overlay's render thread: quit so the update can replace the files.
    public volatile bool QuitRequested;

    // The user's choice: the settings toggle, else the installer's, else on.
    public bool CheckOnStart
    {
        get => m_config.CheckForUpdates ?? SystemInfo.Installed()?.UpdateCheck != "manual";
        set
        {
            m_config.CheckForUpdates = value;
            m_config.Save();
        }
    }

    // Development preview: never goes online.
    public bool PreviewMode { get; set; }

    // Development preview: a made-up newer release, popup open.
    public void ShowPreviewUpdate(bool important)
    {
        var next = new Version(Changelog.Current.Major, Changelog.Current.Minor, Changelog.Current.Build + 1);
        this.Available = new UpdateInfo(
            next,
            DateOnly.FromDateTime(DateTime.Today.AddDays(8)),
            important,
            [new Note(NoteKind.Fixed, "Damage is correct again after the Aion 2 patch."), new Note(NoteKind.New, "Healing details per skill.")],
            new Uri("https://github.com/"),
            new Uri("https://github.com/"));
        this.PopupOpen = true;
    }

    public void CheckAtStartup()
    {
        if (this.CheckOnStart && !this.PreviewMode)
        {
            _ = this.CheckAsync(manual: false);
        }
    }

    public async Task CheckAsync(bool manual)
    {
        if (this.Checking)
        {
            return;
        }

        this.Checking = true;
        this.Status = manual ? "Checking…" : null;
        try
        {
            UpdateInfo? update = await m_service.CheckAsync();
            this.Available = update;
            if (update is null)
            {
                this.Status = manual ? "You're up to date." : null;
                return;
            }

            this.Status = $"Version {update.Version.ToString(3)} is available.";

            // An ignored version only stays quiet at startup; asking by hand always shows it.
            if (manual || !string.Equals(m_config.IgnoredVersion, update.Version.ToString(3), StringComparison.Ordinal))
            {
                this.PopupStatus = null;
                this.PopupError = false;
                this.PopupOpen = true;
            }
        }
        catch (Exception ex)
        {
            // Offline or GitHub unreachable: never an error at startup, just no popup.
            m_log.LogInformation("Update check failed: {Message}", ex.Message);
            this.Status = manual ? "Couldn't reach GitHub. Check your connection and try again." : null;
        }
        finally
        {
            this.Checking = false;
        }
    }

    public void Later() => this.PopupOpen = false;

    public void Ignore()
    {
        if (this.Available is { } update)
        {
            m_config.IgnoredVersion = update.Version.ToString(3);
            m_config.Save();
        }

        this.PopupOpen = false;
    }

    public async Task UpdateNowAsync()
    {
        if (this.Available is not { } update || this.Busy)
        {
            return;
        }

        this.Busy = true;
        this.PopupError = false;
        try
        {
            var progress = new Progress<string>(step => this.PopupStatus = step == "verify"
                ? "Checking the signature…"
                : "Downloading…");
            if (await m_service.DownloadAndRunAsync(update, progress))
            {
                this.PopupStatus = "Starting the update…";
                this.QuitRequested = true; // the setup waits for HamMeter to close, updates, then starts it again
                return;
            }

            this.PopupError = true;
            this.PopupStatus = "The download is not signed by HamMeter. Nothing was installed.";
        }
        catch (Exception ex)
        {
            m_log.LogWarning("Update download failed: {Message}", ex.Message);
            this.PopupError = true;
            this.PopupStatus = "Download failed. Check your connection and try again.";
        }
        finally
        {
            this.Busy = false;
        }
    }
}
