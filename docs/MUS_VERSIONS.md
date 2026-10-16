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
Condition (8)  { u32 value; char* name }                      (0x14 in v0x12)
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
| +0x06 | u16 (0x20, 0x34 ...) | u16 | u16 (0x000 - 0x2820; e.g. 0x2B4) | u16 = 0 |
| +0x08 | - | u32 (e.g. 30) | u8[4]; byte 3 = count(+0x0C) | u32 (the value v0x0A keeps at +0x06, e.g. 0x2B4) |
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
ClipGroup (0x10) { Clip* clips; Clip* subClips; u16 tempo; u16 beats; u8 clipCount; u8 subClipCount; u8 unkE; u8 unkF }
                   (+ u8[16] in v0x12)
Clip      (8)    { char* hca; u8 volume (0-100); u8 condition; u8 unk2; u8 unk3 }
```

Write order: a bar's clip lists come first, then its sub-clip lists, then its group array. When a bar has
several variations, the original tool writes *all* variations' clip lists, then *all* their sub-clip lists
(boss_mdfch2). The legacy layout follows the same rule with layers (defence_1_1).

### Legacy layout B (0x08, 0x09)

There is one more level of indirection, and each clip can carry a list of file names.

```
ClipGroup (0x0C) { Layer* clips; Layer* subClips; u16 tempo; u16 beats }
Layer     (8)    { Track* tracks; u8 volume (0x64); u8 unk (4); u8 unk (1); u8 trackCount (1) }
Track     (8)    { Clip** clips; u32 clipCount }
Clip      (8)    { char** names; u8 volume; u8 unk (0); u8 condition; u8 nameCount }
names            char*[nameCount]
```

Data is written bottom-up: names, Clip, (next clip ...), Clip* array, Track, Layer, then the same for
sub-clips, then ClipGroup.

Empty tracks (count 0) do not point at the sentinel. The n-th empty track in file order points to
`relEnd + base + 4*n`, where relEnd is the end of the REL0 chunk and base is a small per-file constant
(-16 to 0). The file reserves that space: between REL0 and NOF0 there is a block of zero bytes, about 4 per
empty track, that is not counted in the REL0 size. MusTool keeps `base` (`EmptyListBase`) and the block
(`Rel0Gap`) so files round-trip.

Observed in every legacy file: each Layer has exactly one Track (trackCount = 1), each Clip has exactly
one name (nameCount = 1), and bars have 1 variation (2 in some 0x09 songs). The reader stops with a clear
error if a file breaks one of these assumptions.

### Legacy layout A (0x07)

Same tree as B, but volumes are floats and the records are wider:

```
ClipGroup (0x14) { Layer* clips; Layer* subClips; u32 tempo; u32 unk (0); u32 beats }
Layer     (0x0C) { Track* tracks; f32 volume (1.0); u8 unk (4); u8 unk (1); u8 0; u8 0 }
Track     (8)    { Clip** clips; u32 clipCount }
Clip      (0x0C) { char** names; f32 volume (0.0-1.0); u8 unk2 (0); u8 unk3 (0-12); u8 condition (0/1); u8 0 }
```

0x07 stores no name count (always one name). The condition is taken to be byte +0x0A. Its values (0/1)
fit the two conditions those songs define, whereas +0x09 goes up to 12. Sub-clip layers without real clips
hold a clip named "".

## How MusTool maps the versions

- Every version is loaded into the same model (`MusFile` -> parts -> sections -> phrases -> bars ->
  clip groups -> clips), so `info`, `play`, `export`/`import` and the UI work the same for all of them.
- Legacy groups keep their layers in `MusClipGroup.Layer` / `SubLayer` (raw bytes; `Gain()` gives the
  volume) and v0x07 clips keep their float volume in `VolumeF`. Playback gain = clip volume x layer volume.
- 0x09 is **not** an "extended" (0x12-style) root. It is a 0x10 root plus the legacy B tree.
- 0x0B/0x0C differ from 0x0A only in the root: one-shots move from +0x0C to +0x14 (count byte from +0x0B to
  +0x13), and v0x0C adds RootInfo.

## Tools

```
MusTool datadir <pso2_bin\data\win32>     remember the data folder
MusTool songs                             known songs and their ice hashes
MusTool scan [song]                       version + binary/JSON round-trip check for every song, in memory
MusTool dump <song> --ice [relOff len]    hex view of REL0 with pointers and strings resolved
```
