# PSO2 .mus format versions

Findings from reading every adaptive song shipped in the base game's `data/win32` (JP client, Feb 2021), in
memory, with `MusTool scan` / `MusTool dump --ice`. 215 of the 216 names in `songs.txt` are present.

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

## Versions found

| Version | Songs | Root size | Clip-group layout | Status in MusTool |
|---|---|---|---|---|
| 0x07 | 24 | 0x08 | legacy, float volumes (A) | not supported yet |
| 0x08 | 3  | 0x08 | legacy, byte volumes (B) | not supported yet |
| 0x09 | 25 | 0x10 | legacy, byte volumes (B) | not supported yet |
| 0x0A | 89 | 0x10 | flat | 78 round-trip; 11 need the fixes below |
| 0x0B | 5  | 0x1C | flat | not supported yet |
| 0x0C | 69 | 0x1C | flat | not supported yet |
| 0x12 | NGS only (not in this data) | 0xB4 | flat + 16-byte extras | supported (from earlier samples) |

Songs per version are listed by `MusTool scan`.

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
OneShot  (0x10) { u32 kind (2 or 3); Condition* cond; f32 volume (100.0); char* hca }   e.g. "NOD_hit_BDF.hca"
RootInfo (8)    { u8 a (0/1); u8 b (0/1/2); u16 0; Named* named }   (v0x0C only)
Named    (8)    { char* name (observed ""); void* list (always the empty sentinel) }
```

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
(boss_mdfch2). MusTool currently interleaves them per variation, so those files do not round-trip yet.

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
sub-clips, then ClipGroup. An empty Track points one past the end of REL0 (e.g. `0x17530` when REL0 data
ends at `0x17530`), not at the sentinel. Each empty track gets its own address (+4 per track), so these
values have to be kept as they are to round-trip.

### Legacy layout A (0x07)

Same tree as B, but volumes are floats and the records are wider:

```
ClipGroup (0x14) { Layer* clips; Layer* subClips; u32 tempo; u32 unk (0); u32 beats }
Layer     (0x0C) { Track* tracks; f32 volume (1.0); u8 unk (4); u8 unk (1); u8 0; u8 0 }
Track     (8)    { Clip** clips; u32 clipCount }
Clip      (0x0C) { char** names; f32 volume (0.95, 1.0); u8 unk (0); u8 unk (2); u8 0; u8 0 }
```

Not yet verified for 0x07: where the name count is stored (every sample seen so far has one name) and
which byte is the condition index.

## Practical consequences

- 0x0B and 0x0C only need the wider root. Their groups/clips already parse with the 0x0A reader, which is
  why `scan` reports them as parsed but not round-tripping.
- 0x09 is **not** an "extended" (0x12-style) root. It is a 0x10 root plus the legacy B tree.
- 0x07, 0x08 and 0x09 can be mapped onto the common model for playback: one Layer -> one Track -> clips.
  A clip with several names is a random pick among them. To write these files back byte-for-byte, the
  legacy fields have to be kept.

## Tools

```
MusTool datadir <pso2_bin\data\win32>     remember the data folder
MusTool songs                             known songs and their ice hashes
MusTool scan [song]                       version + parse/round-trip check for every song, in memory
MusTool dump <song> --ice [relOff len]    hex view of REL0 with pointers and strings resolved
```
