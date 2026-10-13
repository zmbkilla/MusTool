using System.Buffers.Binary;
using System.Text;

namespace MusTool;

/// <summary>
/// Minimal CRI CPK reader: parses the (optionally XOR-encrypted) @UTF header and TOC tables and
/// returns uncompressed files by name. Enough for PSO2 BGM archives, which store HCA files uncompressed.
/// </summary>
public sealed class CpkArchive : IDisposable
{
    readonly FileStream stream;
    readonly Dictionary<string, (long offset, int size)> files = new(StringComparer.OrdinalIgnoreCase);

    public CpkArchive(string path)
    {
        stream = File.OpenRead(path);
        var head = ReadAt(0, 16);
        if (Encoding.ASCII.GetString(head, 0, 4) != "CPK ")
            throw new InvalidDataException($"{path} is not a CPK archive.");

        var header = ReadUtf(ReadAt(16, BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8))))[0];
        long tocOffset = Convert.ToInt64(header["TocOffset"]);
        long contentOffset = Convert.ToInt64(header["ContentOffset"]);
        long dataBase = Math.Min(tocOffset, contentOffset);

        var tocHead = ReadAt(tocOffset, 16);
        var toc = ReadUtf(ReadAt(tocOffset + 16, BinaryPrimitives.ReadInt32LittleEndian(tocHead.AsSpan(8))));
        foreach (var row in toc)
        {
            string dir = row.GetValueOrDefault("DirName") as string ?? "";
            string name = (string)row["FileName"]!;
            long size = Convert.ToInt64(row["FileSize"]);
            if (size != Convert.ToInt64(row["ExtractSize"]))
                continue; // CRILAYLA-compressed; not used by PSO2 BGM archives
            string key = dir.Length > 0 ? $"{dir}/{name}" : name;
            files[key] = (dataBase + Convert.ToInt64(row["FileOffset"]), (int)size);
        }
    }

    public IReadOnlyCollection<string> FileNames => files.Keys;

    public byte[]? TryRead(string name)
    {
        if (!files.TryGetValue(name, out var f)) return null;
        lock (stream) return ReadAt(f.offset, f.size);
    }

    public void Dispose() => stream.Dispose();

    byte[] ReadAt(long offset, int size)
    {
        var buf = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(buf);
        return buf;
    }

    static List<Dictionary<string, object?>> ReadUtf(byte[] table)
    {
        if (Encoding.ASCII.GetString(table, 0, 4) != "@UTF")
        {
            // CRI table obfuscation
            uint m = 0x655F;
            for (int i = 0; i < table.Length; i++)
            {
                table[i] ^= (byte)m;
                m *= 0x4115;
            }
            if (Encoding.ASCII.GetString(table, 0, 4) != "@UTF")
                throw new InvalidDataException("CPK table is not a @UTF table.");
        }

        var t = table.AsSpan(8);
        int rowsOffset = (int)BE32(t, 0), stringsOffset = (int)BE32(t, 4), dataOffset = (int)BE32(t, 8);
        int columnCount = BE16(t, 16), rowLength = BE16(t, 18);
        int rowCount = (int)BE32(t, 20);
        var tb = table.AsSpan(8).ToArray();

        string Str(int o)
        {
            int start = stringsOffset + o;
            int end = Array.IndexOf(tb, (byte)0, start);
            return Encoding.UTF8.GetString(tb, start, end - start);
        }

        var columns = new List<(int flags, string name, int constPos)>();
        int p = 24;
        for (int c = 0; c < columnCount; c++)
        {
            int flags = tb[p];
            string name = Str((int)BE32(tb, p + 1));
            p += 5;
            int constPos = -1;
            if ((flags & 0xF0) == 0x30)
            {
                constPos = p;
                p += TypeSize(flags & 0x0F);
            }
            columns.Add((flags, name, constPos));
        }

        var rows = new List<Dictionary<string, object?>>();
        for (int r = 0; r < rowCount; r++)
        {
            int q = rowsOffset + r * rowLength;
            var row = new Dictionary<string, object?>();
            foreach (var (flags, name, constPos) in columns)
            {
                int storage = flags & 0xF0;
                if (storage == 0x30) row[name] = ReadValue(tb, constPos, flags & 0x0F, Str, dataOffset);
                else if (storage == 0x50)
                {
                    row[name] = ReadValue(tb, q, flags & 0x0F, Str, dataOffset);
                    q += TypeSize(flags & 0x0F);
                }
                else row[name] = null;
            }
            rows.Add(row);
        }
        return rows;
    }

    static int TypeSize(int type) => type switch
    {
        0 or 1 => 1,
        2 or 3 => 2,
        4 or 5 or 8 or 0xA => 4,
        6 or 7 or 0xB => 8,
        _ => throw new InvalidDataException($"Unknown @UTF column type {type}."),
    };

    static object ReadValue(byte[] t, int o, int type, Func<int, string> str, int dataOffset) => type switch
    {
        0 => t[o],
        1 => (sbyte)t[o],
        2 => BE16(t, o),
        3 => (short)BE16(t, o),
        4 => BE32(t, o),
        5 => (int)BE32(t, o),
        6 => BinaryPrimitives.ReadUInt64BigEndian(t.AsSpan(o)),
        7 => BinaryPrimitives.ReadInt64BigEndian(t.AsSpan(o)),
        8 => BinaryPrimitives.ReadSingleBigEndian(t.AsSpan(o)),
        0xA => str((int)BE32(t, o)),
        _ => t.AsSpan(dataOffset + (int)BE32(t, o), (int)BE32(t, o + 4)).ToArray(),
    };

    static ushort BE16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16BigEndian(s[o..]);
    static uint BE32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32BigEndian(s[o..]);
}
