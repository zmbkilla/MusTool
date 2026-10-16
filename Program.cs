using System.Text.Json;
using MusTool;

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

if (args.Length == 0) return Usage();

// --ice works with every command that takes a .mus: the argument is then an ice file, a hashed name in the
// data folder, or a song name such as mu_11_bgm_dragonland (resolved against the folder set with "datadir").
bool ice = args.Contains("--ice", StringComparer.OrdinalIgnoreCase);
if (ice && args[0].ToLowerInvariant() != "play")
    args = args.Where(a => !a.Equals("--ice", StringComparison.OrdinalIgnoreCase)).ToArray();
MusFile Open(string arg) => GameData.Open(arg, ice).mus;

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "info" when args.Length == 2:
            PrintInfo(Open(args[1]));
            return 0;

        case "clips" when args.Length == 2:
            foreach (var name in AllClips(Open(args[1])).Select(c => c.File).Distinct().Order())
                Console.WriteLine(name);
            return 0;

        case "export" when args.Length is 2 or 3:
        {
            var mus = Open(args[1]);
            string outPath = args.Length == 3 ? args[2]
                : ice ? (mus.Extension?.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(args[1])) + ".json"
                : Path.ChangeExtension(args[1], ".json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(mus, json));
            Console.WriteLine($"Exported {outPath}");
            return 0;
        }

        case "import" when args.Length == 3:
        {
            var mus = JsonSerializer.Deserialize<MusFile>(File.ReadAllText(args[1]), json)
                      ?? throw new InvalidDataException("Empty JSON.");
            Validate(mus);
            MusSerializer.Write(mus, args[2]);
            Console.WriteLine($"Wrote {args[2]}");
            return 0;
        }

        case "rename" when args.Length == 5:
        {
            // rename <in.mus> <out.mus> <find> <replace> : substring replace in every clip file name
            var mus = Open(args[1]);
            int n = 0;
            foreach (var clip in AllClips(mus).Where(c => c.File.Contains(args[3], StringComparison.Ordinal)))
            {
                clip.File = clip.File.Replace(args[3], args[4], StringComparison.Ordinal);
                n++;
            }
            MusSerializer.Write(mus, args[2]);
            Console.WriteLine($"Renamed {n} clip references, wrote {args[2]}");
            return 0;
        }

        case "play" when args.Length >= 2:
            return PlayCommand.Run(args[1..]);

        case "template" when args.Length == 2:
            File.WriteAllText(args[1], JsonSerializer.Serialize(Template(), json));
            Console.WriteLine($"Wrote {args[1]}");
            return 0;

        case "verify" when args.Length == 2:
        {
            byte[] original = ice ? GameData.LoadIce(GameData.ResolveIce(args[1])).MusBytes : File.ReadAllBytes(args[1]);
            var mus = MusSerializer.Read(original);
            byte[] rebuilt = MusSerializer.Write(mus);
            // Also round-trip through JSON so the exported form is known to be lossless.
            var viaJson = JsonSerializer.Deserialize<MusFile>(JsonSerializer.Serialize(mus, json), json)!;
            byte[] rebuiltJson = MusSerializer.Write(viaJson);

            int diff = FirstDifference(original, rebuilt);
            Console.WriteLine(diff < 0
                ? $"OK: rewrite is byte-identical ({original.Length} bytes)"
                : $"MISMATCH at 0x{diff:X} (original {original.Length} bytes, rebuilt {rebuilt.Length} bytes)");
            Console.WriteLine(rebuilt.AsSpan().SequenceEqual(rebuiltJson)
                ? "OK: JSON round-trip is lossless"
                : "MISMATCH: JSON round-trip differs");
            return diff < 0 ? 0 : 1;
        }

        case "datadir" when args.Length == 1:
            Console.WriteLine(GameData.DataFolder ?? "(not set)");
            return 0;

        case "datadir" when args.Length == 2:
        {
            string dir = Path.GetFullPath(args[1]);
            if (!Directory.Exists(dir)) throw new ArgumentException($"{dir} does not exist");
            GameData.DataFolder = dir;
            int found = GameData.KnownSongs.Count(s => File.Exists(Path.Combine(dir, GameData.IceHash(s))));
            Console.WriteLine($"Data folder set to {dir} ({found}/{GameData.KnownSongs.Count} known songs found)");
            return 0;
        }

        case "songs" when args.Length == 1:
        {
            string? dir = GameData.DataFolder;
            foreach (var s in GameData.KnownSongs)
            {
                bool has = dir != null && File.Exists(Path.Combine(dir, GameData.IceHash(s)));
                Console.WriteLine($"{(dir == null ? "" : has ? "  " : "- ")}{s,-32} {GameData.IceHash(s)}");
            }
            return 0;
        }

        case "scan" when args.Length is 1 or 2:
            return Scan(args.Length == 2 ? args[1] : null);

        case "dump" when args.Length is 2 or 4:
        {
            // dump <song> [relOffset len]: hex view of the REL0 data (printed only), for format research.
            byte[] d = ice ? GameData.LoadIce(GameData.ResolveIce(args[1])).MusBytes : File.ReadAllBytes(args[1]);
            int rel = BitConverter.ToInt32(d, 0x0C), nof = BitConverter.ToInt32(d, 0x14);
            int rootHeader = BitConverter.ToInt32(d, rel + 8), root = BitConverter.ToInt32(d, rel + rootHeader);
            var ptrs = Enumerable.Range(0, BitConverter.ToInt32(d, nof + 8) + 1).Select(i => BitConverter.ToInt32(d, nof + 12 + i * 4)).ToHashSet();
            int start = args.Length == 4 ? Convert.ToInt32(args[2], 16) : root;
            int len = args.Length == 4 ? Convert.ToInt32(args[3], 16) : 0x40;
            Console.WriteLine($"rel0 size 0x{BitConverter.ToInt32(d, rel + 4):X}, root 0x{root:X}, rootHeader 0x{rootHeader:X}, nof0 unk 0x{BitConverter.ToInt32(d, nof + 4):X}, {ptrs.Count} ptrs");
            for (int o = start; o < start + len && rel + o + 4 <= d.Length; o += 4)
            {
                uint v = BitConverter.ToUInt32(d, rel + o);
                string target = "";
                if (ptrs.Contains(o) && v < d.Length - rel && d[rel + v] is >= 0x20 and < 0x7F)
                {
                    int end = Array.IndexOf(d, (byte)0, rel + (int)v);
                    if (end > 0 && d.AsSpan(rel + (int)v, end - rel - (int)v).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0)
                        target = $" \"{System.Text.Encoding.ASCII.GetString(d, rel + (int)v, Math.Min(48, end - rel - (int)v))}\"";
                }
                Console.WriteLine($"  {o:X6}: {Convert.ToHexString(d, rel + o, 4)}  {(ptrs.Contains(o) ? $"-> 0x{v:X}{target}" : v.ToString())}");
            }
            try
            {
                byte[] w = MusSerializer.Write(MusSerializer.Read(d));
                int diff = FirstDifference(d, w);
                Console.WriteLine(diff < 0 ? "round-trip OK" : $"first difference at file 0x{diff:X} (rel 0x{diff - rel:X}), lengths {d.Length}/{w.Length}");
            }
            catch (Exception e) { Console.WriteLine($"parse failed: {e.Message}"); }
            return 0;
        }

        default:
            return Usage();
    }
}
catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or JsonException or FormatException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        MusTool - PSO2 .mus (adaptive BGM) editor

          info     <file.mus>                         print the part/section/phrase tree
          clips    <file.mus>                         list every referenced .hca file
          export   <file.mus> [out.json]              convert to editable JSON
          import   <in.json> <out.mus>                build a .mus from JSON
          rename   <in.mus> <out.mus> <find> <repl>   substring-replace clip file names
          template <out.json>                         write a minimal JSON to start a new file
          verify   <file.mus>                         check read/write round-trips byte-for-byte
          datadir  [pso2_bin\data\win32]              show or set the game data folder used by --ice
          songs                                       list known base-game songs and their ice hashes
          scan     [song]                             read every known song from the data folder (in memory),
                                                      report its version and whether it parses/round-trips

          --ice    any <file.mus> above may instead be an ice file, a hashed name or a song name
                   (mu_11_bgm_dragonland or just dragonland) looked up in the data folder
        """);
    Console.Error.WriteLine(PlayCommand.Usage);
    return 2;
}

static int Scan(string? only)
{
    string dir = GameData.DataFolder ?? throw new ArgumentException("no data folder set; run: MusTool datadir <path>");
    var byVersion = new SortedDictionary<byte, (int ok, int failed)>();
    int missing = 0;
    foreach (var song in only != null ? [only] : GameData.KnownSongs)
    {
        string ice = Path.Combine(dir, GameData.IceHash(song));
        if (!File.Exists(ice)) { missing++; Console.WriteLine($"{song,-32} missing"); continue; }
        byte version = 0;
        try
        {
            var loaded = GameData.LoadIce(ice);
            byte[] data = loaded.MusBytes;
            int rel = BitConverter.ToInt32(data, 0x0C);
            int root = BitConverter.ToInt32(data, rel + BitConverter.ToInt32(data, rel + 8));
            version = data[rel + root + 4];
            var mus = MusSerializer.Read(data);
            bool same = MusSerializer.Write(mus).AsSpan().SequenceEqual(data);
            var c = byVersion.GetValueOrDefault(version);
            byVersion[version] = same ? (c.ok + 1, c.failed) : (c.ok, c.failed + 1);
            Console.WriteLine($"{song,-32} v0x{version:X2}  {mus.Parts.Count} parts  {mus.Parts.Sum(p => p.Sections.Count),3} sections  " +
                              $"{(same ? "round-trip OK" : "round-trip MISMATCH")}{(loaded.CpkPath == null ? "  (no cpk)" : "")}");
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            var c = byVersion.GetValueOrDefault(version);
            byVersion[version] = (c.ok, c.failed + 1);
            Console.WriteLine($"{song,-32} v0x{version:X2}  FAILED: {e.Message}");
        }
    }
    Console.WriteLine();
    foreach (var (v, (ok, failed)) in byVersion)
        Console.WriteLine($"version 0x{v:X2}: {ok} ok, {failed} failed");
    if (missing > 0) Console.WriteLine($"{missing} song(s) not in the data folder");
    return byVersion.Values.Any(c => c.failed > 0) ? 1 : 0;
}

static IEnumerable<MusClip> AllClips(MusFile mus) =>
    from p in mus.Parts
    from s in p.Sections
    from h in s.Phrases
    from b in h.Bars
    from g in b.Variations
    from c in g.Clips.Concat(g.SubClips)
    select c;

static void PrintInfo(MusFile mus)
{
    if (mus.Extension is { } ext) Console.WriteLine($"name: {ext.Name}");
    Console.WriteLine($"version 0x{mus.Version:X2}, {mus.Parts.Count} parts, {AllClips(mus).Select(c => c.File).Distinct().Count()} distinct clips");
    for (int pi = 0; pi < mus.Parts.Count; pi++)
    {
        var part = mus.Parts[pi];
        Console.WriteLine($"Part {pi} (index {part.Index})");
        Console.WriteLine($"  clip conditions:     {string.Join(", ", part.ClipConditions.Select(c => c.Name))}");
        Console.WriteLine($"  sub-clip conditions: {string.Join(", ", part.SubClipConditions.Select(c => c.Name))}");
        for (int si = 0; si < part.Sections.Count; si++)
        {
            var s = part.Sections[si];
            var firstGroup = s.Phrases.SelectMany(h => h.Bars).SelectMany(b => b.Variations).FirstOrDefault();
            string first = firstGroup?.Clips.FirstOrDefault()?.File ?? "(silent)";
            string bars = string.Join("+", s.Phrases.Select(h => h.Bars.Count));
            Console.WriteLine($"  [{si,2}] type={s.Type} unk2=0x{s.Unk2:X2} unk3={s.Unk3}  bars={bars,-12} " +
                              $"T{firstGroup?.Tempo} B{firstGroup?.Beats}  {first}");
        }
    }
}

static void Validate(MusFile mus)
{
    foreach (var (part, pi) in mus.Parts.Select((p, i) => (p, i)))
        foreach (var s in part.Sections)
            foreach (var g in s.Phrases.SelectMany(h => h.Bars).SelectMany(b => b.Variations))
            {
                foreach (var c in g.Clips.Where(c => c.Condition >= part.ClipConditions.Count))
                    Console.Error.WriteLine($"warning: part {pi}: clip {c.File} uses condition {c.Condition}, part has {part.ClipConditions.Count}");
                foreach (var c in g.SubClips.Where(c => c.Condition >= part.SubClipConditions.Count))
                    Console.Error.WriteLine($"warning: part {pi}: sub-clip {c.File} uses condition {c.Condition}, part has {part.SubClipConditions.Count}");
            }
}

static MusFile Template() => new()
{
    Parts =
    {
        new MusPart
        {
            Index = 0,
            ClipConditions = { new() { Name = "Always" }, new() { Name = "Always" } },
            SubClipConditions = { new() { Name = "Always" } },
            Sections =
            {
                new MusSection
                {
                    Type = 0,
                    Phrases =
                    {
                        new MusPhrase
                        {
                            Bars =
                            {
                                new MusBar
                                {
                                    Variations =
                                    {
                                        new MusClipGroup
                                        {
                                            Tempo = 120, Beats = 4,
                                            Clips = { new() { File = "MY_SONG_01_T120_B04.hca", Volume = 100, Condition = 1 } },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    },
};

static int FirstDifference(byte[] a, byte[] b)
{
    int n = Math.Min(a.Length, b.Length);
    for (int i = 0; i < n; i++)
        if (a[i] != b[i]) return i;
    return a.Length == b.Length ? -1 : n;
}
