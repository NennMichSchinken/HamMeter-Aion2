# HamMeter for Aion 2

A minimalist, easy-to-read damage meter for **Aion 2**, with the same look and settings as [HamMeter for FFXIV](https://github.com/NennMichSchinken/HamMeter). It runs as a transparent overlay on top of the game.

> **Status: early / experimental.** Damage Done and Deaths use the proven packet layout from Kuroukihime's meter. Healing and Damage Taken are new and still need to be verified in game.

## How it works

HamMeter reads the game's network packets passively (via Npcap, or Windows raw sockets when Npcap is not installed). It does not inject code, read or modify game memory, or send anything anywhere.

- **Packet capture, parsing and game data** come from [Kuroukihime/AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter) (GPL-3.0), included unchanged as a git submodule in `external/AionDpsMeter`.
- **HamMeter's own layer** (`HamMeter.Aion2/Capture`) reads the combat packets a second time to also keep what a pure DPS meter drops: healing, damage taken and deaths of every party member. The heal effect types and the damage-taken rule follow [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter).
- **The overlay** (`HamMeter.Aion2/UI`) is a port of the FFXIV HamMeter's ImGui drawing code onto [ClickableTransparentOverlay](https://github.com/zaafar/ClickableTransparentOverlay).

## Principles

1. **Security first.** HamMeter touches as little data as possible, stores almost nothing, and never talks to the network.
2. **Lightweight.** No bloat: Windows built-ins over extra libraries, no feature without a reason.

### Security model

- **Minimal network access.** HamMeter opens no listening port. Its only outgoing connection is the update check: one HTTPS request to the GitHub API when it starts (can be turned off in the setup and in Settings → Data & App). Nothing is sent besides the request itself. Kuroukihime's update checker and icon downloader are not used.
- **Signed updates.** "Update now" only runs a downloaded setup whose ECDSA-P256 signature matches the public key built into HamMeter (`HamMeter.Aion2/Update/release-public-key.txt`). The private key never leaves the author's PC, so a compromised GitHub account alone cannot push code to users. Downloads are HTTPS-only, GitHub hosts only, size-capped, and locked between verification and start.
- **Least privilege.** With Npcap installed, HamMeter runs as a normal user. Only without Npcap does it restart itself with administrator rights (UAC prompt), because Windows raw sockets require them.
- **Narrow capture (raw sockets).** The network card is not switched to promiscuous mode (`RCVALL_IPLEVEL`). A packet is only looked at if it belongs exactly to a TCP connection owned by `Aion2.exe`; everything else is dropped after the header checks. All lengths are bounds-checked, fragments are dropped, and a connection is only used after Aion's heartbeat was seen on it.
- **Data minimisation.** No database: fight history lives in memory and is gone when HamMeter closes. Logs contain no player names or packet contents. `config.json` holds only look-and-feel settings.
- **Encrypted recordings.** The optional packet recording (for checking numbers) is AES-256-GCM encrypted, with the key protected by Windows DPAPI for your Windows account only. It is off on every start and recordings are deleted after 7 days.
- **Memory-safe parsing.** All packet parsing is managed C# with bounds-checked readers.

## Requirements

- Windows 10/11 x64
- Aion 2 in **borderless windowed** mode (an overlay cannot draw over exclusive fullscreen)
- Optional but recommended: [Npcap](https://npcap.com/#download) with **"WinPcap API-compatible Mode"** checked. Without it HamMeter needs administrator rights and may need a Windows Firewall exception.

## Features

- Metrics: Damage Done, Damage Taken, Healing Done, Healing Taken, Deaths
- Current fight / Overall / per-fight history, named after the main target
- Class icons or text tags, per-class or per-role colours (fully editable)
- Nine bar textures to choose from (Flat, Smooth, Gradient, Bevel, Sheen, Glow, Glow top, Glow bottom, Aurora)
- Lives in the notification area (tray icon), no taskbar button
- Test mode to preview the layout without being in combat
- All HamMeter look settings: sizes, spacing, opacity, colours, and more
- Encrypted packet recording (Settings → Data & App) to replay and check a fight

Settings, the log and recordings are stored in `%APPDATA%\HamMeter-Aion2`.

## Install and uninstall

Run `HamMeter-Setup-<version>.exe`. The wizard (in the HamMeter design) lets you choose:

- **With Npcap (recommended):** HamMeter runs without administrator rights. The wizard walks you through installing Npcap and checks that it is set up correctly before you can continue.
- **Without Npcap:** nothing extra to install, but HamMeter asks for administrator rights on every start and a firewall rule (inbound TCP, HamMeter.exe only) is added.

HamMeter is installed to `C:\Program Files\HamMeter`. Running a newer setup updates in place and keeps your settings.

Uninstall from **Apps & Features**. You can also delete your settings, log and recordings, and uninstall Npcap (pre-selected only if HamMeter's setup installed it). Nothing of HamMeter stays behind.

How the setup works: `HamMeter-Setup.exe` is our wizard, running without elevation. The [Inno Setup](https://jrsoftware.org/isinfo.php) engine is embedded, verified by SHA-256 and run invisibly with your choices; it is the only part that asks for administrator rights.

## Building

```bash
git clone --recurse-submodules <this repo>
dotnet build HamMeter.Aion2.slnx
dotnet test HamMeter.Aion2.slnx
```

Release build (needs [Inno Setup 6](https://jrsoftware.org/isdl.php): `winget install JRSoftware.InnoSetup`):

```powershell
.\build\build.ps1 -Version 0.1.0
```

This writes `artifacts\HamMeter-Setup-<version>.exe`, its signature (`.sig`), SHA-256 and `release-notes-<version>.md`.

### Releasing

1. Add the patch notes to `CHANGELOG.md` (`## <version> - <yyyy-mm-dd>`, lines `- New:` / `- Improved:` / `- Fixed:`; add `[important]` to the heading for releases users should not skip). The build refuses a version without notes.
2. Run `.\build\build.ps1 -Version <version>`. It signs the setup with the release key.
3. Create a GitHub release with the tag `v<version>`, paste `release-notes-<version>.md` as the text, and upload `HamMeter-Setup-<version>.exe` and `HamMeter-Setup-<version>.exe.sig`. HamMeter only offers releases that have both files.

The release key is created once with `dotnet run --project build\ReleaseSigner -- create-key HamMeter.Aion2\Update\release-public-key.txt` and stored DPAPI-protected in `%APPDATA%\HamMeter-ReleaseKey` (this Windows account only). Losing it means installed copies can no longer update themselves, so keep a backup of the Windows account or plan a key rotation.

Updating the parser after a game patch (once Kuroukihime has fixed it):

```bash
git submodule update --remote external/AionDpsMeter
```

## Credits

Concept, design, and UX/UI by **NennMichSchinken**. The implementation was written with the help of AI (Claude) under my direction.

- Packet capture and parsing: **Kuroukihime** ([AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter)), with game data reverse engineering by **taengu**
- Heal / damage-taken packet rules: **taengu** ([A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter))
- Bar textures and styles: from **WispUI** (NennMichSchinken, MIT), the FFXIV suite HamMeter lives on in
- Icons: [Lucide](https://lucide.dev) (ISC, see `HamMeter.Aion2/Assets/Icons/LICENSE-lucide.txt`)
- Installer engine: [Inno Setup](https://jrsoftware.org/isinfo.php) by Jordan Russell and Martijn Laan

## License

GPL-3.0, see [LICENSE](LICENSE). (HamMeter for FFXIV stays MIT.)
