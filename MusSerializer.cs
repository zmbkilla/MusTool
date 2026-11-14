using System.Buffers.Binary;
using System.Text;

namespace MusTool;

// Binary layout (all little-endian). Pointers are u32 offsets relative to the REL0 chunk start
// and every pointer slot is listed in NOF0. Empty lists point at a shared 0xFFFFFFFF sentinel (rel 0x10).
// Version differences are described in docs/MUS_VERSIONS.md.
//
// File:
//   NIFL header (0x20): "NIFL", 0x18, 1, rel0Offset(0x20), rel0ChunkSize, nof0Offset, nof0Size, 0
//   REL0 chunk:         "REL0", size(chunk-8), rootHeaderOffset, 0, ...data..., padded to 16
//   NOF0 chunk:         "NOF0", unk, entryCount-1, u32 entries[entryCount] (sorted), padded to 16
//   NEND chunk:         "NEND", 8, 0, 0
//
// REL0 data:
//   RootHeader (4)        { Root* root }                  <- REL0 header points here
//   Root  v07/08 (8)      { Part* parts; u8 version; u8 partCount; u16 }
//         v09/0A (0x10)   { ...; u32 unk08; u32 or OneShot* (v0A, count = unk08 >> 24) }
//         v0B/0C (0x1C)   { ...; u32 unk08; u32 unk0C; u32 unk10 (count = unk10 >> 24); OneShot*; RootInfo* }
//         v12 (0xB4)      { ...; u32; void* (empty); u8[4]; void* (always empty); u8[4]; char* name;
//                           u8 tuneCount,u8[3]; Tune* tunes; char*; u8[0x88] }
//   OneShot   (0x10)      { u32 kind; Condition* cond; f32 volume; char* hca }
//   RootInfo  (8)         { u8[4]; Named* }   Named (8) { char* name; void* (always empty) }
//   Tune      (0x38)      see MusTune
//   Part      (12)        { Section* sections; Condition* conds; u8 sectionCount; u8 index; u8 clipCondCount; u8 subClipCondCount }
//   Condition (8 / 0x14)  { u32 value; char* name }   v12: { char* name; u8[16] }
//   Section   (8 / 0x2C)  { Phrase* phrases; u8 phraseCount; u8 type; u8 unk2; u8 unk3;  v12: char* name; u8[32] }
//   Phrase    (8)         { Bar* bars; u32 barCount }
//   Bar       (8)         { ClipGroup* variations; u32 variationCount }
//   flat (v0A and later):
//   ClipGroup (16 / 0x20) { Clip* clips; Clip* subClips; u16 tempo; u16 beats; u8 clipCount; u8 subClipCount; u8 unkE; u8 unkF;  v12: u8[16] }
//   Clip      (8)         { char* hcaName; u8 volume; u8 condition; u8 unk2; u8 unk3 }
//   legacy (v07-v09), ClipGroup -> Layer -> Track -> Clip* -> Clip -> char*[]:
//   ClipGroup (0x14 / 12) { Layer* clips; Layer* subClips;  v07: u32 tempo; u32 unk; u32 beats   v08/09: u16 tempo; u16 beats }
//   Layer     (0x0C / 8)  { Track* track;  v07: f32 volume; u8 kind (4 = has clips); u8[3]
//                                          v08/09: u8 volume; u8 kind; u8[2] }
//   Track     (8)         { Clip** clips; u32 clipCount }   (empty: pointer past REL0, see MusFile.EmptyListBase)
//   Clip      (0x0C / 8)  { char** names;  v07: f32 volume; u8 kind (0 = plays); u8 condition; u8 flags; u8 0
//                           v08/09: u8 volume; u8 kind; u8 condition; u8 flags }
//   flags bit 0 clear: the condition is the clip's position in the list, not the stored byte.
//   Bytes are kept raw here; MusPlayback turns them into what the game plays.
//
// Writer emits data in the same order the original tool did (bottom-up, strings last), so an
// unmodified file round-trips byte-for-byte.
public static class MusSerializer
{
    public const byte Version07 = 0x07;
    public const byte Version08 = 0x08;
    public const byte Version09 = 0x09;
    public const byte Version0A = 0x0A;
    public const byte Version0B = 0x0B;
    public const byte Version0C = 0x0C;
    public const byte Version12 = 0x12;

    /// <summary>
    /// First NGS (win32reboot) version. NGS files seen: 0x0F, 0x10, 0x12. 0x0D/0x0E are unseen and stay on the
    /// classic range rules. NGS is handled apart from classic everywhere: its own layout here, its own playback
    /// rules in MusPlayback (none of the classic pso2.exe findings are applied to it).
    /// </summary>
    public const byte FirstNgsVersion = 0x0F;

    public static bool IsNgs(byte version) => version >= FirstNgsVersion;

    const uint SentinelOffset = 0x10;

    enum Family { Legacy07, Legacy08, Flat, Ngs }

    sealed record Layout(Family Family, int Root, int Section, int Condition, int Group)
    {
        public bool Extended => Family == Family.Ngs;
        public bool Legacy => Family is Family.Legacy07 or Family.Legacy08;
        public int LayerSize => Family == Family.Legacy07 ? 0x0C : 8;
        public int LegacyClipSize => Family == Family.Legacy07 ? 0x0C : 8;

        /// <summary>
        /// Picks the layout by version range, the way the game's loader does (pso2.exe FUN_0056da70 / FUN_0056cd70 /
        /// FUN_0056df40): clip groups and clips are "&lt; 8", "8-9" or "10 and later"; the root is "&lt; 9", "9-10" or
        /// "later". Classic versions in between the ones seen (0x0D/0x0E) therefore read like their neighbours. The
        /// game rejects 6 and below. NGS versions (0x0F and later) use the NGS layout.
        /// </summary>
        public static Layout For(byte version) => version switch
        {
            <= 6 => throw new NotSupportedException($".mus version 0x{version:X2} is older than any the game accepts (7 and later)."),
            7 => new Layout(Family.Legacy07, 0x08, 8, 8, 0x14),
            8 => new Layout(Family.Legacy08, 0x08, 8, 8, 0x0C),
            9 => new Layout(Family.Legacy08, 0x10, 8, 8, 0x0C),
            10 => new Layout(Family.Flat, 0x10, 8, 8, 0x10),
            < FirstNgsVersion => new Layout(Family.Flat, 0x1C, 8, 8, 0x10),
            // NGS: clip groups are 0x10 bytes in 0x0F (like classic flat) and 0x20 from 0x10 on (16 extra bytes).
            _ => new Layout(Family.Ngs, 0xB4, 0x2C, 0x14, version == FirstNgsVersion ? 0x10 : 0x20),
        };
    }

    /// <summary>Versions whose layout was checked against real files; others are read by range (see Layout.For).</summary>
    public static bool IsVerifiedVersion(byte version) => version is >= Version07 and <= Version0C or 0x0F or 0x10 or Version12;

    /// <summary>The game treats clip records as legacy (layer/track/clip tree) below version 10.</summary>
    public static bool IsLegacy(byte version) => version < Version0A;

    // ---------------------------------------------------------------- reading

    public static MusFile Read(string path) => Read(File.ReadAllBytes(path));

    public static MusFile Read(byte[] data)
    {
        if (data.Length < 0x20 || Encoding.ASCII.GetString(data, 0, 4) != "NIFL")
            throw new InvalidDataException("Not a NIFL file.");

        int relBase = (int)U32(data, 0x0C);
        int nofOffset = (int)U32(data, 0x14);
        if (Encoding.ASCII.GetString(data, relBase, 4) != "REL0")
            throw new InvalidDataException("REL0 chunk not found.");
        if (Encoding.ASCII.GetString(data, nofOffset, 4) != "NOF0")
            throw new InvalidDataException("NOF0 chunk not found.");

        var r = new RelReader(data, relBase);
        int nofCount = (int)U32(data, nofOffset + 8) + 1;
        var pointerSlots = new HashSet<uint>();
        for (int i = 0; i < nofCount; i++) pointerSlots.Add(U32(data, nofOffset + 12 + i * 4));
        int relEnd = 8 + (int)U32(data, relBase + 4);
        uint rootHeader = r.U32(8);
        uint root = r.U32(rootHeader);

        var mus = new MusFile
        {
            Version = r.U8(root + 4),
            RootUnk06 = r.U16(root + 6),
            Nof0Unk = U32(data, nofOffset + 4),
            Nof0SizeUnpadded = U32(data, 0x18) % 16 != 0,
        };
        int gap = nofOffset - (relBase + relEnd);
        if (gap > 0) mus.Rel0Gap = data.AsSpan(relBase + relEnd, gap).ToArray();
        var layout = Layout.For(mus.Version);
        var ctx = new ReadContext(r, layout, mus, relEnd);

        if (layout.Root >= 0x10)
        {
            mus.RootUnk08 = r.U32(root + 8);
            mus.RootUnk0C = r.U32(root + 12);
            mus.RootList0C = pointerSlots.Contains(root + 12);
        }
        if (mus.RootList0C)
        {
            uint list = mus.RootUnk0C;
            mus.RootUnk0C = 0;
            if (layout.Extended && list != SentinelOffset)
                throw new NotSupportedException("Root list at +0x0C is not empty in this file; its format is unknown. A sample is needed.");
            if (!layout.Extended) ReadOneShots(r, list, (int)(mus.RootUnk08 >> 24), mus.OneShots);
        }
        if (layout.Root == 0x1C)
        {
            mus.RootUnk10 = r.U32(root + 0x10);
            ReadOneShots(r, r.U32(root + 0x14), (int)(mus.RootUnk10 >> 24), mus.OneShots);
            uint info = r.U32(root + 0x18);
            if (info != SentinelOffset)
            {
                uint named = r.U32(info + 4);
                mus.RootInfo = new MusRootInfo { Head = r.Bytes(info, 4) };
                if (named != SentinelOffset)
                {
                    if (r.U32(named + 4) != SentinelOffset)
                        throw new NotSupportedException("Root info list is not empty in this file; its format is unknown. A sample is needed.");
                    mus.RootInfo.Name = r.Str(r.U32(named));
                }
            }
        }

        if (layout.Extended)
        {
            // Root+0x14: one-shot list, same records as classic (count in the last byte of Unk10).
            ReadOneShots(r, r.U32(root + 0x14), r.U8(root + 0x13), mus.OneShots);
            var ext = new MusRootExtension
            {
                Unk10 = r.Bytes(root + 0x10, 4),
                Unk18 = r.Bytes(root + 0x18, 4),
                Name = r.Str(r.U32(root + 0x1C)),
                Unk20 = r.Bytes(root + 0x20, 4),
                Str28 = r.Str(r.U32(root + 0x28)),
                Tail = r.Bytes(root + 0x2C, 0x88),
            };
            uint tunes = r.U32(root + 0x24);
            for (int i = 0; i < ext.Unk20[0]; i++)
                ext.Tunes.Add(ReadTune(r, tunes + (uint)(i * TuneSize(mus.Version)), mus.Version));
            mus.Extension = ext;
        }

        uint parts = r.U32(root);
        int partCount = r.U8(root + 5);
        for (int pi = 0; pi < partCount; pi++)
        {
            uint p = parts + (uint)pi * 12;
            uint sections = r.U32(p), conds = r.U32(p + 4);
            int sectionCount = r.U8(p + 8), nClipCond = r.U8(p + 10), nSubCond = r.U8(p + 11);
            var part = new MusPart { Index = r.U8(p + 9) };

            for (int ci = 0; ci < nClipCond + nSubCond; ci++)
            {
                uint c = conds + (uint)(ci * layout.Condition);
                var cond = layout.Extended
                    ? new MusCondition { Name = r.Str(r.U32(c)), Extra = r.Bytes(c + 4, 16) }
                    : ReadCondition(r, c);
                (ci < nClipCond ? part.ClipConditions : part.SubClipConditions).Add(cond);
            }

            for (int si = 0; si < sectionCount; si++)
            {
                uint s = sections + (uint)(si * layout.Section);
                var section = new MusSection { Type = r.U8(s + 5), Unk2 = r.U8(s + 6), Unk3 = r.U8(s + 7) };
                if (layout.Extended)
                {
                    section.Name = r.Str(r.U32(s + 8));
                    section.Extra = r.Bytes(s + 12, 32);
                }
                uint phrases = r.U32(s);
                for (int hi = 0; hi < r.U8(s + 4); hi++)
                {
                    uint h = phrases + (uint)hi * 8;
                    var phrase = new MusPhrase();
                    uint bars = r.U32(h);
                    for (uint bi = 0; bi < r.U32(h + 4); bi++)
                    {
                        uint b = bars + bi * 8;
                        var bar = new MusBar();
                        uint groups = r.U32(b);
                        for (uint gi = 0; gi < r.U32(b + 4); gi++)
                        {
                            uint g = groups + gi * (uint)layout.Group;
                            bar.Variations.Add(layout.Legacy ? ReadLegacyGroup(ctx, g) : ReadGroup(r, g, layout));
                        }
                        phrase.Bars.Add(bar);
                    }
                    section.Phrases.Add(phrase);
                }
                part.Sections.Add(section);
            }
            mus.Parts.Add(part);
        }

        // Anything between the end of the string table and the end of REL0 is unreferenced data.
        int trailerStart = (int)r.StringsEnd;
        int trailerEnd = relEnd;
        while (trailerEnd > trailerStart && data[relBase + trailerEnd - 1] == 0) trailerEnd--;
        // Some NGS files pad REL0 with a whole extra 16-byte zero block that the writer's alignment would not
        // recreate; keep the zeros in the trailer then (NGS only; classic files never need it).
        if (layout.Extended && (int)Align((uint)Math.Max(trailerEnd, trailerStart), 16) != relEnd) trailerEnd = relEnd;
        mus.Trailer = data.AsSpan(relBase + trailerStart, Math.Max(0, trailerEnd - trailerStart)).ToArray();
        return mus;
    }

    sealed record ReadContext(RelReader R, Layout Layout, MusFile Mus, int RelEnd);

    static MusCondition ReadCondition(RelReader r, uint c) => new() { Value = r.U32(c), Name = r.Str(r.U32(c + 4)) };

    static void ReadOneShots(RelReader r, uint list, int count, List<MusOneShot> into)
    {
        if (list == SentinelOffset) return;
        for (int i = 0; i < count; i++)
        {
            uint o = list + (uint)i * 0x10;
            into.Add(new MusOneShot
            {
                Kind = r.U32(o),
                Condition = r.U32(o + 4) is var c && c != SentinelOffset ? ReadCondition(r, c) : null,
                Volume = r.F32(o + 8),
                File = r.Str(r.U32(o + 12)),
            });
        }
    }

    /// <summary>NGS tune record size: 0x30 in 0x0F/0x10, 0x38 from 0x12 on (adds Str24 and Unk34).</summary>
    static int TuneSize(byte version) => version >= Version12 ? 0x38 : 0x30;

    static MusTune ReadTune(RelReader r, uint t, byte version)
    {
        bool wide = TuneSize(version) == 0x38;
        uint x = wide ? 4u : 0u; // fields after Str20 shift by the extra string in the wide record
        return new MusTune
        {
            Unk00 = r.U32(t),
            Name = r.Str(r.U32(t + 0x04)),
            Unk08 = r.U32(t + 0x08),
            Unk0C = r.F32(t + 0x0C),
            Unk10 = r.U32(t + 0x10),
            Str14 = r.Str(r.U32(t + 0x14)),
            Str18 = r.Str(r.U32(t + 0x18)),
            Unk1C = r.U32(t + 0x1C),
            Str20 = r.Str(r.U32(t + 0x20)),
            Str24 = wide ? r.Str(r.U32(t + 0x24)) : null,
            Unk28 = r.Bytes(t + 0x24 + x, 4),
            Str2C = r.Str(r.U32(t + 0x28 + x)),
            Unk30 = r.U32(t + 0x2C + x),
            Unk34 = wide ? r.F32(t + 0x34) : null,
        };
    }

    static MusClipGroup ReadGroup(RelReader r, uint g, Layout layout)
    {
        var group = new MusClipGroup
        {
            Tempo = r.U16(g + 8),
            Beats = r.U16(g + 10),
            UnkE = r.U8(g + 14),
            UnkF = r.U8(g + 15),
            Extra = layout.Group == 0x20 ? r.Bytes(g + 16, 16) : null,
        };
        ReadClips(r, r.U32(g), r.U8(g + 12), group.Clips);
        ReadClips(r, r.U32(g + 4), r.U8(g + 13), group.SubClips);
        return group;
    }

    static void ReadClips(RelReader r, uint list, int count, List<MusClip> into)
    {
        for (int i = 0; i < count; i++)
        {
            uint c = list + (uint)i * 8;
            into.Add(new MusClip
            {
                File = r.Str(r.U32(c)),
                Volume = r.U8(c + 4),
                Condition = r.U8(c + 5),
                Unk2 = r.U8(c + 6),
                Unk3 = r.U8(c + 7),
            });
        }
    }

    static MusClipGroup ReadLegacyGroup(ReadContext ctx, uint g)
    {
        var r = ctx.R;
        var group = new MusClipGroup();
        if (ctx.Layout.Family == Family.Legacy07)
        {
            group.Tempo = checked((ushort)r.U32(g + 8));
            group.LegacyUnk = r.U32(g + 12);
            group.Beats = checked((ushort)r.U32(g + 16));
        }
        else
        {
            group.Tempo = r.U16(g + 8);
            group.Beats = r.U16(g + 10);
        }
        group.Layer = ReadLayer(ctx, r.U32(g), group.Clips);
        group.SubLayer = ReadLayer(ctx, r.U32(g + 4), group.SubClips);
        return group;
    }

    static MusLayer ReadLayer(ReadContext ctx, uint l, List<MusClip> into)
    {
        var r = ctx.R;
        bool v07 = ctx.Layout.Family == Family.Legacy07;
        // Raw bytes only; what they mean for playback is in MusPlayback (as the game uses one track per layer).
        var layer = new MusLayer { Data = r.Bytes(l + 4, ctx.Layout.LayerSize - 4) };

        uint track = r.U32(l);
        uint list = r.U32(track), count = r.U32(track + 4);
        if (count == 0)
        {
            ctx.Mus.EmptyListBase ??= (int)(list - ctx.RelEnd);
            return layer;
        }
        for (uint i = 0; i < count; i++)
        {
            uint c = r.U32(list + i * 4);
            uint names = r.U32(c);
            var clip = new MusClip { File = r.Str(r.U32(names)) };
            if (v07)
            {
                // Same byte roles as 0x08/0x09 after a float volume. Stored raw; MusPlayback.Clip interprets them.
                clip.VolumeF = r.F32(c + 4);
                clip.Volume = MusPlayback.LegacyVolume(clip.VolumeF.Value);
                clip.Unk2 = r.U8(c + 8);
                clip.Condition = r.U8(c + 9);
                clip.Unk3 = r.U8(c + 10);
                if (r.U8(c + 11) != 0)
                    throw new NotSupportedException($"v0x07 clip {clip.File} has a non-zero last byte. A sample is needed.");
            }
            else
            {
                clip.Volume = r.U8(c + 4);
                clip.Unk2 = r.U8(c + 5);
                clip.Condition = r.U8(c + 6);
                clip.Unk3 = r.U8(c + 7);
            }
            into.Add(clip);
        }
        return layer;
    }

    sealed class RelReader(byte[] data, int relBase)
    {
        public uint StringsEnd { get; private set; }

        public byte U8(uint o) => data[relBase + o];
        public ushort U16(uint o) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(relBase + (int)o));
        public uint U32(uint o) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(relBase + (int)o));
        public float F32(uint o) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(relBase + (int)o));
        public byte[] Bytes(uint o, int n) => data.AsSpan(relBase + (int)o, n).ToArray();

        public string Str(uint o)
        {
            int start = relBase + (int)o;
            int end = Array.IndexOf(data, (byte)0, start);
            StringsEnd = Math.Max(StringsEnd, Align((uint)(end - relBase + 1), 4));
            return Encoding.UTF8.GetString(data, start, end - start);
        }
    }

    // ---------------------------------------------------------------- writing

    public static void Write(MusFile mus, string path) => File.WriteAllBytes(path, Write(mus));

    public static byte[] Write(MusFile mus)
    {
        var layout = Layout.For(mus.Version);
        if (layout.Extended && mus.Extension is null)
            throw new InvalidDataException("Version 0x12 files need an Extension block.");
        if (mus.OneShots.Count > 0 && !(layout.Extended || layout.Root == 0x1C || (layout.Root == 0x10 && layout.Family == Family.Flat && mus.RootList0C)))
            throw new InvalidDataException($"One-shot sounds need version 0x0B/0x0C, or 0x0A with RootList0C set (this file is 0x{mus.Version:X2}).");

        var w = new RelWriter();
        w.Bytes("REL0"u8);
        w.U32(0); // size, patched later
        w.U32(0); // root header offset, patched later
        w.U32(0);
        w.PtrRaw(0xFFFFFFFF); // empty-list sentinel at rel 0x10; the original lists it in NOF0 too

        // 1. Clip lists, clip groups and bar arrays, depth first.
        var phraseBarArrays = new Dictionary<MusPhrase, uint>(ReferenceEqualityComparer.Instance);
        foreach (var part in mus.Parts)
            foreach (var section in part.Sections)
                foreach (var phrase in section.Phrases)
                {
                    var groupArrays = new List<uint>();
                    foreach (var bar in phrase.Bars)
                    {
                        if (layout.Legacy)
                        {
                            // As in the flat layout: all variations' clip layers, then all their sub-clip layers.
                            var layers = bar.Variations.Select(g => WriteLayer(w, layout, g.Layer, g.Clips)).ToList();
                            var subLayers = bar.Variations.Select(g => WriteLayer(w, layout, g.SubLayer, g.SubClips)).ToList();
                            groupArrays.Add(bar.Variations.Count == 0 ? SentinelOffset : w.Pos);
                            for (int i = 0; i < bar.Variations.Count; i++)
                                WriteLegacyGroup(w, layout, bar.Variations[i], layers[i], subLayers[i]);
                            continue;
                        }

                        // All variations' clip lists come before all their sub-clip lists.
                        var clipLists = bar.Variations.Select(g => WriteClips(w, g.Clips)).ToList();
                        var subLists = bar.Variations.Select(g => WriteClips(w, g.SubClips)).ToList();

                        groupArrays.Add(bar.Variations.Count == 0 ? SentinelOffset : w.Pos);
                        for (int i = 0; i < bar.Variations.Count; i++)
                        {
                            var g = bar.Variations[i];
                            w.Ptr(clipLists[i]);
                            w.Ptr(subLists[i]);
                            w.U16(g.Tempo);
                            w.U16(g.Beats);
                            w.U8(Count(g.Clips.Count, "clips in a group"));
                            w.U8(Count(g.SubClips.Count, "sub-clips in a group"));
                            w.U8(g.UnkE);
                            w.U8(g.UnkF);
                            if (layout.Group == 0x20) w.Fixed(g.Extra, 16);
                        }
                    }

                    phraseBarArrays[phrase] = phrase.Bars.Count == 0 ? SentinelOffset : w.Pos;
                    for (int i = 0; i < phrase.Bars.Count; i++)
                    {
                        w.Ptr(groupArrays[i]);
                        w.U32((uint)phrase.Bars[i].Variations.Count);
                    }
                }

        // 2. Phrase arrays for every section of every part.
        var sectionPhraseArrays = new Dictionary<MusSection, uint>(ReferenceEqualityComparer.Instance);
        foreach (var part in mus.Parts)
            foreach (var section in part.Sections)
            {
                sectionPhraseArrays[section] = section.Phrases.Count == 0 ? SentinelOffset : w.Pos;
                foreach (var phrase in section.Phrases)
                {
                    w.Ptr(phraseBarArrays[phrase]);
                    w.U32((uint)phrase.Bars.Count);
                }
            }

        // 3. Per part: section array followed by its condition array.
        var partArrays = new List<(uint sections, uint conds)>();
        foreach (var part in mus.Parts)
        {
            uint sections = part.Sections.Count == 0 ? SentinelOffset : w.Pos;
            foreach (var section in part.Sections)
            {
                w.Ptr(sectionPhraseArrays[section]);
                w.U8(Count(section.Phrases.Count, "phrases in a section"));
                w.U8(section.Type);
                w.U8(section.Unk2);
                w.U8(section.Unk3);
                if (layout.Extended)
                {
                    w.StrPtr(section.Name ?? "");
                    w.Fixed(section.Extra, 32);
                }
            }

            var conds = part.ClipConditions.Concat(part.SubClipConditions).ToList();
            uint condArray = conds.Count == 0 ? SentinelOffset : w.Pos;
            for (int i = 0; i < conds.Count; i++)
            {
                var c = conds[i];
                if (layout.Extended)
                {
                    w.StrPtr(c.Name);
                    // Default for new conditions follows the observed pattern: position, 0, 0xFF, 0.
                    int listPos = i < part.ClipConditions.Count ? i : i - part.ClipConditions.Count;
                    w.Fixed(c.Extra ?? [(byte)listPos, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 16);
                }
                else
                {
                    w.U32(c.Value);
                    w.StrPtr(c.Name);
                }
            }
            partArrays.Add((sections, condArray));
        }

        // 4. Part array, root lists (one-shots, root info, v12 tunes), root, root header.
        uint partsOffset = mus.Parts.Count == 0 ? SentinelOffset : w.Pos;
        for (int i = 0; i < mus.Parts.Count; i++)
        {
            var part = mus.Parts[i];
            w.Ptr(partArrays[i].sections);
            w.Ptr(partArrays[i].conds);
            w.U8(Count(part.Sections.Count, "sections in a part"));
            w.U8(part.Index);
            w.U8(Count(part.ClipConditions.Count, "clip conditions"));
            w.U8(Count(part.SubClipConditions.Count, "sub-clip conditions"));
        }

        uint oneShotsOffset = SentinelOffset;
        if (mus.OneShots.Count > 0)
        {
            var condOffsets = new List<uint>();
            foreach (var o in mus.OneShots)
            {
                if (o.Condition is not { } c) { condOffsets.Add(SentinelOffset); continue; }
                condOffsets.Add(w.Pos);
                w.U32(c.Value);
                w.StrPtr(c.Name);
            }
            oneShotsOffset = w.Pos;
            for (int i = 0; i < mus.OneShots.Count; i++)
            {
                var o = mus.OneShots[i];
                w.U32(o.Kind);
                w.Ptr(condOffsets[i]);
                w.F32(o.Volume);
                w.StrPtr(o.File);
            }
        }

        uint rootInfoOffset = SentinelOffset;
        if (layout.Root == 0x1C && mus.RootInfo is { } info)
        {
            uint named = SentinelOffset;
            if (info.Name != null)
            {
                named = w.Pos;
                w.StrPtr(info.Name);
                w.Ptr(SentinelOffset);
            }
            rootInfoOffset = w.Pos;
            w.Fixed(info.Head, 4);
            w.Ptr(named);
        }

        var ext = mus.Extension;
        uint tunesOffset = SentinelOffset;
        if (layout.Extended && ext!.Tunes.Count > 0)
        {
            tunesOffset = w.Pos;
            foreach (var t in ext.Tunes) WriteTune(w, t, mus.Version);
        }

        uint root = w.Pos;
        w.Ptr(partsOffset);
        w.U8(mus.Version);
        w.U8(Count(mus.Parts.Count, "parts"));
        w.U16(mus.RootUnk06);
        if (layout.Root == 0x10 && layout.Family == Family.Flat && mus.RootList0C)
        {
            w.U32(WithCount(mus.RootUnk08, mus.OneShots.Count));
            w.Ptr(oneShotsOffset);
        }
        else if (layout.Root >= 0x10)
        {
            w.U32(mus.RootUnk08);
            if (mus.RootList0C) w.Ptr(SentinelOffset);
            else w.U32(mus.RootUnk0C);
        }
        if (layout.Root == 0x1C)
        {
            w.U32(WithCount(mus.RootUnk10, mus.OneShots.Count));
            w.Ptr(oneShotsOffset);
            w.Ptr(rootInfoOffset);
        }
        if (layout.Extended)
        {
            var unk10 = (byte[])Fixed(ext!.Unk10, 4).Clone();
            unk10[3] = Count(mus.OneShots.Count, "one-shots");
            w.Bytes(unk10);
            w.Ptr(oneShotsOffset);
            w.Fixed(ext.Unk18, 4);
            w.StrPtr(ext.Name);
            var unk20 = (byte[])Fixed(ext.Unk20, 4).Clone();
            unk20[0] = Count(ext.Tunes.Count, "tunes");
            w.Bytes(unk20);
            w.Ptr(tunesOffset);
            w.StrPtr(ext.Str28);
            w.Fixed(ext.Tail, 0x88);
        }

        uint rootHeader = w.Pos;
        w.Ptr(root);

        // 5. Strings (deduplicated, 4-byte aligned), then the opaque trailer.
        w.FlushStrings();
        w.Bytes(mus.Trailer);
        w.Align(16);

        w.Patch(4, w.Pos - 8);
        w.Patch(8, rootHeader);
        w.PatchEmptyLists((uint)((int)w.Pos + (mus.EmptyListBase ?? 0)));

        // ---- assemble NIFL container
        byte[] rel = [.. w.ToArray(), .. mus.Rel0Gap ?? []];
        int relChunk = (int)w.Pos;
        var pointers = w.Pointers.OrderBy(p => p).ToArray();
        int nofDataSize = 12 + pointers.Length * 4;
        int nofChunk = (int)Align((uint)nofDataSize, 16);
        uint niflNofSize = mus.Nof0SizeUnpadded ? (uint)nofDataSize : (uint)nofChunk;

        var file = new byte[0x20 + rel.Length + nofChunk + 0x10];
        var span = file.AsSpan();
        "NIFL"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x04..], 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], (uint)relChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], (uint)(0x20 + rel.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], niflNofSize);
        rel.CopyTo(span[0x20..]);

        var nof = span[(0x20 + rel.Length)..];
        "NOF0"u8.CopyTo(nof);
        // Size of the entry data (count field + entries). Dragonland stores an unrelated value here,
        // so files keep whatever they were read with.
        BinaryPrimitives.WriteUInt32LittleEndian(nof[4..], mus.Nof0Unk ?? (uint)(nofDataSize - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(nof[8..], (uint)Math.Max(0, pointers.Length - 1));
        for (int i = 0; i < pointers.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(nof[(12 + i * 4)..], pointers[i]);

        var nend = span[(0x20 + rel.Length + nofChunk)..];
        "NEND"u8.CopyTo(nend);
        BinaryPrimitives.WriteUInt32LittleEndian(nend[4..], 8);
        return file;
    }

    static uint WithCount(uint value, int count) => (value & 0x00FFFFFF) | ((uint)Count(count, "one-shot sounds") << 24);

    static void WriteTune(RelWriter w, MusTune t, byte version)
    {
        bool wide = TuneSize(version) == 0x38;
        w.U32(t.Unk00);
        w.StrPtr(t.Name);
        w.U32(t.Unk08);
        w.F32(t.Unk0C);
        w.U32(t.Unk10);
        w.StrPtr(t.Str14);
        w.StrPtr(t.Str18);
        w.U32(t.Unk1C);
        w.StrPtr(t.Str20);
        if (wide) w.StrPtr(t.Str24 ?? "");
        w.Fixed(t.Unk28, 4);
        w.StrPtr(t.Str2C);
        w.U32(t.Unk30);
        if (wide) w.F32(t.Unk34 ?? 100f);
    }

    static uint WriteClips(RelWriter w, List<MusClip> clips)
    {
        if (clips.Count == 0) return SentinelOffset;
        uint start = w.Pos;
        foreach (var c in clips)
        {
            w.StrPtr(c.File);
            w.U8(c.Volume);
            w.U8(c.Condition);
            w.U8(c.Unk2);
            w.U8(c.Unk3);
        }
        return start;
    }

    /// <summary>Legacy layer subtree: per clip its name array then the clip, then the Clip* array, Track and Layer.</summary>
    static uint WriteLayer(RelWriter w, Layout layout, MusLayer? layer, List<MusClip> clips)
    {
        bool v07 = layout.Family == Family.Legacy07;
        var clipOffsets = new List<uint>();
        foreach (var c in clips)
        {
            uint names = w.Pos;
            w.StrPtr(c.File);
            clipOffsets.Add(w.Pos);
            w.Ptr(names);
            if (v07)
            {
                w.F32(c.VolumeF ?? c.Volume / 100f);
                w.U8(c.Unk2);
                w.U8(c.Condition);
                w.U8(c.Unk3);
                w.U8(0);
            }
            else
            {
                w.U8(c.Volume);
                w.U8(c.Unk2);
                w.U8(c.Condition);
                w.U8(c.Unk3);
            }
        }
        uint array = w.Pos;
        foreach (var o in clipOffsets) w.Ptr(o);

        uint track = w.Pos;
        if (clips.Count == 0) w.EmptyListPtr();
        else w.Ptr(array);
        w.U32((uint)clips.Count);

        uint at = w.Pos;
        w.Ptr(track);
        w.Fixed(layer?.Data ?? (v07 ? [0, 0, 0x80, 0x3F, 4, 1, 0, 0] : [100, 4, 1, 1]), layout.LayerSize - 4);
        return at;
    }

    static void WriteLegacyGroup(RelWriter w, Layout layout, MusClipGroup g, uint layer, uint subLayer)
    {
        w.Ptr(layer);
        w.Ptr(subLayer);
        if (layout.Family == Family.Legacy07)
        {
            w.U32(g.Tempo);
            w.U32(g.LegacyUnk ?? 0);
            w.U32(g.Beats);
        }
        else
        {
            w.U16(g.Tempo);
            w.U16(g.Beats);
        }
    }

    static byte Count(int n, string what) =>
        n <= byte.MaxValue ? (byte)n : throw new InvalidDataException($"Too many {what}: {n} (max 255).");

    static byte[] Fixed(byte[]? b, int n) =>
        b is null ? new byte[n]
        : b.Length == n ? b
        : throw new InvalidDataException($"Expected {n} bytes, got {b.Length}.");

    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

    static uint Align(uint v, uint a) => (v + a - 1) & ~(a - 1);

    sealed class RelWriter
    {
        byte[] buf = new byte[0x10000];
        readonly List<(uint at, string s)> stringFixups = new();
        readonly List<uint> emptyListFixups = new();

        public uint Pos { get; private set; }
        public List<uint> Pointers { get; } = new();

        void Grow(int n)
        {
            if (Pos + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, (int)Pos + n));
        }

        public void U8(byte v) { Grow(1); buf[Pos++] = v; }
        public void U16(ushort v) { Grow(2); BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan((int)Pos), v); Pos += 2; }
        public void U32(uint v) { Grow(4); BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)Pos), v); Pos += 4; }
        public void F32(float v) { Grow(4); BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan((int)Pos), v); Pos += 4; }
        public void Bytes(ReadOnlySpan<byte> b) { Grow(b.Length); b.CopyTo(buf.AsSpan((int)Pos)); Pos += (uint)b.Length; }
        public void Fixed(byte[]? b, int n) => Bytes(MusSerializer.Fixed(b, n));
        public void Align(uint a) { while (Pos % a != 0) U8(0); }
        public void Patch(uint at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)at), v);

        public void Ptr(uint target) => PtrRaw(target);
        public void PtrRaw(uint value) { Pointers.Add(Pos); U32(value); }

        public void StrPtr(string s) { stringFixups.Add((Pos, s)); Ptr(0); }

        /// <summary>Legacy empty track pointer; patched by <see cref="PatchEmptyLists"/> once the REL0 size is known.</summary>
        public void EmptyListPtr() { emptyListFixups.Add(Pos); Ptr(0); }

        public void PatchEmptyLists(uint first)
        {
            for (int i = 0; i < emptyListFixups.Count; i++) Patch(emptyListFixups[i], first + (uint)i * 4);
        }

        public void FlushStrings()
        {
            var placed = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var (at, s) in stringFixups)
            {
                if (!placed.TryGetValue(s, out uint off))
                {
                    off = Pos;
                    placed[s] = off;
                    Bytes(Encoding.UTF8.GetBytes(s));
                    U8(0);
                    Align(4);
                }
                Patch(at, off);
            }
            stringFixups.Clear();
        }

        public byte[] ToArray() => buf.AsSpan(0, (int)Pos).ToArray();
    }
}
