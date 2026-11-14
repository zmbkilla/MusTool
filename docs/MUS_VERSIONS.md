# PSO2 .mus format versions

Findings from reading every adaptive song shipped in the base game's `data/win32` (JP client, Feb 2021), in
memory, with `MusTool scan` / `MusTool dump --ice`. 215 of the 216 names in `songs.txt` have an ice (the
other, `mu_11_bgm_defence_add`, is a CPK only). The NA Steam client's `win32` holds the same versions
(0x07: 23, 0x0A: 88, 0x0C: 70, others equal) and also round-trips completely.

The container is identical in every version: a NIFL file with REL0 / NOF0 / NEND chunks, little-endian,
pointers are u32 offsets from the REL0 start, every pointer slot is listed in NOF0, and empty lists point at
the shared `0xFFFFFFFF` sentinel at rel `0x10`. Only the root record and the clip-group subtree change.

## Where the files are

| What | Logical path | File in `win32` |
|---|---|---|
| song definition (`NAME.mus` inside an ice) | `sound/music/adaptive/NAME.ice` | `md5(logical path)` |
| clips (`*.hca`, CRI CPK) | `sound/music/adaptive/NAME.cpk` | `md5(logical path)` |

Example: `mu_11_bgm_boss_dfman` -> ice `e69f245db646d2a2930995705adcc87d`. The `.mus` inside the ice has a
0x60-byte ice entry header (its size is the u32 at entry+0x0C).

- Find the `.mus` inside the ice by its extension only. Its file name is not always the song name:
  `mu_11_bgm_mdfcl.ice` contains `11_bgm_mdfcl.mus`. The CPK is the song name with `.ice` -> `.cpk`.
- Songs can use clips from other songs' CPKs. `mu_11_bgm_defence_add.cpk` has no ice. It is a shared clip
  pack: defence_1 takes 2129 of its 4847 clips from it, and defence_mdn takes 2673 from defence_1 and 2048
  from defence_add. `MusTool clips <song> --ice --check` lists the CPKs a song needs, and `play` loads them
  automatically.

## Versions found

| Version | Songs | Root size | Clip-group layout | Status in MusTool |
|---|---|---|---|---|
| 0x07 | 24 | 0x08 | legacy, float volumes (A) | read/write, 24/24 byte-identical |
| 0x08 | 3  | 0x08 | legacy, byte volumes (B) | read/write, 3/3 byte-identical |
| 0x09 | 25 | 0x10 | legacy, byte volumes (B) | read/write, 25/25 byte-identical |
| 0x0A | 89 | 0x10 | flat | read/write, 89/89 byte-identical |
| 0x0B | 5  | 0x1C | flat | read/write, 5/5 byte-identical |
| 0x0C | 69 | 0x1C | flat | read/write, 69/69 byte-identical |
| 0x12 | NGS only (not in this data) | 0xB4 | flat + 16-byte extras | supported (from earlier samples) |

Songs per version are listed by `MusTool scan`. "Byte-identical" means read -> write and
read -> JSON -> write both reproduce the original file exactly (`scan` checks both).

## Shared records (all versions)

```
Part      (12) { Section* sections; Condition* conds; u8 sectionCount; u8 index; u8 clipCondCount; u8 subClipCondCount }
Condition (8)  { f32 offGain; char* name }                    (0x14 in v0x12; offGain is 0.0 in every file)
Section   (8)  { Phrase* phrases; u8 phraseCount; u8 type; u8 unk2; u8 unk3 }   (0x2C in v0x12)
Phrase    (8)  { Bar* bars; u32 barCount }
Bar       (8)  { ClipGroup* variations; u32 variationCount }
```

## Root record

Offsets are from the root. `count(+X)` means "the number of entries in the list at +X".

| Field | 0x07 / 0x08 | 0x09 | 0x0A | 0x0B / 0x0C |
|---|---|---|---|---|
| +0x00 | Part* parts | same | same | same |
| +0x04 | u8 version, u8 partCount | same | same | same |
| +0x06 | u8 flags, u8 (0x20, 0x34 ...) | u16 flags | u16 flags (0x000 - 0x2820; e.g. 0x2B4) | u16 = 0 |
| +0x08 | - | u32 (e.g. 30) | u8[4]; byte 3 = count(+0x0C) | u32 flags (what v0x0A keeps at +0x06, e.g. 0x2B4) |
| +0x0C | - | u32 = 0 | u32 0 **or** OneShot* list | u32 = 0 |
| +0x10 | - | - | - | u8[4]; byte 3 = count(+0x14) (the bytes v0x0A keeps at +0x08) |
| +0x14 | - | - | - | OneShot* list (sentinel when empty) |
| +0x18 | - | - | - | 0x0B: always sentinel. 0x0C: RootInfo* |

In v0x0A, +0x0C is a pointer only when NOF0 lists it. Of the 89 files, 41 store the plain value 0, 41 point at the empty
sentinel and 7 point at a real OneShot list.

```
OneShot  (0x10) { u32 kind (2, 3, 4); Condition* cond (empty sentinel = none, seen with kind 4);
                  f32 volume (100.0); char* hca }   e.g. "NOD_hit_BDF.hca", "PD2C_SE.hca"
RootInfo (8)    { u8 a (0/1); u8 b (0/1/2); u16 0; Named* named }   (v0x0C only)
Named    (8)    { char* name; void* list (always the empty sentinel) }
```

`Named.name` is either "" or a comma-separated list of other songs, e.g. boss_pd2:
"mu_11_bgm_sv2,mu_11_bgm_sv1,mu_11_bgm_flt_before,mu_11_bgm_boss_opr" (maybe songs that share its clips or
that it hands over to; not confirmed).

OneShot lists observed with 1-4 entries (boss_bdf_md, boss_mdfcl1, gmm, nodf, nonf, tkd, tkn, boss_bdfo,
boss_pd2, bst_01, neo). In v0x0C files, RootInfo and the OneShot list sit right before the root.

## Clip groups

### Flat layout (0x0A, 0x0B, 0x0C, 0x12)

```
ClipGroup (0x10) { Clip* clips; Clip* subClips; u16 tempo; u16 beats; u8 clipCount; u8 subClipCount;
                   u8 clipVolume (unkE); u8 subClipVolume (unkF) }   (+ u8[16] in v0x12)
Clip      (8)    { char* hca; u8 volume (0-100); u8 condition; u8 unk2 (1); u8 unk3 (0) }
```

Write order: a bar's clip lists come first, then its sub-clip lists, then its group array. When a bar has
several variations, the original tool writes *all* variations' clip lists, then *all* their sub-clip lists
(boss_mdfch2). The legacy layout follows the same rule with layers (defence_1_1).

### Legacy layout B (0x08, 0x09)

There is one more level of indirection: group -> layer -> track -> clip pointers -> clip -> name.

```
ClipGroup (0x0C) { Layer* clips; Layer* subClips; u16 tempo; u16 beats }
Layer     (8)    { Track* track; u8 volume (0-100); u8 kind (4); u8 (1); u8 (1) }
Track     (8)    { Clip** clips; u32 clipCount }
Clip      (8)    { char** names; u8 volume; u8 kind (0); u8 condition; u8 flags }
```

The meaning of these bytes comes from the game's loader (see "What the game does" below):

- `Layer.volume` scales every clip in that list. It is what the flat layout stores as `unkE`/`unkF`.
- `Layer.kind` must be 4, or the list has no clips.
- `Clip.kind` must be 0, or the clip is skipped.
- `Clip.flags` bit 0 set: `Clip.condition` is the condition index. Bit 0 clear: the condition is the clip's
  *position in its list* and the stored byte is ignored. 0x08/0x09 files always have bit 0 set. The byte is
  not a name count; the game only ever uses the first name.

Data is written bottom-up: names, Clip, (next clip ...), Clip* array, Track, Layer, then the same for
sub-clips, then ClipGroup.

Empty tracks (count 0) do not point at the sentinel. The n-th empty track in file order points to
`relEnd + base + 4*n`, where relEnd is the end of the REL0 chunk and base is a small per-file constant
(-16 to 0). The file reserves that space: between REL0 and NOF0 there is a block of zero bytes, about 4 per
empty track, that is not counted in the REL0 size. MusTool keeps `base` (`EmptyListBase`) and the block
(`Rel0Gap`) so files round-trip.

The game uses one track per layer and the first name of each clip. Bars have 1 variation (2 in some 0x09
songs).

### Legacy layout A (0x07)

Same tree as B, but volumes are floats and the records are wider:

```
ClipGroup (0x14) { Layer* clips; Layer* subClips; u32 tempo; u32 unk (0); u32 beats }
Layer     (0x0C) { Track* track; f32 volume (0.0-1.0); u8 kind (4); u8 (1); u8 (0/1); u8 0 }
Track     (8)    { Clip** clips; u32 clipCount }
Clip      (0x0C) { char** names; f32 volume (0.0-1.0); u8 kind (0); u8 condition; u8 flags (0/1); u8 0 }
```

The game converts float volumes to bytes as `(int)(v * 100)` (truncated, so 0.95 becomes 94). It reads tempo
as the low u16 at +0x08 and beats as the byte at +0x10, and ignores +0x0C.

The flags byte decides where the condition comes from, exactly as in 0x08/0x09:

- flags bit 0 set: the condition is the stored byte. boss_bdragon: line00-line04 -> `boss_11_0_normal` ...
  `boss_11_4_arm_belly`, and the second line00 -> `Hero`. Also boss_dragon, boss_spider, boss_wanigame, forest.
- flags bit 0 clear: the condition is the clip's position in its list. boss_machine: `""` (kind 0 but no file:
  skipped) -> `Always`, ground -> `boss_03_01`, deck -> `boss_03_02`, the second deck -> `Hero`. bb1/city/team
  have one clip per bar, so its condition is `Always`. The stored byte (position + 2 in these files) is not used.

Sub-clip layers without real clips hold a clip named "", which the game skips.

## What the game does (pso2.exe)

Read from the JP client in Ghidra (`unpacked_pso2.exe`). Function addresses are from that build.

**Version ranges.** The loader never checks exact versions, only ranges:

| Record | Ranges |
|---|---|
| clip group, clip | `< 8`, `8-9`, `>= 10` (`FUN_0056da70`, `FUN_0056cd70` / `FUN_0056ce90`) |
| root | `< 9`, `9-10`, `> 10` (`FUN_0056ded0`, `FUN_0056df40`) |
| anything `<= 6` | rejected |

MusTool reads the same way, so an unseen version such as 0x0D is read like 0x0C (`scan` and `info` mark it).

**Normalization.** Every version is turned into the flat form before playback:

- Group: `{ clips, subClips, u16 tempo, u8 beats, u8 clipCount, u8 subClipCount, u8 clipVolume, u8 subClipVolume }`.
- Clip: `{ name, u8 volume, u8 condition, ... }`.
- Root: a 0x20-byte record with the song **flags** as a u32 at +8. The flags are byte +6 of 0x07/0x08 roots,
  the u16 at +6 in 0x09/0x0A, and the u32 at +8 in 0x0B and later.

**Gain** (`FUN_00548310`, per clip):

```
gain = conditionGain * (listVolume / 100) * clipVolume / 100 * master
```

Clips below 1/65536 are not started. `conditionGain` is 1.0 when the condition is met, otherwise the
condition record's first field read as a float, which is 0.0 in every shipped file. `listVolume` is the
group's `unkE` (clips) or `unkF` (sub-clips), or the legacy layer volume. A clip whose condition index is past
its part's list is skipped.

**Conditions.** The game has a fixed list of built-in names, in this order:

| Index | Names |
|---|---|
| 0-4 | `Always`, `Normal`, `Hero`, `Pinch`, `Pse` |
| 5-10 | `First` ... `Sixth` |
| 11-14 | `RandomPhrase1-4` |
| 15-18 | `RandomMovement1-4` |
| 19-22 | `RandomPart1-4` |

Any other name (`aph_pinch`, `boss_11_1_arm` ...) is looked up in a table of states the game sets by name.

- **Random\*.** The engine keeps one selected condition entry per list (clips / sub-clips), per family
  (Phrase / Movement / Part) and per number 1-4. A clip with such a condition plays only if its condition index
  is the selected one. This is the "random group" rule below.
- **Battle states** (`FUN_0056d490`, called from `FUN_00548310` for every clip). One battle state, 0-5,
  decides `Normal`/`Hero`/`Pinch`/`Pse` and can silence a whole list. It gates every condition: other
  conditions also need their own state (custom names) or random pick, but they never play where the battle
  state says no. In the battle part (the part whose index byte is 1, or every part when flags bit 0x4 is set):

  | State | Main clips play | Sub-clips play |
  |---|---|---|
  | 0 Normal | `Normal` + non-battle conditions | same |
  | 1 Hero | only `Hero` | non-battle conditions |
  | 2 Pinch | nothing | only `Pinch` |
  | 3 Pse | non-battle conditions | only `Pse` |
  | 4 Hero+Pse | only `Hero` | only `Pse` |
  | 5 Pinch+Pse | nothing | `Pinch` or `Pse` |

  State 0 applies the same in every part. In states 1-5, parts other than the battle part play only
  non-battle conditions (`Normal`, `Hero`, `Pinch`, `Pse` all off).

  Before this, the song's flags adjust the state: 0x20 always state 0; 0x400 (no Hero) 4→3, 1→0;
  0x800 (no Pinch) 5→3, 2→0; 0x1000 (no Pse) 4→1, 5→2, 3→0.

- **Root conversion** (`FUN_0056ded0` below 9, `FUN_0056df40` for 9-10, used as-is above 10). Every root is
  turned into the 0x1C layout:

  | Normalized | v07/08 (8 bytes) | v09/0A (0x10) | v0B/0C (0x1C) |
  |---|---|---|---|
  | +0 parts, +4 version, +5 part count | same | same | same |
  | +8 flags (u32) | byte +6 | u16 +6 | u32 +8 |
  | +0x10 byte (default 30) | byte +7 | byte +8 | byte +0x10 |
  | +0x11, +0x12 bytes (default 30, 0) | 0 | 0 | +0x11, +0x12 |
  | +0x13 one-shot count, +0x14 one-shots | none | **zeroed** | +0x13, +0x14 |
  | +0x18 root info | none | none | +0x18 |

  So the game plays one-shots only from v0x0B on. The v0x0A list is kept for round-trips, but the game never
  reads it (`info` and the UI say so). The meaning of the +0x10..+0x12 bytes is still unknown.
- **Flag bits the game tests:** 0x4, 0x20, 0x400, 0x800 and 0x1000 (battle state, above), plus 0x8, 0x10,
  0x40, 0x80, 0x100, 0x200 and 0x4000, whose effects aren't traced yet.
- **Parts, sections, phrases and bars** have the same records in every version, with no version checks: part
  12 bytes (index byte +9, which the game clamps to 0-6); section 8 bytes (+4 phrase count, +5 type, +6 byte,
  +7 flags, with bits 0x1, 0x4, 0x10, 0x20, 0x40 and 0x80 tested); phrase and bar 8 bytes.
- **Rejected versions:** the loader (`FUN_0056e120`) accepts any version above 6.
- **NGS:** this exe (Feb 2021) predates NGS. It has no `MusicDiscTune` string, and a 0x12 file would go
  through the above-10 path and be misread. The 0x12 support in MusTool comes from the files alone.

- **First...Sixth** (`FUN_00565c60`, a method on the music object). The stage passes a number. The game
  then sets the states `First`...`Sixth` so that exactly the one at that index is on. They are alternatives
  (tkd: every bar has `TK_N_BP*_1`...`_6` and `TK_N_UP_1`...`_6`, one per number), not separate layers.
  MusTool shows them as one "Variant" selector (UI, `play --variant 1-6`, V key), defaulting to First. A sibling
  method (`0x00565bd0`) picks the section by type and position, so stage scripts likely set both together.

## How MusTool maps the versions

- `MusSerializer` keeps every version's records raw, so files round-trip byte-for-byte.
- `MusPlayback` turns them into what the game plays, with the same version ranges: `Group`, `Clip`, `Flags`,
  `OffGain`, and the battle-state rule `BattleAllows` / `EffectiveState`. The engine, `info --section`, `timing` and the UI all use it.
- Legacy groups keep their layers in `MusClipGroup.Layer` / `SubLayer` (raw bytes), and v0x07 clips keep
  their float volume in `VolumeF`.
- 0x09 is **not** an "extended" (0x12-style) root. It is a 0x10 root plus the legacy B tree.
- 0x0B/0x0C differ from 0x0A only in the root: the flags move from +0x06 to +0x08, one-shots move from +0x0C
  to +0x14 (count byte from +0x0B to +0x13), and v0x0C adds RootInfo.

## Conditions and sub-clips (playback)

Each clip names a condition, an index into its part's `ClipConditions` (main clips) or `SubClipConditions`
(sub-clips). Conditions are game states, and many are alternatives that never play together:

- normal vs pinch: `aph_normal`/`aph_pinch`, `mgr_normal`/`mgr_pinch`, `bt_normal_*`/`bt_pinch_*`
- boss phases or body parts: `boss_11_0_normal` ... `boss_11_4_arm_belly` (boss_bdragon), `boss_03_01`/`boss_03_02`
- results and choices: `pk_win`/`pk_lose`/`pk_draw`, `male`/`female`, `deepsea_01`-`04`, `before1`/`before2`
- situational layers: `*_hissatsu`, `bt_last1min`

`Normal`, `Hero`, `Pinch` and `Pse` are not toggles: the battle state drives them as above (UI "Battle state"
panel, `play --battle STATE` or the B key). `info` prints which part the battle state changes and what the
flags turn each state into.

Turning every condition on at once layers versions that never play together. So the player starts with
`Always`, names containing "normal" and the `RandomMovement*`/`RandomPhrase*` conditions, for main clips
and sub-clips alike. If that leaves only `Always`, it also turns on the first other condition. Everything
else starts off and can be toggled (`play --conditions A,B|all` sets the starting set from the command line).

Sub-clips follow their conditions exactly like main clips. There is no separate sub-clip switch; every
other sub-clip condition (`RandomMovement2`, `aph_pinch` ...) is a toggle of its own. Sub-clips can be full layers:
boss_aph's `APH_R_xx` (`aph_normal`) and `APH_R2_xx` (`aph_pinch`) are exactly one bar long, like
`APH_M`/`APH_O`, so the song is M + O + R for the active state.

**Random groups.** A condition list can hold the same `Random*` name several times. Each entry is a
separate alternative. Ancient's sub-clip list is four `RandomMovement2` entries, and every bar has one
`A_N_R_*` clip per entry (`R_M01`, `R_M06`, `R_M01`, `R_M10`). Its main clips do the same with `M1`/`M2`
under `RandomMovement1`. Playing every entry at once stacks the alternatives, which peaks. So, within one
part and list, the entries of a `Random*` name form a group:

- One entry of each group plays.
- It is picked at random again each time a section starts, so a section stays coherent.
- The UI can pin a group to one entry ("Random picks"), or leave it on Auto.

A `Random*` name used once in a list is an ordinary condition.

## Tools

```
MusTool datadir <pso2_bin\data\win32>     remember the data folder
MusTool songs                             known songs and their ice hashes
MusTool scan [song]                       version + binary/JSON round-trip check for every song, in memory
MusTool dump <song> --ice [relOff len]    hex view of REL0 with pointers and strings resolved
MusTool info <song> --ice --section P.S   one section bar by bar: every clip with volume and condition
MusTool timing <song> --ice P.S           per bar: scheduled length (Beats*60/Tempo) vs decoded clip lengths
MusTool clips <song> --ice --check        which CPKs a song's clips come from (own and shared)
```
