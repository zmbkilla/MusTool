namespace MusTool;

/// <summary>
/// What the game plays from a song, for any version. MusSerializer keeps every version's records raw so files
/// round-trip; these functions turn them into one playable form the way the game's loader does
/// (pso2.exe: FUN_0056da70 clip group, FUN_0056cd70 / FUN_0056ce90 clips, FUN_0056df40 root, FUN_00548310 gain).
/// Like the game they branch on version ranges (below 8, 8-9, 10 and later), so unseen versions in between
/// are handled like their neighbours.
/// </summary>
public static class MusPlayback
{
    /// <summary>A clip as the game schedules it.</summary>
    /// <param name="Condition">Index into the part's ClipConditions (or SubClipConditions for sub-clips).</param>
    /// <param name="Volume">0-100.</param>
    /// <param name="Plays">False for records the game skips (legacy kind byte not 0, empty file name).</param>
    public sealed record PlayClip(MusClip Source, string File, int Volume, int Condition, bool Plays);

    /// <summary>A clip group as the game schedules it. Volumes are 0-100 and scale every clip of that list.</summary>
    public sealed record PlayGroup(MusClipGroup Source, int Tempo, int Beats, int MainVolume, int SubVolume,
                                   IReadOnlyList<PlayClip> Clips, IReadOnlyList<PlayClip> SubClips)
    {
        public double Seconds => Tempo > 0 ? Beats * 60.0 / Tempo : 1.0;
    }

    /// <summary>A legacy float volume (0.0-1.0) as the game converts it: (byte)(int)(v * 100).</summary>
    public static byte LegacyVolume(float volume) => (byte)Math.Clamp((int)(volume * 100.0), 0, 255);

    public static PlayGroup Group(byte version, MusClipGroup g)
    {
        if (MusSerializer.IsNgs(version)) return Ngs.Group(g);
        if (!MusSerializer.IsLegacy(version))
            return new PlayGroup(g, g.Tempo, g.Beats, g.UnkE, g.UnkF, Clips(version, g.Clips), Clips(version, g.SubClips));

        // Legacy: the group holds a layer per list. Its volume scales the list; only a layer of kind 4 has clips.
        var (mainVolume, mainClips) = Layer(version, g.Layer, g.Clips);
        var (subVolume, subClips) = Layer(version, g.SubLayer, g.SubClips);
        int beats = version < 8 ? g.Beats & 0xFF : g.Beats; // v0x07: the game reads only the low byte of beats
        return new PlayGroup(g, g.Tempo, beats, mainVolume, subVolume, mainClips, subClips);
    }

    static (int volume, IReadOnlyList<PlayClip> clips) Layer(byte version, MusLayer? layer, List<MusClip> clips)
    {
        if (layer == null) return (100, []);
        bool v07 = version < 8;
        int volume = v07 ? LegacyVolume(BitConverter.ToSingle(layer.Data, 0)) : layer.Data[0];
        int kind = v07 ? layer.Data[4] : layer.Data[1];
        return (volume, kind == 4 ? Clips(version, clips) : []);
    }

    static List<PlayClip> Clips(byte version, List<MusClip> clips) => clips.Select((c, i) => Clip(version, c, i)).ToList();

    /// <param name="position">The clip's index in its list; legacy clips without flag bit 0 use it as their condition.</param>
    public static PlayClip Clip(byte version, MusClip c, int position)
    {
        if (!MusSerializer.IsLegacy(version))
            return new PlayClip(c, c.File, c.Volume, c.Condition, c.File.Length > 0);

        int volume = version < 8 ? LegacyVolume(c.VolumeF ?? c.Volume / 100f) : c.Volume;
        int condition = (c.Unk3 & 1) != 0 ? c.Condition : position;
        return new PlayClip(c, c.File, volume, condition, c.Unk2 == 0 && c.File.Length > 0);
    }

    /// <summary>
    /// The song's flags (game: u32 at +8 of the normalized root). Bits seen in the game's battle-state check:
    /// 0x4 battle states apply in every part (else only in the part with index 1), 0x20 battle state always
    /// Normal, 0x400 no Hero, 0x800 no Pinch, 0x1000 no Pse.
    /// </summary>
    public static uint Flags(MusFile mus) => mus.Version switch
    {
        _ when MusSerializer.IsNgs(mus.Version) => 0, // NGS flags are not known; no classic flag rule applies
        < 9 => (uint)(mus.RootUnk06 & 0xFF),
        < 0x0B => mus.RootUnk06,
        _ => mus.RootUnk08,
    };

    /// <summary>The game's battle music state (the value FUN_00548310 passes to FUN_0056d490).</summary>
    public enum BattleState { Normal = 0, Hero = 1, Pinch = 2, Pse = 3, HeroPse = 4, PinchPse = 5 }

    public static readonly string[] BattleConditions = ["Normal", "Hero", "Pinch", "Pse"];

    /// <summary>Normal, Hero, Pinch and Pse are not toggled: the battle state decides them.</summary>
    public static bool IsBattleCondition(string name) => BattleConditions.Contains(name);

    /// <summary>
    /// First..Sixth: the game turns exactly one of these on, picked by a number the stage sets on the music
    /// object (FUN_00565c60 sets state "First".."Sixth" to index == value). They are one selector, not six toggles.
    /// </summary>
    public static readonly string[] VariantConditions = ["First", "Second", "Third", "Fourth", "Fifth", "Sixth"];

    public static int VariantIndex(string name) => Array.IndexOf(VariantConditions, name);

    public const uint FlagAllParts = 0x4, FlagAlwaysNormal = 0x20, FlagNoHero = 0x400, FlagNoPinch = 0x800, FlagNoPse = 0x1000;

    /// <summary>The state after the song's flags remove what it does not use (0x20: always Normal; 0x400/0x800/0x1000: no Hero/Pinch/Pse).</summary>
    public static BattleState EffectiveState(BattleState state, uint flags)
    {
        if ((flags & FlagAlwaysNormal) != 0) return BattleState.Normal;
        if ((flags & FlagNoHero) != 0) state = state switch { BattleState.HeroPse => BattleState.Pse, BattleState.Hero => BattleState.Normal, _ => state };
        if ((flags & FlagNoPinch) != 0) state = state switch { BattleState.PinchPse => BattleState.Pse, BattleState.Pinch => BattleState.Normal, _ => state };
        if ((flags & FlagNoPse) != 0) state = state switch
        {
            BattleState.HeroPse => BattleState.Hero, BattleState.PinchPse => BattleState.Pinch, BattleState.Pse => BattleState.Normal, _ => state,
        };
        return state;
    }

    /// <summary>
    /// Whether the battle state lets a condition's clips play (game: FUN_0056d490). The result gates every
    /// condition; Normal/Hero/Pinch/Pse are on exactly when this is true, other conditions also need their own state.
    /// <list type="bullet">
    /// <item>Normal state: Normal on, Hero/Pinch/Pse off, everything else allowed, in every part.</item>
    /// <item>Other states only change the battle part (index 1, or every part with flag 0x4). Elsewhere
    ///   Normal/Hero/Pinch/Pse are all off and everything else is allowed.</item>
    /// <item>In the battle part only these play: Hero → main-list Hero clips (sub list: non-battle conditions);
    ///   Pinch → sub-list Pinch clips, main list silent; Pse → sub-list Pse clips (main list: non-battle conditions);
    ///   Hero+Pse → main Hero and sub Pse; Pinch+Pse → sub Pinch or Pse, main list silent.</item>
    /// </list>
    /// </summary>
    public static bool BattleAllows(string name, BattleState state, bool sub, MusPart part, uint flags)
    {
        bool other = !IsBattleCondition(name);
        state = EffectiveState(state, flags);
        if (state == BattleState.Normal) return other || name == "Normal";
        if ((flags & FlagAllParts) == 0 && part.Index != 1) return other;
        return state switch
        {
            BattleState.Hero => sub ? other : name == "Hero",
            BattleState.Pinch => sub && name == "Pinch",
            BattleState.Pse => sub ? name == "Pse" : other,
            BattleState.HeroPse => name == (sub ? "Pse" : "Hero"),
            _ => sub && name is "Pse" or "Pinch",
        };
    }

    /// <summary>
    /// The gain a clip gets while its condition is not met: the condition's first field read as a float. The game
    /// uses 1.0 when the condition is met and this value otherwise; it is 0.0 in every shipped file (= silent).
    /// </summary>
    public static float OffGain(MusCondition c) => BitConverter.Int32BitsToSingle(unchecked((int)c.Value)); // NGS conditions have no Value: 0

    /// <summary>
    /// Whether the game uses the root's one-shot list. It reads it from the normalized root, and when converting a
    /// v0x09/0x0A root (FUN_0056df40) it zeroes that list: only versions above 0x0A have one-shots in game.
    /// </summary>
    public static bool UsesOneShots(byte version) => version > 0x0A && !MusSerializer.IsNgs(version);

    /// <summary>
    /// NGS (win32reboot, 0x0F and later) playback, kept apart from the classic rules above: those were read from the
    /// classic pso2.exe, and the NGS client could not be read (packed). So NGS gets only what the files show:
    /// clips play by condition toggles and random groups; no battle state, no First..Sixth selector, no flags,
    /// no legacy kinds, no one-shot gating. Change NGS behaviour here only.
    /// </summary>
    public static class Ngs
    {
        /// <summary>
        /// The group's first 16 bytes are laid out like a classic flat group. UnkE/UnkF are taken as the clip and
        /// sub-clip list volumes like there (100 in the files seen); unconfirmed for NGS.
        /// </summary>
        public static PlayGroup Group(MusClipGroup g) =>
            new(g, g.Tempo, g.Beats, g.UnkE, g.UnkF, Clips(g.Clips), Clips(g.SubClips));

        /// <summary>
        /// NGS: a bar's clip groups play one after another, each for its own Beats*60/Tempo, instead of one being
        /// picked at random as for classic. Taken from the NGS files: wherever a bar holds several groups with clips,
        /// they continue the clip numbering (defense_1_1_md section 8: bar 25 = _025 then _026). The NGS client is
        /// packed, so this could not be confirmed against its code.
        /// </summary>
        public static bool GroupsInSequence(byte version) => MusSerializer.IsNgs(version);

        static List<PlayClip> Clips(List<MusClip> clips) =>
            clips.Select(c => new PlayClip(c, c.File, c.Volume, c.Condition, c.File.Length > 0)).ToList();
    }

    /// <summary>Clips quieter than this are not started (game: 1/65536).</summary>
    public const float MinimumGain = 1f / 65536;
}
