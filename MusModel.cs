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
    /// Format version (root+0x04); see docs/MUS_VERSIONS.md. 0x07-0x09 = legacy clip tree (layer/track/clip),
    /// 0x0A-0x0C = flat clip groups (0x0B/0x0C have a wider root), 0x12 = NGS layout with extra root data.
    /// </summary>
    public byte Version { get; set; } = 0x0A;

    /// <summary>Root+0x06 / +0x08 / +0x0C (+0x08/+0x0C only from v0x09 on). Observed 0/0/0 (v0x0A) and 0/0x290/0 (v0x12).</summary>
    public ushort RootUnk06 { get; set; }
    public uint RootUnk08 { get; set; }
    public uint RootUnk0C { get; set; }

    /// <summary>v0x0B/v0x0C root+0x10. The top byte is written as OneShots.Count.</summary>
    public uint RootUnk10 { get; set; }

    /// <summary>
    /// v0x0A/v0x12: true when root+0x0C is a pointer (to OneShots, or to the empty list) rather than the plain
    /// value RootUnk0C. In v0x0A the top byte of RootUnk08 is then written as OneShots.Count.
    /// </summary>
    public bool RootList0C { get; set; }

    /// <summary>Sounds triggered by game events rather than by the bar schedule (e.g. "NOD_hit_BDF.hca"). v0x0A-v0x0C.</summary>
    public List<MusOneShot> OneShots { get; set; } = new();

    /// <summary>v0x0C root+0x18 block. Null in other versions.</summary>
    public MusRootInfo? RootInfo { get; set; }

    /// <summary>
    /// Legacy versions (0x07-0x09): empty clip tracks do not point at the sentinel but past the end of REL0,
    /// at relEnd + EmptyListBase + 4*n for the n-th empty track. Preserved so files round-trip.
    /// </summary>
    public int? EmptyListBase { get; set; }

    /// <summary>
    /// Bytes between the REL0 and NOF0 chunks. Legacy files reserve 4 zero bytes there per empty track
    /// (where those tracks point). Preserved as-is; null when there is none.
    /// </summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[]? Rel0Gap { get; set; }

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

/// <summary>0x10-byte record in the v0x0A-v0x0C root list: a sound the game fires on an event.</summary>
public sealed class MusOneShot
{
    /// <summary>Observed 2, 3 and 4.</summary>
    public uint Kind { get; set; } = 2;
    /// <summary>Null when the condition pointer is the empty sentinel (observed with kind 4).</summary>
    public MusCondition? Condition { get; set; }
    /// <summary>Observed 100.0.</summary>
    public float Volume { get; set; } = 100f;
    public string File { get; set; } = "";
}

/// <summary>v0x0C root+0x18: { u8[4] head; Named* } where Named = { char* name; void* list (always empty) }.</summary>
public sealed class MusRootInfo
{
    /// <summary>Observed 00 00 00 00, 00 01 00 00, 01 02 00 00.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Head { get; set; } = new byte[4];

    /// <summary>
    /// Null when the Named pointer is the empty sentinel. Observed "" and comma-separated song names
    /// (boss_pd2: "mu_11_bgm_sv2,mu_11_bgm_sv1,mu_11_bgm_flt_before").
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>Legacy (v0x07-v0x09) layer: { Track* track; data }. The game uses one track per layer.</summary>
public sealed class MusLayer
{
    /// <summary>
    /// Raw bytes after the track pointer. v0x07: f32 volume (0.0-1.0), u8 kind, u8[3].
    /// v0x08/v0x09: u8 volume (0-100), u8 kind, u8[2]. Only kind 4 has clips; the volume scales the whole list
    /// (it becomes the flat group's UnkE / UnkF). See MusPlayback.Group.
    /// </summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Data { get; set; } = [100, 4, 1, 1];
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
    /// <summary>v0x12 only (tune record 0x38 bytes); null in 0x0F/0x10, whose tune record is 0x30 bytes.</summary>
    public string? Str24 { get; set; }
    /// <summary>Observed 00 03 00 00 and 00 03 00 01.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[] Unk28 { get; set; } = [0, 3, 0, 0];
    public string Str2C { get; set; } = "";
    public uint Unk30 { get; set; }
    /// <summary>v0x12 only, like <see cref="Str24"/>. Observed 100.</summary>
    public float? Unk34 { get; set; }
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
    /// <summary>
    /// u32 stored before the name pointer (all versions but 0x12). The game reads it as a float: the gain of this
    /// condition's clips while the condition is not met (MusPlayback.OffGain). 0 (= silent) in every shipped file.
    /// </summary>
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

    /// <summary>Byte at group+0x0E: volume (0-100) of every clip in Clips (the game's scheduler scales by it).</summary>
    public byte UnkE { get; set; } = 0x64;

    /// <summary>Byte at group+0x0F: volume (0-100) of every clip in SubClips.</summary>
    public byte UnkF { get; set; } = 0x64;

    /// <summary>v0x12 only: 16 bytes at +0x10. Observed all zero.</summary>
    [JsonConverter(typeof(HexBytesConverter))]
    public byte[]? Extra { get; set; }

    /// <summary>Legacy only (v0x07-v0x09): the layers holding Clips / SubClips; their volume takes the place of UnkE/UnkF.</summary>
    public MusLayer? Layer { get; set; }
    public MusLayer? SubLayer { get; set; }

    /// <summary>v0x07 only: u32 between tempo and beats. Always 0 in observed data.</summary>
    public uint? LegacyUnk { get; set; }

    /// <summary>Layered stems that play together (base, M1_, M2_, SQ_, HERO_ ...).</summary>
    public List<MusClip> Clips { get; set; } = new();

    /// <summary>
    /// Second clip list, using Part.SubClipConditions; plays by its conditions just like Clips (boss_aph APH_R /
    /// APH_R2 layers, ancient's RandomMovement2 alternatives, PSE_, pinch_).
    /// </summary>
    public List<MusClip> SubClips { get; set; } = new();
}

public sealed class MusClip
{
    /// <summary>HCA cue file name inside the song's CPK.</summary>
    public string File { get; set; } = "";

    /// <summary>Probably volume, 0-100. Observed: 100 for jingles, 74-89 for stems.</summary>
    public byte Volume { get; set; } = 100;

    /// <summary>v0x07 only: the volume as stored there (f32, 0.0-1.0). Written instead of Volume when set.</summary>
    public float? VolumeF { get; set; }

    /// <summary>Index into the owning part's ClipConditions / SubClipConditions.</summary>
    public byte Condition { get; set; }

    /// <summary>
    /// Flat: always 1 (it is where the game puts the legacy flags byte when converting). Legacy: the kind byte
    /// (v0x07 +0x08, v0x08/09 +0x05); the game only plays clips of kind 0.
    /// </summary>
    public byte Unk2 { get; set; } = 1;

    /// <summary>
    /// Flat: always 0. Legacy: flags (v0x07 +0x0A, v0x08/09 +0x07). Bit 0 clear: the game ignores Condition and uses
    /// the clip's position in its list instead (MusPlayback.Clip).
    /// </summary>
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
