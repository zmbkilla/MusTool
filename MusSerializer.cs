using System.Buffers.Binary;
using System.Text;

namespace MusTool;

// Binary layout (all little-endian). Pointers are u32 offsets relative to the REL0 chunk start
// and every pointer slot is listed in NOF0. Empty lists point at a shared 0xFFFFFFFF sentinel (rel 0x10).
//
// File:
//   NIFL header (0x20): "NIFL", 0x18, 1, rel0Offset(0x20), rel0ChunkSize, nof0Offset, nof0Size, 0
//   REL0 chunk:         "REL0", size(chunk-8), rootHeaderOffset, 0, ...data..., padded to 16
//   NOF0 chunk:         "NOF0", unk, entryCount-1, u32 entries[entryCount] (sorted), padded to 16
//   NEND chunk:         "NEND", 8, 0, 0
//
// REL0 data (sizes: version 0x0A / version 0x12):
//   RootHeader (4)        { Root* root }                  <- REL0 header points here
//   Root      (16 / 0xB4) { Part* parts; u8 version; u8 partCount; u16; u32; u32 or void* (empty list);
//                           v12: u8[4]; void* (always empty); u8[4]; char* name; u8 tuneCount,u8[3]; Tune* tunes; char*; u8[0x88] }
//   Tune      (- / 0x38)  see MusTune
//   Part      (12)        { Section* sections; Condition* conds; u8 sectionCount; u8 index; u8 clipCondCount; u8 subClipCondCount }
//   Condition (8 / 0x14)  v0A: { u32 value; char* name }   v12: { char* name; u8[16] }
//   Section   (8 / 0x2C)  { Phrase* phrases; u8 phraseCount; u8 type; u8 unk2; u8 unk3;  v12: char* name; u8[32] }
//   Phrase    (8)         { Bar* bars; u32 barCount }
//   Bar       (8)         { ClipGroup* variations; u32 variationCount }
//   ClipGroup (16 / 0x20) { Clip* clips; Clip* subClips; u16 tempo; u16 beats; u8 clipCount; u8 subClipCount; u8 unkE; u8 unkF;  v12: u8[16] }
//   Clip      (8)         { char* hcaName; u8 volume; u8 condition; u8 unk2; u8 unk3 }
//
// Writer emits data in the same order the original tool did (bottom-up, strings last), so an
// unmodified file round-trips byte-for-byte.
public static class MusSerializer
{
    public const byte Version0A = 0x0A;
    public const byte Version12 = 0x12;

    const uint SentinelOffset = 0x10;

    sealed record Layout(int Section, int Condition, int Group, int Root, bool Extended)
    {
        public static Layout For(byte version) => version switch
        {
            Version0A => new Layout(8, 8, 0x10, 0x10, false),
            Version12 => new Layout(0x2C, 0x14, 0x20, 0xB4, true),
            _ => throw new NotSupportedException(
                $"Unsupported .mus version 0x{version:X2} (known: 0x0A, 0x12). A sample of this file is needed to add it."),
        };
    }

    // ---------------------------------------------------------------- reading

    public static MusFile Read(string path) => Read(File.ReadAllBytes(path));
    public static MusFile ReadIce(byte[] file) => Read(file);

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
            RootUnk08 = r.U32(root + 8),
            RootUnk0C = r.U32(root + 12),
            RootList0C = pointerSlots.Contains(root + 12),
            Nof0Unk = U32(data, nofOffset + 4),
            Nof0SizeUnpadded = U32(data, 0x18) % 16 != 0,
        };
        if (mus.RootList0C)
        {
            if (mus.RootUnk0C != SentinelOffset)
                throw new NotSupportedException("Root list at +0x0C is not empty in this file; its format is unknown. A sample is needed.");
            mus.RootUnk0C = 0;
        }
        var layout = Layout.For(mus.Version);

        if (layout.Extended)
        {
            if (r.U32(root + 0x14) != SentinelOffset)
                throw new NotSupportedException("Root list at +0x14 is not empty in this file; its format is unknown. A sample is needed.");
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
                ext.Tunes.Add(ReadTune(r, tunes + (uint)i * 0x38));
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
                    : new MusCondition { Value = r.U32(c), Name = r.Str(r.U32(c + 4)) };
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
                            bar.Variations.Add(ReadGroup(r, groups + gi * (uint)layout.Group, layout));
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
        mus.Trailer = data.AsSpan(relBase + trailerStart, Math.Max(0, trailerEnd - trailerStart)).ToArray();
        return mus;
    }

    static MusTune ReadTune(RelReader r, uint t) => new()
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
        Str24 = r.Str(r.U32(t + 0x24)),
        Unk28 = r.Bytes(t + 0x28, 4),
        Str2C = r.Str(r.U32(t + 0x2C)),
        Unk30 = r.U32(t + 0x30),
        Unk34 = r.F32(t + 0x34),
    };

    static MusClipGroup ReadGroup(RelReader r, uint g, Layout layout)
    {
        var group = new MusClipGroup
        {
            Tempo = r.U16(g + 8),
            Beats = r.U16(g + 10),
            UnkE = r.U8(g + 14),
            UnkF = r.U8(g + 15),
            Extra = layout.Extended ? r.Bytes(g + 16, 16) : null,
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
                        var lists = new List<(uint clips, uint subs)>();
                        foreach (var g in bar.Variations)
                            lists.Add((WriteClips(w, g.Clips), WriteClips(w, g.SubClips)));

                        groupArrays.Add(bar.Variations.Count == 0 ? SentinelOffset : w.Pos);
                        for (int i = 0; i < bar.Variations.Count; i++)
                        {
                            var g = bar.Variations[i];
                            w.Ptr(lists[i].clips);
                            w.Ptr(lists[i].subs);
                            w.U16(g.Tempo);
                            w.U16(g.Beats);
                            w.U8(Count(g.Clips.Count, "clips in a group"));
                            w.U8(Count(g.SubClips.Count, "sub-clips in a group"));
                            w.U8(g.UnkE);
                            w.U8(g.UnkF);
                            if (layout.Extended) w.Fixed(g.Extra, 16);
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

        // 4. Part array, (v12) tunes, root, root header.
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

        var ext = mus.Extension;
        uint tunesOffset = SentinelOffset;
        if (layout.Extended && ext!.Tunes.Count > 0)
        {
            tunesOffset = w.Pos;
            foreach (var t in ext.Tunes) WriteTune(w, t);
        }

        uint root = w.Pos;
        w.Ptr(partsOffset);
        w.U8(mus.Version);
        w.U8(Count(mus.Parts.Count, "parts"));
        w.U16(mus.RootUnk06);
        w.U32(mus.RootUnk08);
        if (mus.RootList0C) w.Ptr(SentinelOffset);
        else w.U32(mus.RootUnk0C);
        if (layout.Extended)
        {
            w.Fixed(ext!.Unk10, 4);
            w.Ptr(SentinelOffset);
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

        // ---- assemble NIFL container
        byte[] rel = w.ToArray();
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
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], (uint)rel.Length);
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

    static void WriteTune(RelWriter w, MusTune t)
    {
        w.U32(t.Unk00);
        w.StrPtr(t.Name);
        w.U32(t.Unk08);
        w.F32(t.Unk0C);
        w.U32(t.Unk10);
        w.StrPtr(t.Str14);
        w.StrPtr(t.Str18);
        w.U32(t.Unk1C);
        w.StrPtr(t.Str20);
        w.StrPtr(t.Str24);
        w.Fixed(t.Unk28, 4);
        w.StrPtr(t.Str2C);
        w.U32(t.Unk30);
        w.F32(t.Unk34);
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
