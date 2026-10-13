using System.Text.Json;
using MusTool;

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

if (args.Length == 0) return Usage();

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "info" when args.Length == 2:
            PrintInfo(MusSerializer.Read(args[1]));
            return 0;

        case "clips" when args.Length == 2:
            foreach (var name in AllClips(MusSerializer.Read(args[1])).Select(c => c.File).Distinct().Order())
                Console.WriteLine(name);
            return 0;

        case "export" when args.Length is 2 or 3:
        {
            var mus = MusSerializer.Read(args[1]);
            string outPath = args.Length == 3 ? args[2] : Path.ChangeExtension(args[1], ".json");
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
            var mus = MusSerializer.Read(args[1]);
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

        case "play" when args.Length >= 3:
            return PlayCommand.Run(args[1..]);

        case "template" when args.Length == 2:
            File.WriteAllText(args[1], JsonSerializer.Serialize(Template(), json));
            Console.WriteLine($"Wrote {args[1]}");
            return 0;

        case "verify" when args.Length == 2:
        {
            byte[] original = File.ReadAllBytes(args[1]);
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
        """);
    Console.Error.WriteLine(PlayCommand.Usage);
    return 2;
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
