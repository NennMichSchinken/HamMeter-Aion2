# Changelog

Format (read by HamMeter's "What's new" and the update popup, keep it this way):

- `## <version> - <yyyy-mm-dd>` starts a release; add ` [important]` for releases users
  should not skip (e.g. a parser fix after an Aion 2 patch).
- Each line is `- New: ...`, `- Improved: ...` or `- Fixed: ...`.

## 0.3.0 - 2026-10-07
- New: HamMeter reads the game with its own packet reader.
- New: Group play: your party counts on every enemy of the group, with the tank's damage taken and heals between party members.
- New: Fights like combat in WoW: every pack is its own fight and walking between packs no longer lowers your DPS.
- New: Boss fights get a fight of their own, end with the boss's death and carry a crown in the history.
- New: Fights are named after the boss or the main target.
- New: Works with VPN and ping boosters such as LagoFast (needs Npcap).
- New: Boss fights show the boss's name with a crown at the top of the meter instead of the metric.
- Improved: Your character is recognised right away, with your name after the first kill.
- Improved: Only you and your party are listed; players nearby no longer show up in the open world.
- Improved: Official class icons, with bar colours that match them.
- Fixed: The Cleric's self-heal while attacking counts as healing.
- Fixed: Monsters, bosses and summons of summons no longer show up as grey "Player" entries in dungeons.
- Fixed: Fights no longer run on for 30 seconds after the last enemy died because of a late damage-over-time tick, an unknown heal on a party member or a misread monster.
- Fixed: Adds that explode or vanish without dying no longer keep a fight going through the walk to the next pack.
- Fixed: When HamMeter starts inside a dungeon, party members it has no name for yet still count.

## 0.2.0 - 2026-09-24
- New: HamMeter checks for updates when it starts (can be turned off) and offers a signed one-click update.
- New: Version pill and "What's new" in the settings window.
- New: Lock in the meter's bottom-right corner, shown on hover. Replaces the "Lock position" setting.
- Improved: Resize corner drawn like in WispUI.

## 0.1.4 - 2026-09-24
- New: Nine bar textures from WispUI, selectable under Bars.
- Improved: The texture list opens upward when there is no room below.
- Fixed: Bars are rounded at both ends again.

## 0.1.3 - 2026-09-24
- New: Tray icon in the notification area instead of a taskbar button.
- New: One-click update path in the setup; HamMeter starts again afterwards.

## 0.1.1 - 2026-09-24
- Fixed: Settings window no longer covers the meter or gets cut off at the screen edges.

## 0.1.0 - 2026-09-24
- New: First release: damage, healing, damage taken and deaths for the whole party.
- New: Setup in the HamMeter design with guided Npcap install.
