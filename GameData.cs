using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zamboni;

namespace MusTool;

/// <summary>
/// Locates songs inside the game's data folder (pso2_bin/data). Classic songs are in win32 (NA: win32_na), NGS
/// songs in win32reboot (NA: win32reboot_na); whichever of these exist are used, NA folders first. Archives are
/// named by the MD5 of their logical path: the .mus lives in an ice named md5("sound/music/adaptive/NAME.ice") and
/// its clips in a CPK named md5("sound/music/adaptive/NAME.cpk"). Classic folders hold them flat; NGS folders split
/// the hash into a two-character folder and the rest. Everything is read in memory; nothing is extracted.
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
    public static IReadOnlyList<string> KnownSongs { get; } = LoadKnownSongs("MusTool.songs.txt");

    /// <summary>NGS song names (from the embedded songs_ngs.txt), listed separately from the classic ones.</summary>
    public static IReadOnlyList<string> KnownNgsSongs { get; } = LoadKnownSongs("MusTool.songs_ngs.txt");

    static List<string> LoadKnownSongs(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (s == null) return [];
        using var reader = new StreamReader(s);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }

    /// <summary>
    /// Turns what the user typed into an ice path. Accepts a full path to an ice, an ice's (md5) name in the data folder, a song name
    /// ("mu_11_bgm_dragonland", with or without .ice/.mus/.cpk) or a short name ("dragonland" -> mu_11_bgm_dragonland).
    /// </summary>
    public static string ResolveIce(string arg, string? dataFolder = null) => ResolveIce(arg, out _, dataFolder);

    /// <param name="song">The song name whose .ice hash matched (null when <paramref name="arg"/> was a path or hash).</param>
    public static string ResolveIce(string arg, out string? song, string? dataFolder = null)
    {
        song = null;
        if (File.Exists(arg)) return arg;
        string root = DataRoot(dataFolder) ?? throw new ArgumentException(
            $"{arg} is not a file and no data folder is set. Run: MusTool datadir <pso2_bin\\data>");
        if (!HasGameFolders(root)) throw new ArgumentException($"{root} has no win32 or win32reboot folder");

        string lower = arg.ToLowerInvariant();
        if (lower.Length == 32 && (FindClassic(lower, root) ?? FindReboot(lower, root)) is { } byHash) return byHash;
        string name = SongName(arg);
        foreach (var candidate in new[] { name, "mu_11_bgm_" + name, "mu_11_bgm02_" + name, "mu_" + name })
            if (FindIce(candidate, root) is { } path) { song = candidate; return path; }
        throw new FileNotFoundException($"no ice for \"{name}\" in {root} (looked for {IceHash(name)})");
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
        var wanted = new HashSet<string>(clips, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, List<string>>();
        if (wanted.Count == 0) return found;
        foreach (var song in KnownSongs)
        {
            if (FindClassic(CpkHash(song), dataFolder) is not { } path || string.Equals(Path.GetFullPath(path), ownCpk == null ? null : Path.GetFullPath(ownCpk), StringComparison.OrdinalIgnoreCase)) continue;
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
        // Classic songs borrow from classic CPKs, NGS songs from NGS CPKs; the two are never mixed.
        var elsewhere = IsRebootPath(song.IcePath)
            ? FindNgsClipsElsewhere(missing, song.Song, LinkedCpkNames(mus), song.CpkPath, RootOf(song.IcePath))
            : FindClipsElsewhere(missing, song.CpkPath, RootOf(song.IcePath));
        sources.AddRange(elsewhere.Keys);
        notFound = missing.Except(elsewhere.Values.SelectMany(h => h), StringComparer.OrdinalIgnoreCase).ToList();
        return sources;
    }

    /// <summary>
    /// NGS counterpart of <see cref="FindClipsElsewhere"/>, searching only NGS (win32reboot) CPKs. A music-disc song
    /// (NAME_md) usually has no CPK: its clips are in NAME.cpk, so that is tried first. Then the CPKs of the other
    /// known NGS songs (and their names without _md); some songs use another's clips (tpcs plays twp03's).
    /// </summary>
    /// <summary>
    /// The CPKs an NGS song names in its own root (Extension.Name), comma-separated: rg01_02_md names
    /// "mu_11_bgm02_rg01-02", sparring01_2_md names "mu_11_bgm02_sparring01,mu_11_bgm02_sparring01_2".
    /// </summary>
    public static IEnumerable<string> LinkedCpkNames(MusFile mus) =>
        (mus.Extension?.Name ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <param name="linked">CPK names the song itself lists (<see cref="LinkedCpkNames"/>); searched first.</param>
    public static Dictionary<string, List<string>> FindNgsClipsElsewhere(IEnumerable<string> clips, string song, IEnumerable<string> linked, string? ownCpk, string? dataFolder = null)
    {
        var wanted = new HashSet<string>(clips, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, List<string>>();
        if (wanted.Count == 0) return found;
        static string Base(string s) => s.EndsWith("_md", StringComparison.OrdinalIgnoreCase) ? s[..^3] : s;
        var candidates = linked.Append(Base(song))
            .Concat(KnownNgsSongs.SelectMany(s => new[] { s, Base(s) }))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        string? own = ownCpk == null ? null : Path.GetFullPath(ownCpk);
        foreach (var name in candidates)
        {
            if (FindReboot(CpkHash(name), dataFolder) is not { } path || found.ContainsKey(path)
                || string.Equals(Path.GetFullPath(path), own, StringComparison.OrdinalIgnoreCase)) continue;
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

    public static List<string> ReferencedClips(MusFile mus) =>
        mus.Parts.SelectMany(p => p.Sections).SelectMany(s => s.Phrases).SelectMany(h => h.Bars)
           .SelectMany(b => b.Variations).SelectMany(g => g.Clips.Concat(g.SubClips)).Select(c => c.File)
           .Concat(mus.OneShots.Select(o => o.File))
           .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    /// <summary>Song name for a CPK path in the data folder, or the file name.</summary>
    public static string SongForCpk(string path) =>
        IsRebootPath(path)
            ? KnownNgsSongs.SelectMany(s => new[] { s, s.EndsWith("_md") ? s[..^3] : s })
                  .FirstOrDefault(s => CpkHash(s).Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) + Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
              ?? Path.GetFileName(path)
            : KnownSongs.FirstOrDefault(s => CpkHash(s).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) ?? Path.GetFileName(path);

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
            // The CPK is looked up in the same kind of folder as the ice (classic or NGS), else next to the ice.
            string cpkHash = CpkHash(song);
            string? root = RootOf(icePath);
            string beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(icePath))!, cpkHash);
            string? cpk = (IsRebootPath(icePath) ? FindReboot(cpkHash, root) : FindClassic(cpkHash, root))
                          ?? (File.Exists(beside) ? beside : null);
            return new IceSong(song, file[headerSize..], icePath, cpk);
        }
        throw new InvalidDataException($"{icePath} contains no .mus file");
    }

    /// <summary>
    /// The song an ice belongs to when it was opened by path or hash: the known name whose .ice hash is the file
    /// name, else the inner .mus name.
    /// </summary>
    static string SongForIce(string icePath, string innerName)
    {
        string hash = IsRebootPath(icePath)
            ? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(icePath))) + Path.GetFileName(icePath)
            : Path.GetFileName(icePath);
        foreach (var candidate in new[] { innerName, "mu_" + innerName }.Concat(KnownSongs))
            if (IceHash(candidate).Equals(hash, StringComparison.OrdinalIgnoreCase)) return candidate;
        return innerName;
    }

    // ---------------------------------------------------------------- folders
    // Classic (win32, win32_na) and NGS (win32reboot, win32reboot_na) are looked up separately.

    /// <summary>
    /// pso2_bin/data for a folder the user gave: the folder itself, or its parent when a win32* folder was given
    /// (older configs saved pso2_bin/data/win32).
    /// </summary>
    public static string? DataRoot(string? folder = null)
    {
        folder ??= DataFolder;
        if (folder == null) return null;
        string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(full).StartsWith("win32", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(full) : full;
    }

    static IEnumerable<string> Existing(string? folder, params string[] names)
    {
        if (DataRoot(folder) is not { } root) yield break;
        foreach (var name in names)
            if (Directory.Exists(Path.Combine(root, name))) yield return Path.Combine(root, name);
    }

    /// <summary>Classic folders that exist: win32_na (NA overrides) then win32.</summary>
    public static IEnumerable<string> ClassicFolders(string? folder = null) => Existing(folder, "win32_na", "win32");

    /// <summary>NGS folders that exist: win32reboot_na then win32reboot.</summary>
    public static IEnumerable<string> RebootFolders(string? folder = null) => Existing(folder, "win32reboot_na", "win32reboot");

    public static bool HasGameFolders(string? folder = null) => ClassicFolders(folder).Any() || RebootFolders(folder).Any();

    /// <summary>A classic archive by hash (flat file name), or null.</summary>
    public static string? FindClassic(string hash, string? folder = null) =>
        ClassicFolders(folder).Select(f => Path.Combine(f, hash)).FirstOrDefault(File.Exists);

    public static string RebootPath(string rebootFolder, string hash) => Path.Combine(rebootFolder, hash[..2], hash[2..]);

    /// <summary>An NGS archive by hash (first two characters are the folder), or null.</summary>
    public static string? FindReboot(string hash, string? folder = null) =>
        RebootFolders(folder).Select(f => RebootPath(f, hash)).FirstOrDefault(File.Exists);

    /// <summary>A song's ice: classic folders first, then NGS ones. Null when absent.</summary>
    public static string? FindIce(string song, string? folder = null) =>
        FindClassic(IceHash(song), folder) ?? FindReboot(IceHash(song), folder);

    /// <summary>True for a file inside an NGS folder (…/win32reboot*/xx/rest).</summary>
    public static bool IsRebootPath(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        return dir != null && Path.GetFileName(dir).Length == 2
               && Path.GetFileName(Path.GetDirectoryName(dir) ?? "").StartsWith("win32reboot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The data folder an archive belongs to (so a song opened by path finds its own CPKs).</summary>
    static string? RootOf(string archivePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        return DataRoot(IsRebootPath(archivePath) ? Path.GetDirectoryName(dir) : dir);
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
