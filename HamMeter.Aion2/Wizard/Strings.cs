using System.Globalization;

namespace HamMeter.Wizard;

// Setup / uninstall texts in English (default) and German, picked from the Windows
// display language.
internal static class Strings
{
    private static readonly bool German =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);

    private static string T(string en, string de) => German ? de : en;

    // Frame
    public static string Setup => T("Setup", "Setup");
    public static string Uninstall => T("Uninstall", "Deinstallation");
    public static string Back => T("Back", "Zurück");
    public static string Next => T("Next", "Weiter");
    public static string Cancel => T("Cancel", "Abbrechen");
    public static string Close => T("Close", "Schließen");
    public static string Finish => T("Finish", "Fertig");
    public static string Install => T("Install", "Installieren");
    public static string Retry => T("Retry", "Erneut versuchen");

    // Steps
    public static string StepWelcome => T("Welcome", "Willkommen");
    public static string StepMode => T("Capture mode", "Weg wählen");
    public static string StepNpcap => T("Npcap", "Npcap");
    public static string StepInstall => T("Install", "Installieren");
    public static string StepDone => T("Done", "Fertig");
    public static string StepUninstall => T("Uninstall", "Deinstallieren");

    // Welcome
    public static string WelcomeTitle => T("Welcome to HamMeter for Aion 2", "Willkommen bei HamMeter für Aion 2");
    public static string WelcomeText => T(
        "A lightweight damage meter in the HamMeter design. It only reads Aion 2's network traffic, never touches the game and never sends anything to the internet.",
        "Der schlanke Damage-Meter im HamMeter-Design. Liest nur den Netzwerkverkehr von Aion 2, greift nicht ins Spiel ein und sendet nichts ins Internet.");
    public static string UpdateText(string from, string to) => T(
        $"HamMeter {from} is installed. This updates it to {to}. Your settings are kept.",
        $"HamMeter {from} ist installiert. Das aktualisiert auf {to}. Deine Einstellungen bleiben erhalten.");
    public static string AcceptLicense => T("I accept the license (GPL-3.0)", "Ich akzeptiere die Lizenz (GPL-3.0)");
    public static string ViewLicense => T("View license", "Lizenz ansehen");

    // Mode
    public static string ModeTitle => T("How should HamMeter read the game?", "Wie soll HamMeter mitlesen?");
    public static string ModeText => T("You can change this later by running setup again.", "Du kannst das später ändern, indem du das Setup erneut startest.");
    public static string ModeNpcap => T("With Npcap", "Mit Npcap");
    public static string Recommended => T("Recommended", "Empfohlen");
    public static string ModeNpcapText => T(
        "Runs without administrator rights. Npcap is installed once, we guide you step by step.",
        "Läuft ohne Admin-Rechte. Npcap wird einmalig installiert, wir führen dich Schritt für Schritt durch.");
    public static string ModeRaw => T("Without Npcap", "Ohne Npcap");
    public static string ModeRawText => T(
        "No extra install. Windows asks for administrator rights on every start, and a firewall rule is added.",
        "Keine Zusatzinstallation. Windows fragt bei jedem Start nach Admin-Rechten, eine Firewall-Freigabe wird eingerichtet.");
    public static string NpcapAlreadyInstalled => T("Npcap is already installed and set up correctly.", "Npcap ist bereits installiert und richtig eingerichtet.");

    // Npcap
    public static string NpcapTitle => T("Set up Npcap", "Npcap einrichten");
    public static string NpcapText => T("Three short steps. You can continue once the check is green.", "Drei kurze Schritte. Weiter geht es, sobald die Prüfung grün ist.");
    public static string NpcapStep1 => T("Download the Npcap installer", "Npcap-Installer herunterladen");
    public static string NpcapDownload(string file) => T($"Download {file}", $"{file} herunterladen");
    public static string NpcapDownloadHint(string file) => T(
        $"Official file from npcap.com. Open {file} when the download is finished.",
        $"Offizielle Datei von npcap.com. Öffne {file}, wenn der Download fertig ist.");
    public static string NpcapNoDownload => T("Didn't start?", "Startet nicht?");
    public static string NpcapOpenPageLink(string version) => T(
        $"Download page, pick \"Npcap {version} installer\"",
        $"Download-Seite, dort \"Npcap {version} installer\" wählen");
    public static string NpcapStep2 => T("In the Npcap installer, set these two options", "Im Npcap-Installer diese zwei Optionen setzen");
    public static string NpcapOptionOn => T("tick", "anhaken");
    public static string NpcapOptionOff => T("leave off", "aus lassen");
    public static string NpcapStep2Hint => T("Leave everything else as it is.", "Alles andere so lassen.");
    public static string NpcapStep3 => T("Check", "Prüfen");
    public static string NpcapOk => T("Npcap is set up correctly", "Npcap richtig installiert");
    public static string NpcapMissing => T("Npcap not found yet", "Npcap noch nicht gefunden");
    public static string NpcapNoCompat => T("Found, but without WinPcap mode. Reinstall and tick the box.", "Gefunden, aber ohne WinPcap-Modus. Neu installieren und Haken setzen.");
    public static string NpcapAdminOnly => T("Found, but restricted to administrators. Reinstall without that option.", "Gefunden, aber nur für Admins freigegeben. Ohne diese Option neu installieren.");

    // Install
    public static string InstallTitle => T("Install", "Installieren");
    public static string InstallText => T("HamMeter is installed into a protected folder.", "HamMeter wird in einen geschützten Ordner installiert.");
    public static string UpdatesHeading => T("UPDATES", "UPDATES");
    public static string UpdatesAuto => T("Check for updates when HamMeter starts", "Beim Start nach Updates suchen");
    public static string UpdatesAutoText => T(
        "One short request to GitHub when HamMeter starts. No data is sent.",
        "Eine kurze Anfrage an GitHub, wenn HamMeter startet. Es werden keine Daten gesendet.");
    public static string UpdatesManual => T("Manual only", "Nur manuell");
    public static string UpdatesManualText => T(
        "HamMeter never goes online by itself. Check under Settings > Data & App > \"Check for updates\".",
        "HamMeter geht nie von selbst online. Updates findest du unter Einstellungen > Data & App > \"Check for updates\".");
    public static string WaitingForMeter => T("Waiting for HamMeter to close…", "Warte, bis HamMeter geschlossen ist…");
    public static string StartMenu => T("Start menu entry", "Startmenü-Eintrag");
    public static string Desktop => T("Desktop shortcut", "Desktop-Verknüpfung");
    public static string Ready => T("Ready", "Bereit");
    public static string Installing => T("Installing… confirm the Windows prompt.", "Wird installiert… bestätige die Windows-Abfrage.");
    public static string InstallCancelled => T("The Windows prompt was declined. Nothing was installed.", "Die Windows-Abfrage wurde abgelehnt. Es wurde nichts installiert.");
    public static string InstallFailed(int code) => T($"Installation failed (code {code}).", $"Installation fehlgeschlagen (Code {code}).");
    public static string PayloadBroken => T("This setup file is damaged. Download it again.", "Diese Setup-Datei ist beschädigt. Lade sie neu herunter.");

    // Update
    public static string StepUpdate => T("Update", "Update");
    public static string UpdateTitle => T("Update available", "Update verfügbar");
    public static string ReinstallTitle => T("Already installed", "Bereits installiert");
    public static string ReinstallButton => T("Reinstall", "Neu installieren");
    public static string OlderVersionTitle => T("Newer version installed", "Neuere Version installiert");
    public static string OlderVersionText => T(
        "This setup is older than the installed HamMeter. Installing it goes back to the older version.",
        "Dieses Setup ist älter als das installierte HamMeter. Installieren wechselt zurück auf die ältere Version.");
    public static string UpdateVersions(string from, string to) => T($"HamMeter {from}  →  {to}", $"HamMeter {from}  →  {to}");
    public static string UpdateKeep => T(
        "Your settings and options stay the same. HamMeter starts again when the update is done.",
        "Deine Einstellungen und Optionen bleiben gleich. HamMeter startet nach dem Update wieder.");
    public static string CaptureMode(bool npcap) => npcap
        ? T("Capture: with Npcap", "Mitlesen: mit Npcap")
        : T("Capture: without Npcap", "Mitlesen: ohne Npcap");
    public static string ChangeOptionsLead => T("Want to change something?", "Etwas ändern?");
    public static string ChangeOptions => T("Change options", "Optionen ändern");
    public static string UpdateButton => T("Update", "Aktualisieren");
    public static string Updating => T("Updating… confirm the Windows prompt.", "Wird aktualisiert… bestätige die Windows-Abfrage.");
    public static string UpdatedTitle => T("HamMeter is up to date", "HamMeter ist aktuell");
    public static string UpdatedText => T(
        "HamMeter was updated and started again. You'll find its icon in the notification area of the taskbar.",
        "HamMeter wurde aktualisiert und wieder gestartet. Du findest das Icon im Infobereich der Taskleiste.");

    // Done
    public static string DoneTitle => T("HamMeter is ready", "HamMeter ist bereit");
    public static string DoneText => T(
        "Start Aion 2 in borderless windowed mode. HamMeter appears as an overlay on top of the game, its icon sits in the notification area of the taskbar.",
        "Starte Aion 2 im randlosen Fenster. HamMeter erscheint als Overlay über dem Spiel, das Icon findest du im Infobereich der Taskleiste.");
    public static string LaunchNow => T("Launch HamMeter now", "HamMeter jetzt starten");

    // Uninstall
    public static string UninstallTitle => T("Uninstall HamMeter", "HamMeter deinstallieren");
    public static string UninstallText => T(
        "The program, start menu entries and the firewall rule are always removed.",
        "Programm, Startmenü-Eintrag und Firewall-Regel werden immer entfernt.");
    public static string DeleteData => T("Delete settings, log and recordings", "Einstellungen, Log und Aufnahmen löschen");
    public static string RemoveNpcap => T("Also uninstall Npcap", "Npcap ebenfalls deinstallieren");
    public static string NpcapByUs => T("Npcap was installed together with HamMeter.", "Npcap wurde mit HamMeter installiert.");
    public static string NpcapShared => T(
        "Npcap was already installed before HamMeter. Other programs such as Wireshark may still need it.",
        "Npcap war schon vor HamMeter installiert. Andere Programme wie Wireshark nutzen es eventuell noch.");
    public static string UninstallRunning => T(
        "HamMeter is still running. Quit it first: right-click its icon in the notification area, then \"Quit HamMeter\".",
        "HamMeter läuft noch. Beende es zuerst: Rechtsklick auf das Icon im Infobereich, dann \"Quit HamMeter\".");
    public static string Removing => T("Removing HamMeter…", "HamMeter wird entfernt…");
    public static string UninstallCancelled => T("The Windows prompt was declined. Nothing was removed.", "Die Windows-Abfrage wurde abgelehnt. Es wurde nichts entfernt.");
}
