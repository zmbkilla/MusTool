using System.Collections.Concurrent;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Hca;
using VGAudio.Formats.Pcm16;

namespace MusTool;

/// <summary>Finds clips by name in a list of sources (CPK archives or folders, first match wins) and decodes them.</summary>
public sealed class ClipLibrary : IDisposable
{
    /// <summary>HCA key used by PSO2 (cipher type 56).</summary>
    public const ulong Pso2HcaKey = 0xCC55463930DBE1AB;

    const int MaxCachedClips = 256;

    readonly List<Func<string, byte[]?>> sources = new();
    readonly List<IDisposable> owned = new();
    readonly ConcurrentDictionary<string, Lazy<Task<short[][]?>>> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<string> cacheOrder = new();
    readonly ulong key;

    public ClipLibrary(IEnumerable<string> paths, ulong key = Pso2HcaKey)
    {
        this.key = key;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                string dir = path;
                sources.Add(name => File.Exists(Path.Combine(dir, name)) ? File.ReadAllBytes(Path.Combine(dir, name)) : null);
            }
            else
            {
                var cpk = new CpkArchive(path);
                owned.Add(cpk);
                sources.Add(cpk.TryRead);
            }
        }
    }

    public ConcurrentQueue<string> Warnings { get; } = new();

    /// <summary>Starts decoding in the background if it isn't already cached.</summary>
    public Task<short[][]?> Prefetch(string name)
    {
        var lazy = cache.GetOrAdd(name, n =>
        {
            cacheOrder.Enqueue(n);
            return new Lazy<Task<short[][]?>>(() => Task.Run(() => Decode(n)));
        });
        while (cache.Count > MaxCachedClips && cacheOrder.TryDequeue(out var old) && old != name)
            cache.TryRemove(old, out _); // voices already playing keep their own reference to the samples
        return lazy.Value;
    }

    /// <summary>Blocks until the clip is decoded. Null if the clip is missing or fails to decode.</summary>
    public short[][]? Get(string name) => Prefetch(name).GetAwaiter().GetResult();

    short[][]? Decode(string name)
    {
        byte[]? data = null;
        foreach (var source in sources)
            if ((data = source(name)) != null) break;
        if (data == null)
        {
            Warnings.Enqueue($"clip not found: {name}");
            return null;
        }
        try
        {
            var pcm = new HcaReader { EncryptionKey = new CriHcaKey(key) }.Read(data).GetFormat<Pcm16Format>();
            var channels = pcm.Channels.Length == 1 ? [pcm.Channels[0], pcm.Channels[0]] : pcm.Channels;
            return pcm.SampleRate == MusEngine.SampleRate ? channels : Resample(channels, pcm.SampleRate);
        }
        catch (Exception e)
        {
            Warnings.Enqueue($"cannot decode {name}: {e.Message}");
            return null;
        }
    }

    static short[][] Resample(short[][] channels, int rate)
    {
        // Linear resampling; PSO2 clips are 48 kHz already, this only covers user-supplied files.
        double step = (double)rate / MusEngine.SampleRate;
        int length = (int)(channels[0].Length / step);
        return channels.Select(c =>
        {
            var o = new short[length];
            for (int i = 0; i < length; i++)
            {
                double x = i * step;
                int a = (int)x, b = Math.Min(a + 1, c.Length - 1);
                o[i] = (short)(c[a] + (c[b] - c[a]) * (x - a));
            }
            return o;
        }).ToArray();
    }

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
    }
}

/// <summary>Where the engine is, for display.</summary>
public sealed record PlaybackPosition(int Part, int Section, int Phrase, int PhraseCount, int Bar, int BarCount,
                                      int Tempo, int Beats, double SlotProgress, (int part, int section)? Pending,
                                      double Seconds);

/// <summary>Information about a slot that just started, for display.</summary>
public sealed record SlotInfo(int Part, int Section, int Phrase, int PhraseCount, int Bar, int BarCount,
                              MusClipGroup? Group, double Seconds, IReadOnlyList<string> Clips, bool Switched);

/// <summary>
/// Plays a .mus the way the game schedules it: every bar gets a slot of Beats*60/Tempo seconds, the bar's
/// clips start together at the slot start and keep ringing past its end (overlapping the next slot).
/// A requested section/part change is applied at the next slot boundary.
/// </summary>
public sealed class MusEngine
{
    public const int SampleRate = 48000;

    readonly MusFile mus;
    readonly ClipLibrary library;
    readonly object gate = new();
    readonly List<Voice> voices = new();
    readonly Random random = new();

    int part, section, phrase, bar = -1;
    int step; // NGS: which of the bar's clip groups is playing (they play in order; see MusPlayback.Ngs.GroupsInSequence)
    long now;
    double nextSlotAt, slotStartedAt;
    MusClipGroup? currentGroup;
    (int part, int section)? pending;

    sealed class Voice(short[][] samples, float gain)
    {
        public readonly short[][] Samples = samples;
        public readonly float Gain = gain;
        public int Position;
    }

    public MusEngine(MusFile mus, ClipLibrary library, int part, int section)
    {
        this.mus = mus;
        this.library = library;
        CheckTarget(part, section);
        this.part = part;
        this.section = section;
        EnabledConditions.UnionWith(DefaultConditions(mus));
        for (int pi = 0; pi < mus.Parts.Count; pi++)
            foreach (var (list, sub) in new[] { (mus.Parts[pi].ClipConditions, false), (mus.Parts[pi].SubClipConditions, true) })
                foreach (var g in list.Select((c, i) => (Key: RandomKey(c), i)).GroupBy(x => x.Key).Where(g => g.Key != null && g.Count() > 1))
                    randomGroups.Add(new RandomGroup(pi, sub, g.Key!, g.Select(x => x.i).ToList()));
        Reroll(part);
        PrefetchSlot(part, section, 0, 0);
    }

    static bool IsRandom(string name) => name.StartsWith("Random", StringComparison.Ordinal);

    /// <summary>
    /// Which random group a condition entry belongs to (null: none). Classic: entries sharing a Random* name.
    /// NGS: the condition's own extra bytes number its group (byte 1) and mode (byte 2); rg01_01night has four
    /// groups of three entries all named "Random", so grouping by name alone would merge them into one.
    /// </summary>
    string? RandomKey(MusCondition c)
    {
        if (!IsRandom(c.Name)) return null;
        if (MusSerializer.IsNgs(mus.Version) && c.Extra is { Length: >= 3 } x) return $"{c.Name} g{x[1]}.{x[2]}";
        return c.Name;
    }

    /// <summary>
    /// Condition entries of one part that share a Random* name (ancient: four sub-clip entries all named
    /// RandomMovement2). They are alternatives: one entry plays, picked again each time a section starts,
    /// unless <see cref="Pinned"/> fixes it.
    /// </summary>
    public sealed class RandomGroup(int part, bool sub, string name, IReadOnlyList<int> entries)
    {
        public int Part { get; } = part;
        public bool Sub { get; } = sub;
        public string Name { get; } = name;
        /// <summary>Condition indexes in the part's ClipConditions (or SubClipConditions when Sub).</summary>
        public IReadOnlyList<int> Entries { get; } = entries;
        public int Current { get; internal set; } = entries[0];
        public int? Pinned { get; internal set; }
    }

    readonly List<RandomGroup> randomGroups = new();

    public IReadOnlyList<RandomGroup> RandomGroups => randomGroups;

    /// <summary>Fixes a random group to one entry, or with null lets it pick at random again.</summary>
    public void PinRandom(int targetPart, bool sub, string name, int? entry)
    {
        lock (gate)
            foreach (var g in randomGroups.Where(g => g.Part == targetPart && g.Sub == sub && g.Name == name))
            {
                if (entry is { } e && !g.Entries.Contains(e)) throw new ArgumentOutOfRangeException(nameof(entry));
                g.Pinned = entry;
                if (entry is { } pick) g.Current = pick;
            }
    }

    void Reroll(int targetPart)
    {
        foreach (var g in randomGroups.Where(g => g.Part == targetPart))
            g.Current = g.Pinned ?? g.Entries[random.Next(g.Entries.Count)];
    }

    /// <summary>
    /// Conditions on at start, for clips and sub-clips alike. Conditions are game states and many are alternatives
    /// (aph_normal / aph_pinch, pk_win / pk_lose, male / female), so turning all on layers versions that never
    /// play together. On: "Always", names containing "normal", the RandomMovement/RandomPhrase layers (one entry
    /// of each plays, see <see cref="RandomGroups"/>); if that leaves only "Always", the first other condition.
    /// Off: pinch and other situational states. Normal/Hero/Pinch/Pse are left out: <see cref="Battle"/> drives them.
    /// </summary>
    public static HashSet<string> DefaultConditions(MusFile mus)
    {
        var names = ToggleConditions(mus);
        var on = names.Where(n => n == "Always"
                                  || n.Contains("normal", StringComparison.OrdinalIgnoreCase)
                                  || n.StartsWith("RandomMovement") || n.StartsWith("RandomPhrase"))
                      .ToHashSet(StringComparer.Ordinal);
        // NGS: Random layers are part of the music (one entry per numbered group plays), so they start on.
        if (MusSerializer.IsNgs(mus.Version)) on.UnionWith(names.Where(IsRandom));
        if (on.All(n => n == "Always") && names.FirstOrDefault(n => n != "Always") is { } first) on.Add(first);
        return on;
    }

    /// <summary>Every condition name of the song, clip conditions first, then sub-clip-only ones.</summary>
    public static List<string> ConditionNames(MusFile mus) =>
        mus.Parts.SelectMany(p => p.ClipConditions).Concat(mus.Parts.SelectMany(p => p.SubClipConditions))
           .Select(c => c.Name).Distinct().ToList();

    /// <summary><see cref="ConditionNames"/> without Normal/Hero/Pinch/Pse, which follow <see cref="Battle"/> instead.</summary>
    public static List<string> ToggleConditions(MusFile mus) => MusSerializer.IsNgs(mus.Version)
        ? ConditionNames(mus) // NGS: every condition is a plain toggle (see MusPlayback.Ngs)
        : ConditionNames(mus).Where(n => !MusPlayback.IsBattleCondition(n) && MusPlayback.VariantIndex(n) < 0).ToList();

    /// <summary>True when the song has First..Sixth clips, i.e. <see cref="Variant"/> changes what plays.</summary>
    public static bool UsesVariant(MusFile mus) =>
        !MusSerializer.IsNgs(mus.Version) && ConditionNames(mus).Any(n => MusPlayback.VariantIndex(n) >= 0);

    /// <summary>Which of First..Sixth is on (0-5); exactly one is, as in the game. Takes effect at the next slot.</summary>
    public int Variant { get; set; }

    /// <summary>True when the song has Normal/Hero/Pinch/Pse clips, i.e. the battle state changes what plays.</summary>
    public static bool UsesBattle(MusFile mus) =>
        !MusSerializer.IsNgs(mus.Version) && ConditionNames(mus).Any(MusPlayback.IsBattleCondition);

    /// <summary>
    /// The game's battle state. It turns Normal/Hero/Pinch/Pse on and off and can silence a list of the battle
    /// part (see <see cref="MusPlayback.BattleAllows"/>). Takes effect at the next slot.
    /// </summary>
    public MusPlayback.BattleState Battle { get; set; } = MusPlayback.BattleState.Normal;

    /// <summary>Whether the game would play clips of this condition entry now (state, toggles and random picks).</summary>
    public bool IsMet(int partIndex, bool sub, int index)
    {
        lock (gate) return Met(partIndex, sub, index);
    }

    /// <summary>
    /// Condition names (clip and sub-clip conditions alike) whose clips are played. Starts as
    /// <see cref="DefaultConditions"/>. Sub-clips follow these exactly like the main clips do.
    /// </summary>
    public HashSet<string> EnabledConditions { get; } = new(StringComparer.Ordinal);

    /// <summary>Apply MusClip.Volume (assumed 0-100) as gain.</summary>
    public bool ApplyClipVolume { get; set; } = true;

    public ConcurrentQueue<SlotInfo> Events { get; } = new();

    public double TimeSeconds => (double)Interlocked.Read(ref now) / SampleRate;

    public (int part, int section) Current { get { lock (gate) return (part, section); } }

    public (int part, int section)? Pending { get { lock (gate) return pending; } }

    /// <summary>
    /// Queue a change; it takes effect at the next slot boundary. With <paramref name="immediate"/> the
    /// current clips are cut and the target starts right away.
    /// </summary>
    public void RequestSwitch(int targetPart, int targetSection, bool immediate = false)
    {
        CheckTarget(targetPart, targetSection);
        PrefetchSlot(targetPart, targetSection, 0, 0);
        lock (gate)
        {
            pending = (targetPart, targetSection);
            if (immediate)
            {
                voices.Clear();
                nextSlotAt = now;
            }
        }
    }

    /// <summary>Stops every voice and starts over at the given section on the next render.</summary>
    public void Restart(int targetPart, int targetSection) => RequestSwitch(targetPart, targetSection, immediate: true);

    /// <summary>Snapshot for display: where playback is and how far into the current slot (0-1).</summary>
    public PlaybackPosition Position
    {
        get
        {
            lock (gate)
            {
                var sec = mus.Parts[part].Sections[section];
                int bars = phrase < sec.Phrases.Count ? sec.Phrases[phrase].Bars.Count : 0;
                double length = nextSlotAt - slotStartedAt;
                double progress = length > 0 ? Math.Clamp((now - slotStartedAt) / length, 0, 1) : 0;
                return new PlaybackPosition(part, section, phrase, sec.Phrases.Count, Math.Max(0, bar), bars,
                                            currentGroup?.Tempo ?? 0, currentGroup?.Beats ?? 0, progress, pending,
                                            (double)now / SampleRate);
            }
        }
    }

    void CheckTarget(int p, int s)
    {
        if (p < 0 || p >= mus.Parts.Count) throw new ArgumentOutOfRangeException(nameof(p), $"part {p} does not exist");
        if (s < 0 || s >= mus.Parts[p].Sections.Count) throw new ArgumentOutOfRangeException(nameof(s), $"section {s} does not exist in part {p}");
    }

    /// <summary>Mixes the next <paramref name="frames"/> stereo frames into <paramref name="buffer"/> (interleaved floats).</summary>
    public void Render(float[] buffer, int offset, int frames)
    {
        Array.Clear(buffer, offset, frames * 2);
        lock (gate)
        {
            int done = 0;
            while (done < frames)
            {
                if (now >= (long)Math.Round(nextSlotAt)) StartNextSlot();
                long untilSlot = (long)Math.Round(nextSlotAt) - now;
                int n = (int)Math.Min(frames - done, Math.Max(1, untilSlot));
                MixVoices(buffer, offset + done * 2, n);
                done += n;
                now += n;
            }
        }
    }

    void MixVoices(float[] buffer, int offset, int frames)
    {
        for (int v = voices.Count - 1; v >= 0; v--)
        {
            var voice = voices[v];
            var l = voice.Samples[0];
            var r = voice.Samples[1];
            int remaining = Math.Min(l.Length, r.Length) - voice.Position;
            int n = Math.Min(frames, Math.Max(0, remaining));
            float g = voice.Gain / 32768f;
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i * 2] += l[voice.Position + i] * g;
                buffer[offset + i * 2 + 1] += r[voice.Position + i] * g;
            }
            voice.Position += n;
            if (voice.Position >= l.Length) voices.RemoveAt(v);
        }
    }

    void StartNextSlot()
    {
        bool switched = false;
        if (pending is { } target)
        {
            (part, section, phrase, bar, step) = (target.part, target.section, 0, 0, 0);
            pending = null;
            switched = true;
        }
        else Advance();

        if (phrase == 0 && bar == 0 && step == 0) Reroll(part); // each pass through a section picks its random entries again
        var sec = mus.Parts[part].Sections[section];
        var barList = sec.Phrases.Count > 0 ? sec.Phrases[phrase].Bars : [];
        var bar0 = barList.Count > 0 ? barList[bar] : null;
        var group = bar0 is not { Variations.Count: > 0 } ? null
            : MusPlayback.Ngs.GroupsInSequence(mus.Version) ? bar0.Variations[Math.Min(step, bar0.Variations.Count - 1)]
            : bar0.Variations[random.Next(bar0.Variations.Count)];
        var play = group != null ? MusPlayback.Group(mus.Version, group) : null;

        double seconds = play?.Seconds ?? 1.0;
        slotStartedAt = now;
        if (nextSlotAt < now - 1) nextSlotAt = now; // only after an immediate switch; keeps fractional timing otherwise
        nextSlotAt += seconds * SampleRate;
        currentGroup = group;

        var started = new List<string>();
        if (play != null)
        {
            foreach (var clip in play.Clips) StartClip(clip, false, play.MainVolume, started);
            foreach (var clip in play.SubClips) StartClip(clip, true, play.SubVolume, started);
        }

        Events.Enqueue(new SlotInfo(part, section, phrase, sec.Phrases.Count, bar, barList.Count,
                                    group, seconds, started, switched));
        PrefetchNext();
    }

    /// <summary>
    /// The condition part of a clip's gain, as the game computes it: 1.0 when the condition is met, else the
    /// condition's own off-gain (0 in shipped files). Met: the battle state allows it, and it is Normal/Hero/Pinch/Pse
    /// or an enabled name, and for a random group the group's current pick.
    /// Null when the condition entry does not exist; the game skips such clips.
    /// </summary>
    float? ConditionGain(int partIndex, bool sub, int index)
    {
        var list = sub ? mus.Parts[partIndex].SubClipConditions : mus.Parts[partIndex].ClipConditions;
        if (index >= list.Count) return null;
        return Met(partIndex, sub, index) ? 1f : MusPlayback.OffGain(list[index]);
    }

    bool Met(int partIndex, bool sub, int index)
    {
        var p = mus.Parts[partIndex];
        var list = sub ? p.SubClipConditions : p.ClipConditions;
        if (index >= list.Count) return false;
        var cond = list[index];
        if (MusSerializer.IsNgs(mus.Version)) return NgsMet(partIndex, sub, index, cond);
        if (!MusPlayback.BattleAllows(cond.Name, Battle, sub, p, MusPlayback.Flags(mus))) return false;
        if (MusPlayback.VariantIndex(cond.Name) is var variant and >= 0)
        {
            if (variant != Variant) return false;
        }
        else if (!MusPlayback.IsBattleCondition(cond.Name))
            lock (EnabledConditions)
                if (!EnabledConditions.Contains(cond.Name)) return false;
        var g = randomGroups.Find(g => g.Part == partIndex && g.Sub == sub && g.Name == cond.Name);
        return g == null || g.Current == index;
    }

    /// <summary>NGS: an enabled condition, and for a random group its current pick. No classic rules.</summary>
    bool NgsMet(int partIndex, bool sub, int index, MusCondition cond)
    {
        lock (EnabledConditions)
            if (!EnabledConditions.Contains(cond.Name)) return false;
        // NGS groups are numbered in the file, so the entry's own group is the one containing its index.
        var g = randomGroups.Find(g => g.Part == partIndex && g.Sub == sub && g.Entries.Contains(index));
        return g == null || g.Current == index;
    }

    /// <summary>Game gain: condition gain x list volume x clip volume; clips below the game's minimum are not started.</summary>
    void StartClip(MusPlayback.PlayClip clip, bool sub, int listVolume, List<string> started)
    {
        if (!clip.Plays || ConditionGain(part, sub, clip.Condition) is not { } conditionGain) return;
        float gain = ApplyClipVolume ? conditionGain * listVolume / 100f * clip.Volume / 100f : conditionGain;
        if (gain < MusPlayback.MinimumGain) return;
        var samples = library.Get(clip.File);
        if (samples == null) return;
        voices.Add(new Voice(samples, gain));
        started.Add(clip.File);
    }

    /// <summary>Plays one of the song's event sounds (MusFile.OneShots) right away, on top of the music.</summary>
    public bool PlayOneShot(MusOneShot shot)
    {
        var samples = library.Get(shot.File);
        if (samples == null) return false;
        lock (gate) voices.Add(new Voice(samples, ApplyClipVolume ? Math.Clamp(shot.Volume / 100f, 0f, 1f) : 1f));
        return true;
    }

    /// <summary>
    /// Moves to the next bar: bars, then phrases in order. At the end of a section, an intro
    /// (type 0) continues into the following section; every other section loops.
    /// </summary>
    void Advance()
    {
        var sections = mus.Parts[part].Sections;
        // NGS: the bar's next clip group gets the next slot before the bar advances.
        if (MusPlayback.Ngs.GroupsInSequence(mus.Version) && bar >= 0 && phrase < sections[section].Phrases.Count
            && bar < sections[section].Phrases[phrase].Bars.Count
            && step + 1 < sections[section].Phrases[phrase].Bars[bar].Variations.Count)
        {
            step++;
            return;
        }
        step = 0;
        bar++;
        for (int guard = 0; guard < 4096; guard++)
        {
            var sec = sections[section];
            if (phrase < sec.Phrases.Count && bar < sec.Phrases[phrase].Bars.Count) return;
            bar = 0;
            phrase++;
            if (phrase >= sec.Phrases.Count)
            {
                phrase = 0;
                if (sec.Type == 0 && section + 1 < sections.Count) section++;
                else if (sec.Type == 1)
                {
                    // Main sections play in order like the intro; after the last one, back to the first.
                    int next = section + 1 < sections.Count && sections[section + 1].Type == 1 ? section + 1
                        : sections.FindIndex(s => s.Type == 1);
                    if (next >= 0) section = next;
                }
                else if (sec.Phrases.All(h => h.Bars.Count == 0)) return; // empty section: stay put, slots are silent
            }
        }
    }

    void PrefetchNext()
    {
        if (pending is { } t) PrefetchSlot(t.part, t.section, 0, 0);
        var saved = (part, section, phrase, bar, step);
        Advance();
        PrefetchSlot(part, section, phrase, bar);
        (part, section, phrase, bar, step) = saved;
    }

    void PrefetchSlot(int p, int s, int h, int b)
    {
        var sec = mus.Parts[p].Sections[s];
        if (h >= sec.Phrases.Count || b >= sec.Phrases[h].Bars.Count) return;
        foreach (var g in sec.Phrases[h].Bars[b].Variations)
            foreach (var c in MusPlayback.Group(mus.Version, g) is var play ? play.Clips.Concat(play.SubClips).Where(c => c.Plays) : [])
                library.Prefetch(c.File);
    }
}
