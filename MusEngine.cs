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
    long now;
    double nextSlotAt;
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
        foreach (var p in mus.Parts)
            foreach (var c in p.ClipConditions) EnabledConditions.Add(c.Name);
        PrefetchSlot(part, section, 0, 0);
    }

    /// <summary>Clip conditions (by name) whose clips are played. Default: all clip conditions on.</summary>
    public HashSet<string> EnabledConditions { get; } = new(StringComparer.Ordinal);

    /// <summary>Play ClipGroup.SubClips too (R_ tails, PSE_, pinch_ ...). Off by default.</summary>
    public bool PlaySubClips { get; set; }

    /// <summary>Apply MusClip.Volume (assumed 0-100) as gain.</summary>
    public bool ApplyClipVolume { get; set; } = true;

    public ConcurrentQueue<SlotInfo> Events { get; } = new();

    public double TimeSeconds => (double)Interlocked.Read(ref now) / SampleRate;

    public (int part, int section) Current { get { lock (gate) return (part, section); } }

    public (int part, int section)? Pending { get { lock (gate) return pending; } }

    /// <summary>Queue a change; it takes effect at the next slot boundary.</summary>
    public void RequestSwitch(int targetPart, int targetSection)
    {
        CheckTarget(targetPart, targetSection);
        lock (gate) pending = (targetPart, targetSection);
        PrefetchSlot(targetPart, targetSection, 0, 0);
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
            (part, section, phrase, bar) = (target.part, target.section, 0, 0);
            pending = null;
            switched = true;
        }
        else Advance();

        var sec = mus.Parts[part].Sections[section];
        var barList = sec.Phrases.Count > 0 ? sec.Phrases[phrase].Bars : [];
        var bar0 = barList.Count > 0 ? barList[bar] : null;
        var group = bar0 is { Variations.Count: > 0 } ? bar0.Variations[random.Next(bar0.Variations.Count)] : null;

        double seconds = group is { Tempo: > 0 } ? group.Beats * 60.0 / group.Tempo : 1.0;
        nextSlotAt += seconds * SampleRate;

        var started = new List<string>();
        if (group != null)
        {
            var p = mus.Parts[part];
            foreach (var clip in group.Clips.Where(c => ConditionOn(p.ClipConditions, c.Condition)))
                StartVoice(clip, started);
            if (PlaySubClips)
                foreach (var clip in group.SubClips)
                    StartVoice(clip, started);
        }

        Events.Enqueue(new SlotInfo(part, section, phrase, sec.Phrases.Count, bar, barList.Count,
                                    group, seconds, started, switched));
        PrefetchNext();
    }

    bool ConditionOn(List<MusCondition> conds, int index)
    {
        if (index >= conds.Count) return true;
        lock (EnabledConditions) return EnabledConditions.Contains(conds[index].Name);
    }

    void StartVoice(MusClip clip, List<string> started)
    {
        var samples = library.Get(clip.File);
        if (samples == null) return;
        voices.Add(new Voice(samples, ApplyClipVolume ? clip.Volume / 100f : 1f));
        started.Add(clip.File);
    }

    /// <summary>
    /// Moves to the next bar: bars, then phrases in order. At the end of a section, an intro
    /// (type 0) continues into the following section; every other section loops.
    /// </summary>
    void Advance()
    {
        var sections = mus.Parts[part].Sections;
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
                else if (sec.Phrases.All(h => h.Bars.Count == 0)) return; // empty section: stay put, slots are silent
            }
        }
    }

    void PrefetchNext()
    {
        if (pending is { } t) PrefetchSlot(t.part, t.section, 0, 0);
        var saved = (part, section, phrase, bar);
        Advance();
        PrefetchSlot(part, section, phrase, bar);
        (part, section, phrase, bar) = saved;
    }

    void PrefetchSlot(int p, int s, int h, int b)
    {
        var sec = mus.Parts[p].Sections[s];
        if (h >= sec.Phrases.Count || b >= sec.Phrases[h].Bars.Count) return;
        foreach (var g in sec.Phrases[h].Bars[b].Variations)
            foreach (var c in g.Clips.Concat(PlaySubClips ? g.SubClips : []))
                library.Prefetch(c.File);
    }
}
