using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Photino.NET;

namespace MusTool;

/// <summary>
/// Graphical player (MusTool ui). The page (ui/index.html, embedded) talks to this host with JSON messages:
/// page -> host { type: "ready" | "browseData" | "open" | "openFile" | "switch" | "transport" | "condition" |
///                       "battle" | "variant" | "randomPick" | "volume" | "oneShot", ... }
/// host -> page { type: "init" | "song" | "status" | "slots" | "log" | "error", ... }
/// </summary>
public sealed class UiHost : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly PhotinoWindow window;
    readonly object audioLock = new();
    readonly SwitchableProvider provider = new();
    WaveOutEvent? output;
    ClipLibrary? library;
    MusEngine? engine;
    MusFile? mus;
    int versionScan; // bumped when the data folder changes; stops an older background version read
    float volume = 0.8f;
    Timer? statusTimer;

    /// <summary>MUSTOOL_UI_TRACE=1 prints page messages, slots and errors to stderr.</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("MUSTOOL_UI_TRACE") == "1";

    readonly string? startSong;

    /// <param name="song">Optional song (name, ice name or path) to open when the window is ready.</param>
    public static int Run(string? song = null)
    {
        int result = 0;
        // WebView2 needs a single-threaded apartment; top-level Main is MTA.
        var thread = new Thread(() =>
        {
            using var host = new UiHost(song);
            host.window.WaitForClose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    UiHost(string? song)
    {
        startSong = song;
        window = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTitle("MusTool")
            .SetUseOsDefaultSize(false)
            .SetSize(1400, 860)
            .SetMinSize(980, 600)
            .Center()
            .SetResizable(true)
            .SetContextMenuEnabled(false)
            .SetTemporaryFilesPath(Path.Combine(Path.GetTempPath(), "MusTool.WebView2"))
            .RegisterWebMessageReceivedHandler((_, message) => OnMessage(message))
            .LoadRawString(LoadPage());
        statusTimer = new Timer(_ => PushStatus(), null, 250, 100);
    }

    static string LoadPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MusTool.ui.index.html")
                      ?? throw new InvalidOperationException("ui/index.html is not embedded");
        return new StreamReader(s).ReadToEnd();
    }

    void Send(object message)
    {
        string text = JsonSerializer.Serialize(message, Json);
        if (Trace && !text.StartsWith("{\"type\":\"status\"")) Console.Error.WriteLine($"ui -> {(text.Length > 200 ? text[..200] + "..." : text)}");
        try { window.Invoke(() => window.SendWebMessage(text)); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { } // window closing
    }

    void OnMessage(string text)
    {
        JsonNode? m = null;
        try
        {
            m = JsonNode.Parse(text);
            if (Trace) Console.Error.WriteLine($"ui <- {text}");
            switch ((string?)m?["type"])
            {
                case "ready":
                    SendInit();
                    if (startSong != null) OnMessage(JsonSerializer.Serialize(new { type = "open", song = startSong }));
                    break;
                case "jsError": Console.Error.WriteLine($"ui script error: {(string?)m!["message"]}"); break;
                case "browseData": BrowseData(); break;
                case "open":
                {
                    // Loading can scan other songs' CPKs for shared clips; keep the window responsive.
                    string song = (string)m!["song"]!;
                    Task.Run(() =>
                    {
                        try { OpenSong(song); }
                        catch (Exception e) { Send(new { type = "error", message = e.Message, request = "open" }); }
                    });
                    break;
                }
                case "openFile": OpenFile(); break;
                case "switch":
                    engine?.RequestSwitch((int)m!["part"]!, (int)m["section"]!, (bool?)m["now"] ?? false);
                    break;
                case "transport": Transport((string)m!["action"]!, m); break;
                case "condition":
                    if (engine != null)
                        lock (engine.EnabledConditions)
                        {
                            string name = (string)m!["name"]!;
                            if ((bool)m["on"]!) engine.EnabledConditions.Add(name); else engine.EnabledConditions.Remove(name);
                        }
                    break;
                case "variant":
                    if (engine != null) engine.Variant = Math.Clamp((int)m!["index"]!, 0, 5);
                    break;
                case "battle":
                    if (engine != null) engine.Battle = (MusPlayback.BattleState)(int)m!["state"]!;
                    break;
                case "randomPick":
                {
                    int entry = (int)m!["entry"]!;
                    engine?.PinRandom((int)m["part"]!, (bool)m["sub"]!, (string)m["name"]!, entry < 0 ? null : entry);
                    break;
                }
                case "clipVolume": if (engine != null) engine.ApplyClipVolume = (bool)m!["on"]!; break;
                case "volume":
                    volume = Math.Clamp((float)(double)m!["value"]!, 0f, 1f);
                    lock (audioLock) if (output != null) output.Volume = volume;
                    break;
                case "oneShot":
                    if (engine != null && mus != null && (int)m!["index"]! is var i && i >= 0 && i < mus.OneShots.Count)
                        Task.Run(() => { if (!engine.PlayOneShot(mus.OneShots[i])) Log($"cannot play {mus.OneShots[i].File}"); });
                    break;
            }
        }
        catch (Exception e)
        {
            Send(new { type = "error", message = e.Message, request = (string?)m?["type"] });
            if (Trace) Console.Error.WriteLine($"ui error: {e}");
        }
    }

    void SendInit()
    {
        string? dir = GameData.DataFolder;
        Send(new
        {
            type = "init",
            dataFolder = dir,
            // kind: "classic" (win32) or "ngs" (win32reboot); the page lists one kind at a time.
            songs = GameData.KnownSongs.Select(s => (s, kind: "classic")).Concat(GameData.KnownNgsSongs.Select(s => (s, kind: "ngs")))
                .Select(x => new { name = x.s, x.kind, present = dir != null && GameData.FindIce(x.s, dir) != null }),
        });
        ReadVersions(dir);
    }

    /// <summary>
    /// Reads every present song's .mus version in the background (in memory, nothing written) and sends them in
    /// batches as { type: "versions", versions: { song: "0x0A" | "error" } } for the song list's version filter.
    /// </summary>
    void ReadVersions(string? dir)
    {
        int scan = Interlocked.Increment(ref versionScan);
        if (dir == null) return;
        var present = GameData.KnownSongs.Concat(GameData.KnownNgsSongs).Where(s => GameData.FindIce(s, dir) != null).ToList();
        Task.Run(() =>
        {
            var batch = new Dictionary<string, string>();
            foreach (var song in present)
            {
                if (scan != Volatile.Read(ref versionScan)) return;
                try { batch[song] = $"0x{MusSerializer.Read(GameData.LoadIce(GameData.FindIce(song, dir)!, song).MusBytes).Version:X2}"; }
                catch { batch[song] = "error"; }
                if (batch.Count >= 20) Flush();
            }
            Flush();

            void Flush()
            {
                if (batch.Count == 0 || scan != Volatile.Read(ref versionScan)) return;
                Send(new { type = "versions", versions = new Dictionary<string, string>(batch) });
                batch.Clear();
            }
        });
    }

    void BrowseData()
    {
        string? start = GameData.DataFolder is { } d && Directory.Exists(d) ? d : null;
        var picked = window.ShowOpenFolder("Select pso2_bin\\data", start, false);
        if (picked is not [var dir, ..] || string.IsNullOrEmpty(dir)) return;
        GameData.DataFolder = dir;
        SendInit();
    }

    void OpenFile()
    {
        var picked = window.ShowOpenFile("Open a .mus or ice file", GameData.DataFolder, false,
                                         [("Song files", ["*.mus", "*.ice", "*"]), ("All files", ["*"])]);
        if (picked is not [var path, ..] || string.IsNullOrEmpty(path)) return;
        bool isMus = path.EndsWith(".mus", StringComparison.OrdinalIgnoreCase);
        if (isMus)
        {
            var folders = window.ShowOpenFolder("Folder with the song's .hca clips (cancel: none)", Path.GetDirectoryName(path), false);
            Load(MusSerializer.Read(path), null, Path.GetFileNameWithoutExtension(path),
                 folders is [var f, ..] && !string.IsNullOrEmpty(f) ? [f] : []);
        }
        else OpenSong(path);
    }

    void OpenSong(string song)
    {
        Send(new { type = "loading", song });
        var (file, ice) = GameData.Open(song, ice: true);
        Load(file, ice, ice!.Song, null);
    }

    void Load(MusFile file, GameData.IceSong? ice, string title, List<string>? extraSources)
    {
        List<string> sources = extraSources ?? [];
        List<string> notFound = [];
        if (ice != null) sources = GameData.ClipSources(file, ice, out notFound);

        var newLibrary = new ClipLibrary(sources);
        var newEngine = new MusEngine(file, newLibrary, 0, 0);
        lock (audioLock)
        {
            output?.Stop();
            output?.Dispose();
            library?.Dispose();
            (mus, library, engine) = (file, newLibrary, newEngine);
            provider.Engine = newEngine;
            output = new WaveOutEvent { DesiredLatency = 150 };
            output.Init(new SampleToWaveProvider16(provider));
            output.Volume = volume;
        }

        Send(new
        {
            type = "song",
            title,
            version = $"0x{file.Version:X2}",
            cpks = sources.Select(s => new { song = GameData.SongForCpk(s), file = Path.GetFileName(s), own = s == ice?.CpkPath }),
            missingClips = notFound.Count,
            oneShotsUsed = MusPlayback.UsesOneShots(file.Version),
            oneShots = file.OneShots.Select(o => new { file = o.File, kind = o.Kind, condition = o.Condition?.Name }),
            linked = file.RootInfo?.Name,
            conditions = MusEngine.ToggleConditions(file),
            usesVariant = MusEngine.UsesVariant(file),
            // Normal/Hero/Pinch/Pse follow the battle state. "becomes" is what each state turns into under the song's flags.
            battle = MusEngine.UsesBattle(file) ? new
            {
                flags = $"0x{MusPlayback.Flags(file):X}",
                allParts = (MusPlayback.Flags(file) & MusPlayback.FlagAllParts) != 0,
                becomes = Enum.GetValues<MusPlayback.BattleState>().Select(st => (int)MusPlayback.EffectiveState(st, MusPlayback.Flags(file))),
            } : null,
            enabledConditions = MusEngine.DefaultConditions(file),
            // Random* alternatives; each entry is labelled with the first clip that uses it (e.g. A_N_R_M06_1_T065_B03).
            randomGroups = newEngine.RandomGroups.Select(g => new
            {
                part = g.Part,
                sub = g.Sub,
                name = g.Name,
                entries = g.Entries.Select(e => new { index = e, label = FirstClipFor(file.Version, file.Parts[g.Part], g.Sub, e) }),
            }),
            parts = file.Parts.Select((p, pi) => new
            {
                index = pi,
                clipConditions = p.ClipConditions.Select(c => c.Name),
                subClipConditions = p.SubClipConditions.Select(c => c.Name),
                sections = p.Sections.Select(s => new
                {
                    type = s.Type,
                    unk2 = s.Unk2,
                    unk3 = s.Unk3,
                    // As the game plays it (MusPlayback): resolved conditions and volumes, skipped records left out.
                    phrases = s.Phrases.Select(h => h.Bars.Select(b => b.Variations.Select(g => MusPlayback.Group(file.Version, g)).Select(g => new
                    {
                        tempo = g.Tempo,
                        beats = g.Beats,
                        clips = g.Clips.Where(c => c.Plays).Select(c => new { file = c.File, volume = c.Volume * g.MainVolume / 100, condition = c.Condition }),
                        subClips = g.SubClips.Where(c => c.Plays).Select(c => new { file = c.File, volume = c.Volume * g.SubVolume / 100, condition = c.Condition }),
                    }))),
                }),
            }),
        });
    }

    static string FirstClipFor(byte version, MusPart part, bool sub, int condition) =>
        part.Sections.SelectMany(s => s.Phrases).SelectMany(h => h.Bars).SelectMany(b => b.Variations)
            .Select(g => MusPlayback.Group(version, g)).SelectMany(g => sub ? g.SubClips : g.Clips)
            .FirstOrDefault(c => c.Condition == condition && c.Plays)?.File.Replace(".hca", "") ?? $"entry {condition}";

    void Transport(string action, JsonNode m)
    {
        lock (audioLock)
        {
            if (output == null || engine == null) return;
            switch (action)
            {
                case "play": output.Play(); break;
                case "pause": output.Pause(); break;
                case "toggle":
                    if (output.PlaybackState == PlaybackState.Playing) output.Pause(); else output.Play();
                    break;
                case "restart":
                    engine.Restart((int?)m["part"] ?? 0, (int?)m["section"] ?? 0);
                    output.Play();
                    break;
            }
        }
    }

    void Log(string text) => Send(new { type = "log", text });

    void PushStatus()
    {
        MusEngine? e;
        ClipLibrary? lib;
        MusFile? song;
        bool playing;
        lock (audioLock)
        {
            (e, lib, song) = (engine, library, mus);
            playing = output?.PlaybackState == PlaybackState.Playing;
        }
        if (e == null) return;

        var slots = new List<object>();
        while (e.Events.TryDequeue(out var ev))
        {
            slots.Add(new { part = ev.Part, section = ev.Section, phrase = ev.Phrase, bar = ev.Bar, switched = ev.Switched, clips = ev.Clips, time = e.TimeSeconds });
            if (Trace) Console.Error.WriteLine($"ui slot {e.TimeSeconds,7:0.00}s {(ev.Switched ? "->" : "  ")} P{ev.Part} S{ev.Section} {ev.Phrase + 1}:{ev.Bar + 1} {ev.Clips.FirstOrDefault()}");
        }
        var warnings = new List<string>();
        while (lib != null && lib.Warnings.TryDequeue(out var w)) warnings.Add(w);

        var p = e.Position;
        Send(new
        {
            type = "status",
            playing,
            time = p.Seconds,
            part = p.Part,
            section = p.Section,
            phrase = p.Phrase,
            phraseCount = p.PhraseCount,
            bar = p.Bar,
            barCount = p.BarCount,
            tempo = p.Tempo,
            beats = p.Beats,
            progress = p.SlotProgress,
            battle = (int)e.Battle,
            variant = e.Variant,
            // Per part: which condition entries play right now (battle state, toggles, random picks).
            met = song?.Parts.Select((pt, pi) => new
            {
                main = pt.ClipConditions.Select((_, i) => e.IsMet(pi, false, i)).ToArray(),
                sub = pt.SubClipConditions.Select((_, i) => e.IsMet(pi, true, i)).ToArray(),
            }).ToArray(),
            picks = e.RandomGroups.Select(g => new { part = g.Part, sub = g.Sub, name = g.Name, current = g.Current, pinned = g.Pinned }),
            pending = p.Pending is { } t ? new { part = t.part, section = t.section } : null,
            slots,
            warnings,
        });
    }

    public void Dispose()
    {
        statusTimer?.Dispose();
        lock (audioLock)
        {
            output?.Stop();
            output?.Dispose();
            library?.Dispose();
        }
    }

    /// <summary>Lets the output keep one device while songs are swapped underneath it.</summary>
    sealed class SwitchableProvider : ISampleProvider
    {
        public volatile MusEngine? Engine;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MusEngine.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            if (Engine is { } e) e.Render(buffer, offset, count / 2);
            else Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
