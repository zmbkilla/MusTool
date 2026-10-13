using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusTool;

// Object model for PSO2 .mus (interactive / adaptive BGM) files.
//
// Hierarchy:  MusFile -> Part -> Section -> Phrase -> Bar -> ClipGroup (variation) -> Clip (.hca)
//
// Field names marked "Unk" are not understood yet; they are preserved verbatim so that
// read -> write round-trips byte for byte. The "observed" notes come from mu_11_bgm_dragonland.mus.

public sealed class MusFile
{
    /// <summary>
    /// Format version (root+0x04). 0x0A = older layout (e.g. mu_11_bgm_dragonland),
    /// 0x12 = newer layout with larger sections/conditions/groups and extra root data (e.g. mu_11_bgm02_boss_nea_md).
    /// </summary>
    public byte Version { get; set; } = 0x0A;

    /// <summary>Root+0x06 / +0x08 / +0x0C. Observed 0/0/0 (v0x0A) and 0/0x290/0 (v0x12).</summary>
    public ushort RootUnk06 { get; set; }
    public uint RootUnk08 { get; set; }
    public uint RootUnk0C { get; set; }

    /// <summary>
    /// True when root+0x0C is a pointer (always to an empty list in observed files, e.g. cafe_night)
    /// rather than the plain value RootUnk0C (dragonland).
    /// </summary>
    public bool RootList0C { get; set; }

    /// <summary>
    /// The NIFL header normally stores the NOF0 size rounded up to 16; dragonland stores it unrounded.
    /// </summary>
    public bool Nof0SizeUnpadded { get; set; }

    /// <summary>Extra root data present only in version 0x12.</summary>
    public MusRootExtension? Extension { get; set; }

    public List<MusPart> Parts { get; set; } = new();

    /// <summary>
    /// NOF0 chunk's size field. Normally 4 + 4*entries, but dragonland holds an unrelated value (0xEC44),
    /// so it is preserved. Null = compute 4 + 4*entries when writing.
    /// </summary>
    public uint? Nof0Unk { get; set; }

    /// <summary>
    /// Bytes after the string table that no pointer references (observed 32 bytes that look like a
    /// bitmask lookup table). Preserved as-is.
    /// </summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Trailer { get; set; } = Convert.FromHexString(
        "00000000010000000A0101020408102040000400080010002000400080000001");
}

/// <summary>Root fields that only exist in version 0x12 files.</summary>
public sealed class MusRootExtension
{
    /// <summary>Root+0x10. Observed 01 01 05 00.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Unk10 { get; set; } = [1, 1, 5, 0];

    /// <summary>Root+0x18. Observed 01 02 03 00.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Unk18 { get; set; } = [1, 2, 3, 0];

    /// <summary>Root+0x1C. The song name, e.g. "mu_11_bgm02_boss_nea".</summary>
    public string Name { get; set; } = "";

    /// <summary>Root+0x20. Byte 0 is written as Tunes.Count; the other bytes are unknown (observed 01 01 00).</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Unk20 { get; set; } = [0, 1, 1, 0];

    /// <summary>Root+0x24.</summary>
    public List<MusTune> Tunes { get; set; } = new();

    /// <summary>Root+0x28. Observed "".</summary>
    public string Str28 { get; set; } = "";

    /// <summary>Root+0x2C..0xB4. Observed all zero.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Tail { get; set; } = new byte[0x88];
}

/// <summary>0x38-byte record referenced from the v0x12 root (observed name "MusicDiscTune", twice).</summary>
public sealed class MusTune
{
    public uint Unk00 { get; set; } = 2;
    public string Name { get; set; } = "MusicDiscTune";
    public uint Unk08 { get; set; }
    public float Unk0C { get; set; }
    public uint Unk10 { get; set; }
    public string Str14 { get; set; } = "";
    public string Str18 { get; set; } = "";
    public uint Unk1C { get; set; }
    public string Str20 { get; set; } = "";
    public string Str24 { get; set; } = "";
    /// <summary>Observed 00 03 00 00 and 00 03 00 01.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Unk28 { get; set; } = [0, 3, 0, 0];
    public string Str2C { get; set; } = "";
    public uint Unk30 { get; set; }
    public float Unk34 { get; set; } = 100f;
}

/// <summary>
/// One state machine / track set. Dragonland: 0 = field non-combat (GN_*), 1 = field combat (GB_*),
/// 2 = non-combat->combat transitions (G_NtoB_*), 3 = combat->non-combat (G_BtoN_*), 4 = G_NtoB variant.
/// </summary>
public sealed class MusPart
{
    /// <summary>Byte at part+0x09. Equals the part's position in the list in every observed file.</summary>
    public byte Index { get; set; }

    /// <summary>Indexed by MusClip.Condition for entries in ClipGroup.Clips (e.g. "Always", "Hero", "RandomMovement1").</summary>
    public List<MusCondition> ClipConditions { get; set; } = new();

    /// <summary>Indexed by MusClip.Condition for entries in ClipGroup.SubClips (e.g. "Pse", "Pinch", "RandomMovement3").</summary>
    public List<MusCondition> SubClipConditions { get; set; } = new();

    public List<MusSection> Sections { get; set; } = new();
}

public sealed class MusCondition
{
    /// <summary>v0x0A only: u32 stored before the name pointer. Always 0 in observed data.</summary>
    public uint Value { get; set; }

    public string Name { get; set; } = "Always";

    /// <summary>v0x12 only: 16 bytes after the name pointer. Observed "II 00 FF 00" + 12 zero bytes, II = list position.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[]? Extra { get; set; }
}

public sealed class MusSection
{
    /// <summary>
    /// Section kind. Observed: 0 = entry/intro, 1 = main body, 2 = outro ("O"), 3 = special ("S01/S02"),
    /// 4 = quest-clear jingle, 8 = silence, 9 = campship.
    /// </summary>
    public byte Type { get; set; } = 1;

    /// <summary>Observed values 0x02, 0x0A, 0x13. Meaning unknown.</summary>
    public byte Unk2 { get; set; } = 2;

    /// <summary>Observed values 0, 3, 4, 5, 7, 8 (8 appears on 2-bar bridge sections). Meaning unknown.</summary>
    public byte Unk3 { get; set; }

    /// <summary>v0x12 only: string pointer at +0x08. Observed "".</summary>
    public string? Name { get; set; }

    /// <summary>v0x12 only: 32 bytes at +0x0C. Observed all zero.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[]? Extra { get; set; }

    public List<MusPhrase> Phrases { get; set; } = new();
}

public sealed class MusPhrase
{
    public List<MusBar> Bars { get; set; } = new();
}

public sealed class MusBar
{
    /// <summary>Alternative clip groups for this bar. Every observed bar has exactly one.</summary>
    public List<MusClipGroup> Variations { get; set; } = new();
}

public sealed class MusClipGroup
{
    /// <summary>BPM. Matches the T### in the clip file names.</summary>
    public ushort Tempo { get; set; } = 120;

    /// <summary>Matches the B## in the clip file names (beats or bars per clip; not yet verified).</summary>
    public ushort Beats { get; set; } = 4;

    /// <summary>Byte at group+0x0E. Observed 0x4A or 0x64. Possibly a volume.</summary>
    public byte UnkE { get; set; } = 0x64;

    /// <summary>Byte at group+0x0F. Observed 0x4A, 0x4B or 0x64. Possibly a volume.</summary>
    public byte UnkF { get; set; } = 0x64;

    /// <summary>v0x12 only: 16 bytes at +0x10. Observed all zero.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[]? Extra { get; set; }

    /// <summary>Layered stems that play together (base, M1_, M2_, SQ_, HERO_ ...).</summary>
    public List<MusClip> Clips { get; set; } = new();

    /// <summary>Second clip list (R_ tails, PSE_, pinch_ ...). Uses Part.SubClipConditions.</summary>
    public List<MusClip> SubClips { get; set; } = new();
}

public sealed class MusClip
{
    /// <summary>HCA cue file name inside the song's CPK.</summary>
    public string File { get; set; } = "";

    /// <summary>Probably volume, 0-100. Observed: 100 for jingles, 74-89 for stems.</summary>
    public byte Volume { get; set; } = 100;

    /// <summary>Index into the owning part's ClipConditions / SubClipConditions.</summary>
    public byte Condition { get; set; }

    /// <summary>Always 1 in observed data.</summary>
    public byte Unk2 { get; set; } = 1;

    /// <summary>Always 0 in observed data.</summary>
    public byte Unk3 { get; set; }
}

public sealed class HexBytesConverter : JsonConverter<byte[]?>
{
    public override bool HandleNull => false;

    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : Convert.FromHexString((reader.GetString() ?? "").Replace(" ", ""));

    public override void Write(Utf8JsonWriter writer, byte[]? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(Convert.ToHexString(value));
    }
}
