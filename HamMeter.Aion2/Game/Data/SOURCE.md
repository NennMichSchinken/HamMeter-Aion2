# Monster data

`npcs.json` — monster names (English), boss flags and dungeon info by mob code.

- Source: [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter),
  `src/data/i18n/npcs/en.json`, commit `4d99ab70b03e0e18c6e00e8e7b092ecee9093248`
  (2026-09-16), content unchanged. Renamed because a `.en.` file name is built as a
  satellite (language) resource.
- License: GPL-3.0 (same as HamMeter).
- The names themselves are game data of AION 2 (NCSoft).

Kuroukihime's AIon2-Dps-Meter ships the same list (a subset of it), so this is the list
both other meters rely on. HamMeter's own rules (docs/protocol.md §8) sit on top of it
for monsters that are not listed yet.

To update: download the file again from the repository above, replace `npcs.json` and
put the new commit here.
