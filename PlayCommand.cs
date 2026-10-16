using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Globalization;

namespace MusTool;

public static class PlayCommand
{
    public const string Usage =
        "  play     <file.mus> [cpk|folder]... [options]  play the song; switch sections live\n" +
        "             --part N --section N     where to start (default 0 0)\n" +
        "             --subclips               also play sub-clips (R_ tails, PSE_, pinch_)\n" +
        "             --no-volume              ignore per-clip volume\n" +
        "             --key HEX                HCA key (default PSO2 CC55463930DBE1AB)\n" +
        "             --out f.wav --seconds S  render to a WAV instead of playing\n" +
        "             --switch T:S | T:P.S     (with --out) request a switch at T seconds\n" +
        "             --ice                    <file.mus> is an ice / song name; its CPK is found automatically";

    public static int Run(string[] args)
    {

        string musPath = args[0];
        var sources = new List<string>();
        int part = 0, section = 0;
        string? outPath = null;
        double seconds = 60;
        bool subClips = false, noVolume = false;
        ulong key = ClipLibrary.Pso2HcaKey;
        var switches = new List<(double at, int part, int section)>();
        bool ice = false;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--part": part = int.Parse(Next()); break;
                case "--section": section = int.Parse(Next()); break;
                case "--out": outPath = Next(); break;
                case "--seconds": seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--subclips": subClips = true; break;
                case "--no-volume": noVolume = true; break;
                case "--key": key = ulong.Parse(Next().Replace("0x", ""), NumberStyles.HexNumber); break;
                case "--switch":
                {
                    var (t, target) = (Next().Split(':') is [var x, var y] ? (x, y) : throw new ArgumentException("--switch expects T:S or T:P.S"));
                    var ps = target.Split('.');
                    switches.Add((double.Parse(t, CultureInfo.InvariantCulture),
                                  ps.Length == 2 ? int.Parse(ps[0]) : -1,
                                  int.Parse(ps[^1])));
                    break;
                }
                case "--ice": ice = true; break;
                default:
                    if (a.StartsWith("--")) throw new ArgumentException($"unknown option {a}");
                    sources.Add(a);
                    break;
            }
        }
        var (mus, song) = GameData.Open(musPath, ice);
        // The song's own CPK (found next to the ice by its hashed name) goes first; extra sources are fallbacks.
        if (song?.CpkPath is { } cpk) sources.Insert(0, cpk);
        if (sources.Count == 0) throw new ArgumentException("play needs at least one CPK or folder with the .hca clips");
        using var library = new ClipLibrary(sources, key);
        var engine = new MusEngine(mus, library, part, section) { PlaySubClips = subClips, ApplyClipVolume = !noVolume };

        return outPath != null
            ? Render(engine, library, outPath, seconds, switches)
            : Interactive(mus, engine, library);
    }

    static int Render(MusEngine engine, ClipLibrary library, string outPath, double seconds,
                      List<(double at, int part, int section)> switches)
    {
        const int chunk = 1024;
        var floats = new float[chunk * 2];
        var pcm = new byte[chunk * 4];
        long total = (long)(seconds * MusEngine.SampleRate);
        var queue = new Queue<(double at, int part, int section)>(switches.OrderBy(s => s.at));

        using var writer = new WaveFileWriter(outPath, new WaveFormat(MusEngine.SampleRate, 16, 2));
        for (long done = 0; done < total; done += chunk)
        {
            while (queue.Count > 0 && queue.Peek().at <= engine.TimeSeconds)
            {
                var s = queue.Dequeue();
                int p = s.part >= 0 ? s.part : engine.Current.part;
                engine.RequestSwitch(p, s.section);
                Console.WriteLine($"{engine.TimeSeconds,8:0.000}s  request switch -> P{p} S{s.section}");
            }
            int n = (int)Math.Min(chunk, total - done);
            engine.Render(floats, 0, n);
            for (int i = 0; i < n * 2; i++)
            {
                short v = (short)Math.Clamp(floats[i] * 32767f, short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)v;
                pcm[i * 2 + 1] = (byte)(v >> 8);
            }
            writer.Write(pcm, 0, n * 4);
            Drain(engine, library);
        }
        Console.WriteLine($"Wrote {outPath} ({seconds:0.#} s)");
        return 0;
    }

    static int Interactive(MusFile mus, MusEngine engine, ClipLibrary library)
    {
        if (Console.IsInputRedirected)
            throw new ArgumentException("interactive playback needs a console; use --out to render a WAV");

        var conditions = mus.Parts.SelectMany(p => p.ClipConditions).Select(c => c.Name).Distinct().ToList();
        var (selPart, selSection) = engine.Current;

        PrintSections(mus, selPart, selSection);
        Console.WriteLine("Keys: Up/Down select section, Left/Right select part, Enter switch (at next slot boundary),");
        Console.WriteLine("      1-9 toggle conditions, S sub-clips, Space pause, Q quit");
        PrintConditions(engine, conditions);

        using var output = new WaveOutEvent { DesiredLatency = 150 };

        output.Init(new SampleToWaveProvider16(new EngineProvider(engine)));
        output.Play();

        while (true)
        {
            Drain(engine, library);
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(20);
                continue;
            }

            var k = Console.ReadKey(intercept: true);
            switch (k.Key)
            {
                case ConsoleKey.Q or ConsoleKey.Escape:
                    output.Stop();
                    return 0;
                case ConsoleKey.Spacebar:
                    if (output.PlaybackState == PlaybackState.Playing) { output.Pause(); Console.WriteLine("  [paused]"); }
                    else { output.Play(); Console.WriteLine("  [playing]"); }
                    break;
                case ConsoleKey.UpArrow or ConsoleKey.DownArrow:
                    int count = mus.Parts[selPart].Sections.Count;
                    selSection = (selSection + (k.Key == ConsoleKey.UpArrow ? count - 1 : 1)) % count;
                    Console.WriteLine($"  selected {Describe(mus, selPart, selSection)}");
                    break;
                case ConsoleKey.LeftArrow or ConsoleKey.RightArrow:
                    selPart = (selPart + (k.Key == ConsoleKey.LeftArrow ? mus.Parts.Count - 1 : 1)) % mus.Parts.Count;
                    selSection = Math.Min(selSection, mus.Parts[selPart].Sections.Count - 1);
                    PrintSections(mus, selPart, selSection);
                    break;
                case ConsoleKey.Enter:
                    engine.RequestSwitch(selPart, selSection);
                    Console.WriteLine($"  queued P{selPart} S{selSection}, switching at the next slot boundary");
                    break;
                case ConsoleKey.S:
                    engine.PlaySubClips = !engine.PlaySubClips;
                    Console.WriteLine($"  sub-clips {(engine.PlaySubClips ? "on" : "off")}");
                    break;
                case >= ConsoleKey.D1 and <= ConsoleKey.D9 when k.Key - ConsoleKey.D1 < conditions.Count:
                    string name = conditions[k.Key - ConsoleKey.D1];
                    lock (engine.EnabledConditions)
                        if (!engine.EnabledConditions.Remove(name)) engine.EnabledConditions.Add(name);
                    PrintConditions(engine, conditions);
                    break;
            }
        }
    }

    static void Drain(MusEngine engine, ClipLibrary library)
    {
        while (library.Warnings.TryDequeue(out var w)) Console.WriteLine($"  warning: {w}");
        while (engine.Events.TryDequeue(out var e))
        {
            string clips = e.Clips.Count switch
            {
                0 => "(silent)",
                1 => e.Clips[0],
                _ => $"{e.Clips[0]} +{e.Clips.Count - 1} layer(s)",
            };
            string timing = e.Group is { } g ? $"T{g.Tempo} B{g.Beats} {e.Seconds,5:0.00}s" : "(empty)";
            Console.WriteLine($"{(e.Switched ? "->" : "  ")}{engine.TimeSeconds,8:0.0}s  P{e.Part} S{e.Section}  " +
                              $"phrase {e.Phrase + 1}/{e.PhraseCount}  bar {e.Bar + 1,2}/{e.BarCount,-2}  {timing}  {clips}");
        }
    }

    static void PrintSections(MusFile mus, int part, int selected)
    {
        Console.WriteLine($"Part {part}:");
        for (int s = 0; s < mus.Parts[part].Sections.Count; s++)
            Console.WriteLine($"  {(s == selected ? ">" : " ")} {Describe(mus, part, s)}");
    }

    static string Describe(MusFile mus, int part, int s)
    {
        var sec = mus.Parts[part].Sections[s];
        var first = sec.Phrases.SelectMany(h => h.Bars).SelectMany(b => b.Variations).SelectMany(g => g.Clips).FirstOrDefault();
        return $"P{part} S{s} type {sec.Type}, {sec.Phrases.Count} phrase(s), {sec.Phrases.Sum(h => h.Bars.Count)} bars, {first?.File ?? "(silent)"}";
    }

    static void PrintConditions(MusEngine engine, List<string> conditions)
    {
        lock (engine.EnabledConditions)
            Console.WriteLine("  conditions: " + string.Join("  ", conditions.Select((c, i) =>
                $"{i + 1}:{c}[{(engine.EnabledConditions.Contains(c) ? "on" : "off")}]")));
    }

    sealed class EngineProvider(MusEngine engine) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MusEngine.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            engine.Render(buffer, offset, count / 2);
            return count;
        }
    }
}
