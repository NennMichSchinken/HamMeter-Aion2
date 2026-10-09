# Aion 2 network protocol (HamMeter notes)

This is HamMeter's description of the Aion 2 game traffic: **facts about the
protocol** — ports, framing, opcodes, field order — and how HamMeter uses them. HamMeter's
capture and parsers are written from this document.

Every section carries a status:

| Status | Meaning |
|---|---|
| **confirmed** | HamMeter relies on it today and it produces correct numbers in game. |
| **observed** | Known behaviour of the game, not yet checked against our own recordings. |
| **heuristic** | Not a real understanding of the structure; found by scanning for patterns. Needs proper decoding. |
| **open** | Contradictions or unknowns that must be settled with recordings. |

Verification means: record a session with *Settings → Record packets* (encrypted
`.hmrec` files in `%APPDATA%\HamMeter-Aion2\PacketLogs`), replay it with
`HamMeter.exe --replay <file.hmrec>` (writes `<file>.report.txt`: every fight, per-skill
totals, party lists, opcode counts) and compare against what happened in game. The same
per-skill totals go to `HamMeter.log` after every fight, for a direct comparison with the
in-game meter.

---

## 1. Transport

**Status: confirmed**

- The client talks to the game server over **one long-lived IPv4 TCP connection**.
  A meter only needs the **server → client** direction.
- The connection is owned by `Aion2.exe`, so it can be found in the Windows TCP table
  (`GetExtendedTcpTable`, owner PID) without looking at any payload. HamMeter only
  accepts packets whose (remote ip:port → local ip:port) matches such a connection.
- The server sends a **keep-alive** packet regularly. Its payload starts with the bytes
  `0E 00 36` (a complete 11-byte packet, opcode `00 36`, see §2). Seeing it several
  times (HamMeter uses 5) on a connection proves it is the game stream.
- **VPN / ping-booster tools** (ExitLag and similar) tunnel the game through a local
  proxy: `Aion2.exe` then connects to `127.0.0.1`, and the real traffic only exists on
  the loopback interface. Raw sockets cannot see loopback traffic; Npcap can
  (`\Device\NPF_Loopback`). **Open:** HamMeter's raw-socket path skips loopback
  connections today, so booster users need Npcap.

### TCP reassembly

- Segments are appended in sequence-number order per connection.
- Duplicates / retransmissions (segment entirely before the expected sequence) are
  dropped; sequence arithmetic must be modulo 2³².
- On a gap (segment after the expected sequence) the missing bytes are lost; the
  stream continues from the new position and the framer re-synchronises (§2).
- `FIN` / `RST` end the stream; its buffers are dropped.

---

## 2. Framing

**Status: confirmed**

The TCP stream is a sequence of packets. Every packet starts with:

| Field | Type | Notes |
|---|---|---|
| length | varint | see below |
| opcode | 2 bytes | written here as the two bytes on the wire, e.g. `04 38` |
| body | … | opcode specific |

**Varint:** little-endian base-128 — 7 value bits per byte, low bits first, high bit
set means "another byte follows". At most 5 bytes for 32-bit values.

**Length quirk:** the total packet size in bytes (length field included) is

```
size = lengthValue + lengthFieldBytes − 4
```

Example: the keep-alive starts with `0E` → 14 + 1 − 4 = **11 bytes**.

**Opcode as a number:** when an opcode is stored as a `ushort`, it is the two bytes read
little-endian, so `04 38` is `0x3804`. This document always uses the wire order.

### Synchronisation

A capture usually starts in the middle of the stream, and a TCP gap destroys the
packet boundary. A robust framer therefore:

1. starts **unsynchronised** and discards bytes until the keep-alive pattern
   `0E 00 36` appears (keep the last 2 bytes when not found, the pattern may span two
   segments);
2. from there reads packet after packet using the length rule;
3. treats a size ≤ 0 or unreasonably large (HamMeter-to-be: > 40 KB) as a desync:
   drop one byte and go back to step 1.

Keep-alive packets carry no data for a meter and can be dropped after framing.

---

## 3. Compressed bundles (`FF FF`)

**Status: confirmed**

Many packets are bundles: opcode `FF FF` followed by an LZ4-compressed block of more
packets.

Layout of a bundle frame:

| Field | Type | Notes |
|---|---|---|
| length | varint | as in §2 |
| flag | 0 or 1 byte | **open:** a single byte `F0`–`FE` sometimes sits before `FF FF`; skip it. Meaning unknown. |
| marker | `FF FF` | |
| uncompressed size | u32 LE | sanity range 1 … 10,000,000 |
| data | LZ4 **raw block** | not the LZ4 frame format; fills the rest of the frame |

The decompressed data is again a sequence of packets in the §2 format with two
differences:

- single `00` bytes between packets are padding and are skipped;
- an inner packet can itself be a `FF FF` bundle → decompress recursively.

Every non-bundle packet found this way is dispatched by its opcode.

---

## 4. Identities

**Status: confirmed (model) / open (edge cases)**

The game uses two kinds of ids for players:

- **Entity id** (a.k.a. session id) — a varint that identifies any actor (player, mob,
  summon) *in the current zone/instance*. Combat packets only carry entity ids. Ids are
  reused: after a zone change the same number can belong to someone else.
- **Character id** (a.k.a. global id) — stable per character. It appears as the low
  32 bits of a 64-bit *database id* (see party, §6.4); the top 16 bits of the database
  id are the server id.

Name resolution:

1. `33 36` / `45 36` give *entity id → name* directly (§6.1, §6.2).
2. `02 97` gives *character id → name, level, combat power* for the party (§6.4).
3. `20 36` links *entity id ↔ character id* (§6.3), so party data reaches the entity.
4. **Observed:** link packets are sometimes missing; matching an unlinked entity to a
   party member by identical name is a workable fallback.
5. If an entity id gets a *different* confirmed name than before, the id was reused:
   forget everything attached to the old one.

Players seen only through combat (no name yet) are shown as a placeholder until a name
arrives.

---

## 5. Combat packets

### 5.1 `04 38` — direct hit or direct heal

**Status: confirmed** (HamMeter's `CombatPacketParser`)

| # | Field | Type | Notes |
|---|---|---|---|
| 1 | length, opcode | | §2 |
| 2 | target | varint | entity id |
| 3 | layout switch | varint | valid only if ≤ 255 and the low nibble is 4–7; otherwise drop the packet |
| 4 | unknown | varint | |
| 5 | actor | varint | entity id of the caster |
| 6 | skill code | u32 LE | §7 |
| 7 | unknown | u8 | |
| 8 | hit type | varint | `3` = critical |
| 9 | detail block | 8 or 11 bytes | see below |
| 10 | unknown | varint | scales with the actor's power |
| 11 | amount | varint | damage or heal amount |

Detail block:

- layout switch low nibble **≠ 4**: `u8 hit flags`, `u8 unknown`, `u8 direction`,
  then 8 bytes.
- low nibble **= 4**: only the 8 bytes.

Hit flags (**observed**): bit 0 back attack, bit 1 parry, bit 2 perfect, bit 3 double
damage. Direction (**observed**): bit 0 back, bit 1 front.

Meaning:

- actor = target → self-cast (self-heal, or ignore for damage).
- skill code is a healing skill (§7) → heal from actor to target.
- actor resolves to a player (§7) → damage done by that player.
- actor is not a player but the target is → damage taken by the target.

### 5.2 `05 38` — periodic tick (DoT / HoT)

**Status: confirmed**

| # | Field | Type | Notes |
|---|---|---|---|
| 1 | length, opcode | | |
| 2 | target | varint | |
| 3 | effect type | u8 | see below — compare exact values, not bits |
| 4 | actor | varint | |
| 5 | unknown | varint | |
| 6 | skill code × 100 | u32 LE | divide by 100 to get the skill code |
| 7 | amount | varint | |

Effect types: damage `02`, `0A`; heal `01`, `09`, `0B`. Other values are not HP
changes. (`0B` has the `02` bit set, so a bit test would misread HoTs as damage.)

**Open:** possibly not every damage tick of a player skill counts in the in-game meter.
HamMeter counts every player damage tick (solo it matched 1:1 so far) and logs ticks per
skill (`tick`), so the totals can be compared with the in-game meter; skills that turn
out wrong go on an exclusion list (§8).

### 5.3 `04 8D` — death

**Status: confirmed**

| Field | Type | Notes |
|---|---|---|
| length, opcode | | |
| entity | varint | who died |
| unknown | u32 LE | |
| killer | varint | entity id of the player who landed the killing blow; `0` = none |
| killer's server | u16 LE | the server id of §6.4, e.g. `0C 09`; `0` without a killer |
| killer name | u8 length + UTF-8 | the killer's character name; empty without a killer |
| padding | zero bytes | |

All 233 deaths of two recordings (2026-10-07/08) read this way. Earlier HamMeter read the
two middle fields as varints with a `01` flag; that only fit by chance and doubled names
("HeranorHeranor") when the server id's second byte was taken as the name's length.

The killer block gives the user's name at the first kill, without waiting for a zone
change (`33 36`).

Sent for any actor. For monsters it arrives with the killing blow (0–0.5 s after the
last hit for all 17 mobs in two recordings of 2026-09-24), so it is a reliable "enemy
defeated" signal: HamMeter pauses the fight clock once every engaged enemy is dead and
counts the deaths of the user and the party. Every other death counts as an enemy's,
whatever HamMeter took the entity for, so a misread entity cannot keep the clock running.

### 5.4 `00 8D` — remaining HP

**Status: observed** (not used by HamMeter)

After length and opcode: entity varint, three unknown varints, current HP as u64 LE.

---

## 6. Entity packets

### 6.1 `33 36` — own character

**Status: observed / heuristic**

| Field | Type | Notes |
|---|---|---|
| length, opcode | | |
| entity | varint | the player's own entity id |
| name block | | §6.5 |
| server id | u16 LE | directly after the name |
| class | u8 | directly after the server id |

Combat power: **heuristic** — near the end of the packet there are two u64 LE values
(current and highest combat power, current ≤ highest, both in a plausible range).
Proper field position unknown.

This is the packet that says "this entity is **you**".

### 6.2 `45 36` — other player appears

**Status: observed / heuristic**

Same start as `33 36` (entity varint, name block). After the name a varint class
follows. The server id is **heuristic**: the first u16 after that which is a known
server id. Needs proper decoding.

### 6.3 `20 36` — entity ↔ character link

**Status: observed** — used by HamMeter to match entities to party members (§6.4);
needs a group recording to confirm. **Not seen once** in a group dungeon recording of
2026-10-07 (recording started inside the dungeon, then a zone change to the open world),
so party members are mostly matched by name.

| Field | Type |
|---|---|
| length, opcode | |
| unknown | 2 bytes |
| entity | varint |
| unknown | 4 bytes |
| character id | u32 LE |

### 6.4 `02 97` — party

**Status: observed** — structured, the best-understood entity packet. HamMeter reads it
(`Game/PartyPacket.cs`): party members count like the user (they start fights, their
damage taken and heals count), matched to entities by the link (§6.3), by name, or by
the database id in their appearance (§6.11).

Header:

| Field | Type |
|---|---|
| length, opcode | |
| party key | u32 LE |
| party name | u8 length + UTF-8 |
| party size | u8 |
| dungeon id | u32 LE — **open:** whether it names the dungeon the party is in (the monster list's dungeon ids, e.g. 600002); the replay report prints it |
| unknown | u8, u8 |
| leader database id | u64 LE |
| unknown | 1 bit (§6.6) |
| unknown | u8, u8 |
| member count | varint |

Per member:

| Field | Type | Present |
|---|---|---|
| presence mask | u8 | always; `0` = empty slot |
| slot number | u8 | always (1-based) |
| database id | u64 LE | always — low 32 bits character id, top 16 bits server id |
| name | u8 length + UTF-8 | always |
| unknown | u32 LE | always |
| level | u32 LE | always |
| conqueror level | u32 LE | mask & `01` |
| equipment item level | u32 LE | always |
| ready | 1 bit | mask & `02` |
| online | 1 bit | always |
| origin server id | u16 LE | mask & `04` |
| current server id | u16 LE | mask & `08` |
| party role | u8 | always |
| combat power | u64 LE | always |
| ticket count | varint | always, followed by *count* × (u8, u32 LE) |
| unknown | u64 LE | mask & `10` |
| mentoring role | u8 | always |
| latency state | u8 | always |

**Open:** how the 1-bit fields are packed (§6.6).

### 6.5 Name block (used by `33 36` and `45 36`)

**Status: observed / open**

- 4 bytes unknown, then a flag byte. Only if bit 0 of the flag is set a name follows:
- varint byte length (plausible range 1–72), then the name bytes.
- **Open:** names are not always plain UTF-8. Bytes below `0x20` appear inside names
  and seem to repeat earlier output bytes (a small back-reference scheme). Decode rules
  must be confirmed with recordings of names containing repeated characters and
  non-Latin scripts. Names that are all digits are not real names.

### 6.6 Bit fields

**Status: open**

Some structures contain single-bit fields. A byte is loaded when the first bit is read
and bits are taken from its lowest bit upwards. Whether following bit fields — after
byte-sized fields in between, or in the next party member — continue in the same byte
or start a new byte is **not settled**. This decides whether every party member after
the first is parsed correctly; check with a recording of a full party.

Until then HamMeter reads `02 97` with three candidate packings and keeps the one that
reads cleanly to the last byte (every member with a valid name, slot and level):

- **Shared** — all bit fields of the packet share one byte until its 8 bits are used;
- **PerRun** — consecutive bit fields share a byte, any other field ends it;
- **PerBit** — every bit field is a byte of its own.

`HamMeter.log` ("Party: … (bit packing …)") and the replay report ("Party lists") name
the packing that matched, or print the packet when none did.

### 6.7 `41 36` — spawn (mobs and summons)

**Status: heuristic** — the weakest part of the protocol knowledge.

After length and opcode: the spawned entity id as varint. The rest is not decoded
structurally; today's knowledge is pattern based:

- **Mob code** (which kind of monster): a 3-byte little-endian number shortly after
  the entity id, located via a nearby byte pattern.
- **Max HP:** a varint after a `01` byte following the mob code.
- **Summon owner:** after a run of eight `FF` bytes follows a short header
  (`07 02 06`, `07 02 01`, or just `07 02`); the owner's entity id sits 3 bytes after
  the header start as u16 LE.

Summons (pets, spirits, totems) deal damage with their own entity id; their damage and
healing belong to the owner. **Observed:** a summon can have a summon as its owner
(dungeon, 2026-10-07: shown as a grey `Player_<id>` outside the party); HamMeter follows
the chain up to the player.

A spawn with a mob code from the monster list marks the entity as a monster: it is never
taken for a player afterwards (ids are reused, and a monster may use a skill code that
looks like a player's), unless it is the user or a party member.

**Observed:** the spawn of a player's summon (a 168-byte `41 36`) carries the owner's
character name as u8 length + UTF-8 at byte 13 — a possible, more reliable way to find
the owner than the pattern above.

Proper decoding of this packet is one of the main open points.

### 6.8 `03 36` — server time

**Status: observed**

At byte offset 5 an i64 LE: milliseconds since 0001-01-01 (the .NET epoch).
Subtract `62,135,596,800,000` to get Unix milliseconds. Comparing with the arrival time
gives an estimate of the latency.

### 6.10 `4A 36` — user state

**Status: observed** (3 recordings, 2 characters)

The first body field (varint) is always the **user's own entity id**, never anyone
else's. Unlike `33 36` it also arrives without a zone change, during normal play, so it
identifies the user when HamMeter starts in the middle of a session. The rest of the
packet is not decoded. Other packets seen only for the user (so far): `03 8D`, `41 37`,
`42 37`, `46 37`, `46 36`.

Entity ids seem stable longer than a zone: the same character kept id 3580 over several
hours and zone changes.

### 6.11 `1C 92` — party member

**Status: observed** (3 recordings, open world and dungeon, 2026-10-07/08)

One packet per party member other than the user, about every 2 seconds, also for members
far away. HamMeter reads the start (`EntityPacketParser.OnPartyMember`):

| Field | Type |
|---|---|
| length, opcode | |
| database id | u64 LE — low 32 bits character id, top 16 bits server id (as in §6.4) |
| unknown | u32 LE |
| zone | u32 LE — inside a dungeon its dungeon id from the monster list (600002), in the open world e.g. 1110 |
| unknown | 8 bytes |
| position | 3 × f32 LE (x, y, z) — the same values as in the member's `45 36` and latest `1A 37` / `1B 37` (§6.12) |
| … | more, then the legion name (u8 length + UTF-8) |

It has no name and no entity id. **Observed:** a member in view sometimes gets none for a
while (open world, 25 s), in the dungeon they came the whole time.

Unlike `02 97`, it also comes when HamMeter starts after the party was formed: in the open
world (2026-10-08) not a single `02 97` arrived in 7 minutes. HamMeter takes every member
it sees as part of the party; a `02 97` drops those not on its list.

**Link to the entity**, two ways:

- The appearance of a player (`45 36`) carries the player's database id further back in
  the packet (byte ~1100 of ~1500; the layout around it varies). HamMeter looks for the
  8 bytes of every known member there and links the entity to that character.
- A member already in view when HamMeter starts has no `45 36` any more. Its `1C 92`
  position equals the latest `1A 37` / `1B 37` position of its entity (dungeon,
  2026-10-07: all three such members found within 25 s of the start). HamMeter links the
  closest entity within 3 units that is not the user, a summon, a monster or linked
  already.

### 6.12 `1A 37` / `1B 37` — position

**Status: observed** (about 3,000 packets of one recording, players and monsters)

| Field | Type |
|---|---|
| length, opcode | |
| entity | varint |
| `1A 37`: unknown | 2 bytes (always `00 00` so far) |
| `1B 37`: flags | u8 — when odd, one more unknown byte follows |
| position | 3 × f32 LE (x, y, z) |
| … | 2 to 4 more bytes |

`1C 37` and `1D 37` start with the entity too and look like movement as well; HamMeter
does not read them.

### 6.13 `01 40` — zone entered

**Status: observed** (10 recordings, 2026-10-07/08)

| Field | Type |
|---|---|
| length, opcode | |
| zone | u32 LE — the open world 1110, a dungeon its id from the monster list (600011), other instances other numbers (50, 510034, 320036, …) |
| … | 11 or 20 more bytes |

Arrives with every zone change of the user, entering and leaving, and now and then again
inside the same zone (2026-10-07: twice in the middle of a run). It comes in the same
second as the `33 36` of the change.

### 6.14 `00 61` — dungeon state

**Status: observed** (the same recordings)

| Field | Type |
|---|---|
| length, opcode | |
| zone | u32 LE (as in §6.13) |
| state | u8 |
| time | u64 LE, Unix milliseconds |
| time | u64 LE, Unix milliseconds, `0` in state 1 |

States: `1` on entering (2026-10-08 22:26:54), `2` once the run is under way, with the
second time exactly 1 hour after the first (the time limit), `3` the moment the last boss
dies (Divine Auldor, 22:34:45; Ultimate Berk, 2026-10-07), with the second time 10 minutes
after the first, `4` right after `3` in some instances.

**New run (HamMeter, `Game/DungeonRuns.cs`):** entering a zone that is a dungeon of the
monster list starts a new run, unless it is the dungeon of the last run, that run was not
cleared (state 3) and it started less than an hour ago: that is porting out and back in.
Leaving never starts anything. With the setting "Reset when a dungeon run starts" (on by
default) the meter and its history are cleared. **Open:** whether a re-entry into a run
sends state 1 again or a new first time; that would tell runs apart without the rule.

### 6.9 Not used by HamMeter

`2A 38`, `2B 38` buffs/effects; `49 36` character stats. Listed so they are recognised
in recordings.

---

## 7. Skill codes

**Status: confirmed**

A skill code is a decimal number whose digits carry meaning:

| Range | Meaning |
|---|---|
| 100,000 – 199,999 | pet / summon skill |
| 1,000,000 – 9,999,999 | NPC skill — the actor is **not** a player … |
| 3,000,000 – 3,099,999 | … except this range: **Theostone** (usable by every class; the class must come from the actor) |
| 11,000,000 – 19,999,999 | player class skill |
| > 299,999,999 or < 1 | invalid |

Player skill variants add small offsets to a base code (0, 10, 20, 30, 40, 50, 120,
130, 140, 150, 230, 240, 250, 340, 350, 450).

**Class from skill code:** the first two digits of a player skill code are the class:

| Id | Class |
|---|---|
| 10 | Elementalist spirit (summon) |
| 11 | Gladiator |
| 12 | Templar |
| 13 | Assassin |
| 14 | Ranger |
| 15 | Sorcerer |
| 16 | Elementalist |
| 17 | Cleric |
| 18 | Chanter |
| 19 | Brawler |

Watch out: a 7-digit NPC skill like `12xxxxx` also starts with `12`, so the range check
must come before the class lookup.

**Healing skills:** direct heals (`04 38`) cannot be told apart from damage by layout;
they are recognised by skill code. HamMeter matches a *skill family* — the code with its
last four digits cleared — so every variant of a heal counts. **Open:** both the family
rule and HamMeter's starting list of heal families must be confirmed with recordings of
every healing class (§8). The skill log marks self-casts that are not a known heal as
`self?`, so missing heals show up.

---

## 8. Game data HamMeter needs

| Data | Source for HamMeter | Status |
|---|---|---|
| Class per skill code | rule in §7 | done |
| Theostone range | rule in §7 | done |
| Healing skill codes | starting list in `Game/SkillRules.cs`; confirm with own recordings | in progress |
| DoT skill codes | every player tick counts; exclusions from comparisons with the in-game meter | in progress |
| Monster names, bosses | monster list in `Game/Data/` (see `SOURCE.md` there) | done |
| Bosses not in the list | HamMeter rule on monster HP (needs a boss recording) | todo |
| Class icons | official AION 2 class icons (NCSoft) | done |

Static game data (skill and monster names, icons) belongs to NCSoft. The monster list is
a fixed copy inside HamMeter; nothing is loaded at runtime. Everything else HamMeter
builds from its own recordings.

**Fights (like combat in WoW):** a fight lasts while enemies are engaged; its clock stops when the last one dies (`04 8D`) and the fight ends a few seconds later (setting, default 5 s) unless the next pull comes first. A dead enemy stays dead: damage-over-time ticks on it or from it still count but never re-engage it and never start a fight of their own — re-engaging let the clock run through the walking between packs (recording of 2026-09-24, 23:05), and a tick after a boss's death started a new 30-second fight. Only a direct hit more than 10 s after the death re-engages it (a new monster on the same id). A player skill on the user or a party member is never damage (no friendly fire): it is a heal or buff HamMeter does not know yet, listed as `ally?` in the skill log. Without any death a fight ends after 30 s without damage.

**Bosses:** a boss (monster list) always gets its own fight: the first hit on
it closes a running trash fight, and the fight ends when the last boss dies (§5.3).
Confirmed mob codes from recordings: 2100456 Red Cap Fungen, 2100041 Red Cap Fungie,
2700914 Toblini (boss, sealed dungeon; the fight ended with its death), 2700915 Hideout
Sura (its adds).

**Enemies that never die:** some adds send no death (`Condensed Krao` explodes, recording
of 2026-10-07) and kept a fight going through the walk to the next pack. An enemy nobody
hit and that hit nobody for 10 s leaves the fight as if it died; bosses stay until they die.

**Party and players nearby:** once the user is known (§6.10), only the user and the party
(§6.4, §6.11) are listed. Other players never count, not even on enemies the user or the
party fight too (strangers in the open world filled the list that way); a stranger's heal
on one of ours still counts as healing taken. Until the user is known the combat packets
wait (the user became known 7 to 16 s after the start in the open world, 2026-10-08) and
are then parsed with their own time; only the last 30 s are kept. They never count
without the user: idle at the start, no `4A 36` came for minutes (2026-10-08, 21:55).
Names come with the packet of a player appearing (`45 36`) or a killing blow (§5.3); when
HamMeter starts inside a dungeon the appearance packets have passed. So while party
members in a dungeon (zone of §6.11) are unaccounted for (no link, no name), a player
without a name may be one of them and counts on the fight's enemies, without starting a
fight of its own. Not in the open world: members are often far away there while strangers
without a name are around.

---

## Open points

0. A recording that ports out of a dungeon run and back in, to check the rule of §6.14
   against what `00 61` sends on the re-entry. The replay report lists zone changes, the
   first monster of each dungeon and every packet carrying a known dungeon id.

1. Group test on Global: party list packing (§6.6), entity link (§6.3), heals on others,
   damage taken by the tank.
2. Bosses that are not in the monster list: a rule on monster HP (§8).
3. The *heuristic* parts: spawn packet (§6.7), other players' server id (§6.2), name
   back-references (§6.5).
