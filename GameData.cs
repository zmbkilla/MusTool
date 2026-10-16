using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zamboni;

namespace MusTool;

/// <summary>
/// Locates songs inside the game's data folder (pso2_bin/data/win32). Archives there are named by the MD5 of
/// their logical path: the .mus lives in an ice named md5("sound/music/adaptive/NAME.ice") and its clips in a
/// CPK named md5("sound/music/adaptive/NAME.cpk"). Everything is read in memory; nothing is extracted.
/// </summary>
public static class GameData
{
    const string AdaptiveDir = "sound/music/adaptive/";

    static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusTool", "config.json");

    sealed class Config
    {
        public string? DataFolder { get; set; }
    }

    /// <summary>Set by --data: replaces the saved folder for this run without changing the config.</summary>
    public static string? DataFolderOverride { get; set; }

    /// <summary>The data folder for this run: --data, else the saved one (set with "datadir"), else null.</summary>
    public static string? DataFolder
    {
        get
        {
            if (DataFolderOverride != null) return DataFolderOverride;
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath))?.DataFolder; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config { DataFolder = value }));
        }
    }

    public static string HashName(string logicalPath) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(logicalPath))).ToLowerInvariant();

    public static string IceHash(string song) => HashName($"{AdaptiveDir}{song}.ice");
    public static string CpkHash(string song) => HashName($"{AdaptiveDir}{song}.cpk");

    /// <summary>Song names known to ship with the base game (from the embedded songs.txt).</summary>
    public static IReadOnlyList<string> KnownSongs { get; } = LoadKnownSongs();

    static List<string> LoadKnownSongs()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MusTool.songs.txt");
        if (s == null) return [];
        using var reader = new StreamReader(s);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }

    /// <summary>
    /// Turns what the user typed into an ice path. Accepts an existing file path, a hashed file name, a song name
    /// ("mu_11_bgm_dragonland", with or without .ice/.mus/.cpk) or a short name ("dragonland" -> mu_11_bgm_dragonland).
    /// </summary>
    public static string ResolveIce(string arg, string? dataFolder = null) => ResolveIce(arg, out _, dataFolder);

    /// <param name="song">The song name whose .ice hash matched (null when <paramref name="arg"/> was a path or hash).</param>
    public static string ResolveIce(string arg, out string? song, string? dataFolder = null)
    {
        song = null;
        if (File.Exists(arg)) return arg;
        dataFolder ??= DataFolder ?? throw new ArgumentException(
            $"{arg} is not a file and no data folder is set. Run: MusTool datadir <pso2_bin\\data\\win32>");
        if (!Directory.Exists(dataFolder)) throw new ArgumentException($"data folder {dataFolder} does not exist");

        string direct = Path.Combine(dataFolder, arg);
        if (File.Exists(direct)) return direct;

        string name = SongName(arg);
        foreach (var candidate in new[] { name, "mu_11_bgm_" + name, "mu_" + name })
        {
            string path = Path.Combine(dataFolder, IceHash(candidate));
            if (File.Exists(path)) { song = candidate; return path; }
        }
        throw new FileNotFoundException($"no ice for \"{name}\" in {dataFolder} (looked for {IceHash(name)})");
    }

    static string SongName(string arg)
    {
        string name = Path.GetFileName(arg.Replace('\\', '/'));
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".ice" or ".mus" or ".cpk" ? Path.GetFileNameWithoutExtension(name) : name;
    }

    /// <summary>
    /// For clips a song's own CPK lacks: which other known songs' CPKs in the data folder contain them.
    /// Only the CPK tables of contents are read. Returns CPK path -> clips it provides (first CPK wins).
    /// </summary>
    public static Dictionary<string, List<string>> FindClipsElsewhere(IEnumerable<string> clips, string? ownCpk, string? dataFolder = null)
    {
        dataFolder ??= DataFolder;
        var wanted = new HashSet<string>(clips, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, List<string>>();
        if (dataFolder == null || wanted.Count == 0) return found;
        foreach (var song in KnownSongs)
        {
            string path = Path.Combine(dataFolder, CpkHash(song));
            if (!File.Exists(path) || string.Equals(Path.GetFullPath(path), ownCpk == null ? null : Path.GetFullPath(ownCpk), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var cpk = new CpkArchive(path);
                var hits = cpk.FileNames.Where(wanted.Contains).ToList();
                if (hits.Count == 0) continue;
                found[path] = hits;
                wanted.ExceptWith(hits);
                if (wanted.Count == 0) break;
            }
            catch (InvalidDataException) { }
        }
        return found;
    }

    /// <summary>
    /// CPKs to load for a song read from an ice: its own CPK (when present) plus other songs' CPKs that hold the
    /// clips it lacks. Some songs share clips (defence_* use mu_11_bgm_defence_add, which has no ice) and
    /// some have no CPK of their own at all (mu_11_bgm_mdfcl).
    /// </summary>
    public static List<string> ClipSources(MusFile mus, IceSong song, out List<string> notFound)
    {
        var names = ReferencedClips(mus);
        var sources = new List<string>();
        var missing = names;
        if (song.CpkPath is { } own)
        {
            sources.Add(own);
            using var cpk = new CpkArchive(own);
            var have = cpk.FileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            missing = names.Where(n => !have.Contains(n)).ToList();
        }
        var elsewhere = FindClipsElsewhere(missing, song.CpkPath, Path.GetDirectoryName(Path.GetFullPath(song.IcePath)));
        sources.AddRange(elsewhere.Keys);
        notFound = missing.Except(elsewhere.Values.SelectMany(h => h), StringComparer.OrdinalIgnoreCase).ToList();
        return sources;
    }

    public static List<string> ReferencedClips(MusFile mus) =>
        mus.Parts.SelectMany(p => p.Sections).SelectMany(s => s.Phrases).SelectMany(h => h.Bars)
           .SelectMany(b => b.Variations).SelectMany(g => g.Clips.Concat(g.SubClips)).Select(c => c.File)
           .Concat(mus.OneShots.Select(o => o.File))
           .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    /// <summary>Song name for a CPK path in the data folder, or the file name.</summary>
    public static string SongForCpk(string path) =>
        KnownSongs.FirstOrDefault(s => CpkHash(s).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) ?? Path.GetFileName(path);

    /// <summary>A .mus read from an ice, with the CPK holding its clips (if it is present next to the ice).</summary>
    public sealed record IceSong(string Song, byte[] MusBytes, string IcePath, string? CpkPath);

    /// <param name="song">
    /// The song the ice was looked up by. Its clips are in NAME.cpk (same logical name, .ice -> .cpk); the .mus inside
    /// is found by extension only, since its own name can differ (mu_11_bgm_mdfcl.ice holds "11_bgm_mdfcl.mus").
    /// </param>
    public static IceSong LoadIce(string icePath, string? song = null)
    {
        IceFile ice;
        try { ice = IceFile.LoadIceFile(new MemoryStream(File.ReadAllBytes(icePath))); }
        catch (Exception e) when (e is not IOException) { throw new InvalidDataException($"{icePath} is not a readable ice: {e.Message}"); }

        foreach (var file in (ice.groupOneFiles ?? []).Concat(ice.groupTwoFiles ?? []))
        {
            string name = IceFile.getFileName(file);
            if (!name.EndsWith(".mus", StringComparison.OrdinalIgnoreCase)) continue;

            // Ice entry header: ext[4], total size, data size, header size (0x60 for .mus).
            int headerSize = BitConverter.ToInt32(file, 0x0C);
            song ??= SongForIce(icePath, Path.GetFileNameWithoutExtension(name));
            string cpk = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(icePath))!, CpkHash(song));
            return new IceSong(song, file[headerSize..], icePath, File.Exists(cpk) ? cpk : null);
        }
        throw new InvalidDataException($"{icePath} contains no .mus file");
    }

    /// <summary>
    /// The song an ice belongs to when it was opened by path or hash: the known name whose .ice hash is the file
    /// name, else the inner .mus name.
    /// </summary>
    static string SongForIce(string icePath, string innerName)
    {
        string hash = Path.GetFileName(icePath);
        foreach (var candidate in new[] { innerName, "mu_" + innerName }.Concat(KnownSongs))
            if (IceHash(candidate).Equals(hash, StringComparison.OrdinalIgnoreCase)) return candidate;
        return innerName;
    }

    /// <summary>Reads a .mus either from a plain file or, with ice=true, from an ice resolved via <see cref="ResolveIce"/>.</summary>
    public static (MusFile mus, IceSong? ice) Open(string arg, bool ice)
    {
        if (!ice) return (MusSerializer.Read(arg), null);
        string path = ResolveIce(arg, out var name);
        var song = LoadIce(path, name);
        return (MusSerializer.Read(song.MusBytes), song);
    }
}
