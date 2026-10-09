# Monster data

`npcs.json` — monster names (English), boss flags and dungeon info by mob code.

- Source: [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter),
  `src/data/i18n/npcs/en.json`, commit `4d99ab70b03e0e18c6e00e8e7b092ecee9093248`
  (2026-09-16), content unchanged. Renamed because a `.en.` file name is built as a
  satellite (language) resource.
- License: GPL-3.0 (same as HamMeter).
- The names themselves are game data of AION 2 (NCSoft).

HamMeter's own rules (docs/protocol.md §8) sit on top of it for monsters that are not
listed yet.

To update: download the file again from the repository above, replace `npcs.json` and
put the new commit here.

# Skill names

`skills-de.json`, `skills-en.json` — skill names (German, English) by skill code, shown in
the skill details.

- Source: [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter),
  `src/data/i18n/skills/de.json` and `en.json`, commit
  `82e53c1008ac4c2974446cc703703f473bbbed81` (2026-10-08), content unchanged. Renamed for
  the same reason as above.
- License: GPL-3.0 (same as HamMeter).
- The names themselves are game data of AION 2 (NCSoft). The codes are the ones HamMeter
  reads from the hit packets (docs/protocol.md §7).
