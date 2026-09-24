# HamMeter for Aion 2

A minimalist, easy-to-read damage meter for **Aion 2**, with the same look and settings as [HamMeter for FFXIV](https://github.com/NennMichSchinken/HamMeter). It runs as a transparent overlay on top of the game.

> **Status: early / experimental.** HamMeter has its own packet reader now. Damage matches the in-game meter 1:1 in our tests, healing and deaths are read as well. Party features (names of group members) still need testing in a group.

## How it works

HamMeter reads the game's network packets passively (via Npcap, or Windows raw sockets when Npcap is not installed). It does not inject code, read or modify game memory, or send anything anywhere.

- **Protocol** (`docs/protocol.md`): HamMeter's own description of the Aion 2 traffic — framing, compressed bundles, combat and entity packets — with the status of every part (confirmed, observed, open). All reading code is written from it.
- **Capture** (`HamMeter.Aion2/Capture`): finds `Aion2.exe`'s own connections and reads only those, through Npcap (also with VPN / ping boosters; tested with LagoFast) or raw sockets.
- **Packets** (`HamMeter.Aion2/Protocol`): cuts the stream into game packets, unpacks LZ4 bundles and routes packets by opcode.
- **Game** (`HamMeter.Aion2/Game`): who is who (your character, other players, summons and their owners), what skill codes mean (class, heals, damage over time) and the monster list for names and bosses.
- **Fights** (`HamMeter.Aion2/Combat`): damage done, damage taken, healing, deaths. Like combat in WoW, every pack is a fight of its own: the fight clock stops when the last enemy dies, so walking never lowers your DPS. Bosses always get a fight of their own.
- **The overlay** (`HamMeter.Aion2/UI`): a port of the FFXIV HamMeter's ImGui drawing code onto [ClickableTransparentOverlay](https://github.com/zaafar/ClickableTransparentOverlay).

**Transition:** until HamMeter's reader has been tested in groups, the reader HamMeter first shipped with — built on [Kuroukihime/AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter) (GPL-3.0, git submodule `external/AionDpsMeter`) — is still included and is the default. Switch with *Settings → Data & App → Use HamMeter's own packet reader*. The old reader, the submodule and everything taken from it will be removed once HamMeter's reader is the default.

## Principles

1. **Security first.** HamMeter touches as little data as possible, stores almost nothing, and never talks to the network.
2. **Lightweight.** No bloat: Windows built-ins over extra libraries, no feature without a reason.

### Security model

- **Minimal network access.** HamMeter opens no listening port. Its only outgoing connection is the update check: one HTTPS request to the GitHub API when it starts (can be turned off in the setup and in Settings → Data & App). Nothing is sent besides the request itself.
- **Signed updates.** "Update now" only runs a downloaded setup whose ECDSA-P256 signature matches the public key built into HamMeter (`HamMeter.Aion2/Update/release-public-key.txt`). The private key never leaves the author's PC, so a compromised GitHub account alone cannot push code to users. Downloads are HTTPS-only, GitHub hosts only, size-capped, and locked between verification and start.
- **Least privilege.** With Npcap installed, HamMeter runs as a normal user. Only without Npcap does it restart itself with administrator rights (UAC prompt), because Windows raw sockets require them.
- **Narrow capture.** A packet is only looked at if it belongs exactly to a TCP connection owned by `Aion2.exe`; everything else is dropped after the header checks. With Npcap, HamMeter's reader opens only the adapter that carries the game connection, not in promiscuous mode, with a kernel filter for exactly that connection. Raw sockets do not switch the network card to promiscuous mode either (`RCVALL_IPLEVEL`). All lengths are bounds-checked, fragments are dropped, and a connection is only used after Aion's keep-alive was seen on it.
- **Safe unpacking.** Compressed bundles are size- and depth-limited, so a malformed packet cannot exhaust memory.
- **Data minimisation.** No database: fight history lives in memory and is gone when HamMeter closes. Logs contain no packet contents. `config.json` holds only look-and-feel settings.
- **Encrypted recordings.** The optional packet recording (for checking numbers) is AES-256-GCM encrypted, with the key protected by Windows DPAPI for your Windows account only. It is off on every start and recordings are deleted after 7 days.
- **Memory-safe parsing.** All packet parsing is managed C# with bounds-checked readers.

## Requirements

- Windows 10/11 x64
- Aion 2 in **borderless windowed** mode (an overlay cannot draw over exclusive fullscreen)
- Optional but recommended: [Npcap](https://npcap.com/#download) with **"WinPcap API-compatible Mode"** checked. Without it HamMeter needs administrator rights and may need a Windows Firewall exception, and VPN / ping boosters are not supported.

## Features

- Metrics: Damage Done, Damage Taken, Healing Done, Healing Taken, Deaths
- Current fight / Overall / per-fight history, named after the boss or main target
- Boss fights are kept apart from the trash before and after them and marked with a crown in the history
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

(`--recurse-submodules` is only needed while the old reader is still included.)

Run a development build with HamMeter's own reader, without changing the setting:

```bash
dotnet run --project HamMeter.Aion2 -c Release -- --own-reader
```

Release build (needs [Inno Setup 6](https://jrsoftware.org/isdl.php): `winget install JRSoftware.InnoSetup`):

```powershell
.\build\build.ps1 -Version 0.1.0
```

This writes `artifacts\HamMeter-Setup-<version>.exe`, its signature (`.sig`), SHA-256 and `release-notes-<version>.md`.

### After a game patch

1. Turn on *Record packets*, play a few fights, quit HamMeter.
2. Replay the recording: `HamMeter.exe --replay <file.hmrec>` writes `<file>.report.txt` with every fight, per-skill totals and the opcodes seen.
3. Compare with the in-game meter, fix what changed and update `docs/protocol.md`.
4. New monsters: update `HamMeter.Aion2/Game/Data/npcs.json` as described in `Game/Data/SOURCE.md`.

### Releasing

1. Add the patch notes to `CHANGELOG.md` (`## <version> - <yyyy-mm-dd>`, lines `- New:` / `- Improved:` / `- Fixed:`; add `[important]` to the heading for releases users should not skip). The build refuses a version without notes.
2. Run `.\build\build.ps1 -Version <version>`. It signs the setup with the release key.
3. Create a GitHub release with the tag `v<version>`, paste `release-notes-<version>.md` as the text, and upload `HamMeter-Setup-<version>.exe` and `HamMeter-Setup-<version>.exe.sig`. HamMeter only offers releases that have both files.

The release key is created once with `dotnet run --project build\ReleaseSigner -- create-key HamMeter.Aion2\Update\release-public-key.txt` and stored DPAPI-protected in `%APPDATA%\HamMeter-ReleaseKey` (this Windows account only). Losing it means installed copies can no longer update themselves, so keep a backup of the Windows account or plan a key rotation.

## Credits

Concept, design, and UX/UI by **NennMichSchinken**. The implementation was written with the help of AI (Claude) under my direction.

- Monster names, boss flags and dungeon data: **taengu** ([A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter), GPL-3.0), see `HamMeter.Aion2/Game/Data/SOURCE.md`
- Old reader (still included during the transition): **Kuroukihime** ([AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter), GPL-3.0)
- Class icons: official AION 2 artwork by **NCSoft**, see `HamMeter.Aion2/Assets/Classes/SOURCE.md`
- Bar textures and styles: from **WispUI** (NennMichSchinken, MIT), the FFXIV suite HamMeter lives on in
- Icons: [Lucide](https://lucide.dev) (ISC, see `HamMeter.Aion2/Assets/Icons/LICENSE-lucide.txt`)
- Packet capture library: [SharpPcap](https://github.com/dotpcap/sharppcap) (MIT); LZ4: [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) (MIT)
- Installer engine: [Inno Setup](https://jrsoftware.org/isinfo.php) by Jordan Russell and Martijn Laan

## License

GPL-3.0, see [LICENSE](LICENSE). (HamMeter for FFXIV stays MIT.)
