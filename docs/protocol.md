# Aion 2 network protocol (HamMeter notes)

This is HamMeter's own description of the Aion 2 game traffic, written as the basis
for an independent capture and parser (see [independence plan](#independence-plan)).
It records **facts about the protocol** — ports, framing, opcodes, field order — in our
own words. Code is written from this document, not from other meters' sources.

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
totals, opcode counts) and compare against what happened in game. With *Settings →
Use HamMeter's own packet reader (beta)* the same per-skill totals go to `HamMeter.log`
after every fight, for a direct comparison with the in-game meter.

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

**Open:** possibly not every damage tick of a player skill should count — the classic
reader only counts a curated list of DoT skill codes. HamMeter's own reader counts every
player damage tick for now and logs ticks per skill (`tick`), so the totals can be
compared with the in-game meter; skills that turn out wrong go on an exclusion list (§8).

### 5.3 `04 8D` — death

**Status: confirmed**

| Field | Type |
|---|---|
| length, opcode | |
| entity | varint |

Sent for any actor. For monsters it arrives with the killing blow (0–0.5 s after the
last hit for all 17 mobs in two recordings of 2026-09-24), so it is a reliable "enemy
defeated" signal: HamMeter pauses the fight clock once every engaged enemy is dead and
counts player deaths.

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

**Status: observed**

| Field | Type |
|---|---|
| length, opcode | |
| unknown | 2 bytes |
| entity | varint |
| unknown | 4 bytes |
| character id | u32 LE |

### 6.4 `02 97` — party

**Status: observed** — structured, the best-understood entity packet.

Header:

| Field | Type |
|---|---|
| length, opcode | |
| party key | u32 LE |
| party name | u8 length + UTF-8 |
| party size | u8 |
| dungeon id | u32 LE |
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
healing belong to the owner.

Proper decoding of this packet is a main task of the independence work.

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
| Monster names, bosses | monster list of A2Tools-DPS-Meter (taengu, GPL-3.0), `Game/Data/` | done |
| Bosses not in the list | HamMeter rule on monster HP (needs a boss recording) | todo |
| Class icons | own artwork | todo |

Static game data (skill and monster names, icons) belongs to NCSoft. The monster list is
the one both other meters use (A2Tools' is the more complete one); it is taken under its
GPL-3.0 license with credit, as a fixed copy — HamMeter does not depend on it at runtime
from anywhere else. Everything else HamMeter builds from its own recordings.

**Fights and bosses:** a boss (monster list) always gets its own fight: the first hit on
it closes a running trash fight, and the fight ends when the last boss dies (§5.3).
Confirmed mob codes from recordings: 2100456 Red Cap Fungen, 2100041 Red Cap Fungie,
2700914 Toblini (boss, sealed dungeon; the fight ended with its death), 2700915 Hideout
Sura (its adds).

**Players nearby:** once the user is known (§6.10), only the user starts and keeps fights
going. Other players count only on enemies the user fights too — the same idea as the
other meters' "target" views — so strangers in the open world stay out of the list.

---

## Independence plan

1. ✅ Inventory of what HamMeter uses from the AionDpsMeter submodule.
2. ✅ This protocol description.
3. ☐ Verify the *open* and *heuristic* points with own recordings.
4. ✅ Own pipeline: capture (raw socket, Npcap incl. loopback), reassembly, framing,
   LZ4, dispatch — `Capture/`, `Protocol/`. Selectable as a beta; classic stays default.
5. ◐ Own entity packets and entity registry — own character, other players and summon
   owners done (`Game/`); entity link (§6.3) and party (§6.4) need a group to test.
6. ◐ Own game data (§8).
7. ☐ Compare with the in-game meter (solo: damage, self-heals) and on recordings.
8. ☐ Make the own reader the default; remove `Classic/`, the submodule, EF Core/SQLite
   and the `CombatTap` reflection hook.
