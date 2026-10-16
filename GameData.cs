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

    /// <summary>The saved data folder (set with "datadir"), or null.</summary>
    public static string? DataFolder
    {
        get
        {
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
    public static string ResolveIce(string arg, string? dataFolder = null)
    {
        if (File.Exists(arg)) return arg;
        dataFolder ??= DataFolder ?? throw new ArgumentException(
            $"{arg} is not a file and no data folder is set. Run: MusTool datadir <pso2_bin\\data\\win32>");
        if (!Directory.Exists(dataFolder)) throw new ArgumentException($"data folder {dataFolder} does not exist");

        string direct = Path.Combine(dataFolder, arg);
        if (File.Exists(direct)) return direct;

        string song = SongName(arg);
        foreach (var candidate in new[] { song, "mu_11_bgm_" + song, "mu_" + song })
        {
            string path = Path.Combine(dataFolder, IceHash(candidate));
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"no ice for \"{song}\" in {dataFolder} (looked for {IceHash(song)})");
    }

    static string SongName(string arg)
    {
        string name = Path.GetFileName(arg.Replace('\\', '/'));
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".ice" or ".mus" or ".cpk" ? Path.GetFileNameWithoutExtension(name) : name;
    }

    /// <summary>A .mus read from an ice, with the CPK holding its clips (if it is present next to the ice).</summary>
    public sealed record IceSong(string Song, byte[] MusBytes, string IcePath, string? CpkPath);

    public static IceSong LoadIce(string icePath)
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
            string song = Path.GetFileNameWithoutExtension(name);
            string cpk = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(icePath))!, CpkHash(song));
            return new IceSong(song, file[headerSize..], icePath, File.Exists(cpk) ? cpk : null);
        }
        throw new InvalidDataException($"{icePath} contains no .mus file");
    }

    /// <summary>Reads a .mus either from a plain file or, with ice=true, from an ice resolved via <see cref="ResolveIce"/>.</summary>
    public static (MusFile mus, IceSong? ice) Open(string arg, bool ice)
    {
        if (!ice) return (MusSerializer.Read(arg), null);
        var song = LoadIce(ResolveIce(arg));
        return (MusSerializer.Read(song.MusBytes), song);
    }
}
