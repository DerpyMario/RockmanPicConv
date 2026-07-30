using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// The three directory blocks a stage archive opens with: <c>map.dat</c>, <c>bg.dat</c> and
/// <c>enemy.dat</c>. All three share a header and an offset table.
///
/// <code>
///   0x00  u32        tableSize   bytes of directory, measured from the start of the block
///   0x04  u32        count
///   0x08  u32[count] offsets     each relative to 0x08
///   ...              entries, entry i running to the start of entry i+1 and the last to tableSize
/// </code>
///
/// <c>map.dat</c> and <c>bg.dat</c> share one entry format, a 20-byte head followed by however
/// many 40-byte placements the head's count asks for. <c>bg.dat</c> only ever names assets, so its
/// entries always have a count of zero and are exactly 20 bytes.
///
/// <code>
///   0x00  u16       flags        0x1000 throughout bg.dat, 0 in map.dat
///   0x02  char[8]   name
///   0x0A  u8[6]     reserved
///   0x10  u32       placements
///   0x14  ...       placement[placements], 40 bytes each:
///                     0x00  u32     kind      5, 6, 11 to 15 in the reference data
///                     0x04  u32     reserved
///                     0x08  f32[4]  bounds    a position and an extent
///                     0x18  u8[16]  reserved
/// </code>
///
/// An <c>enemy.dat</c> entry is a spawn list: a count, then one 24-byte record each.
///
/// <code>
///   0x00  u32       spawns
///   0x04  ...       spawn[spawns], 24 bytes each:
///                     0x00  u8      flags     0 throughout the reference data
///                     0x01  char[3] model     the stem of a model in the same archive
///                     0x04  f32[5]  placement
/// </code>
///
/// Both entry sizes are confirmed rather than assumed: <c>20 + placements * 40</c> reproduces the
/// stored length of all 1373 map and background entries in the reference data, and
/// <c>4 + spawns * 24</c> all 82 enemy entries.
///
/// Everything past <c>tableSize</c> is the level data proper - itself a four-entry offset table
/// followed by geometry and placement streams. That is a separate format and is extracted verbatim.
/// </summary>
public sealed class DataDirectory
{
    /// <summary>Size of the header before the offset table.</summary>
    public const int HeaderSize = 0x08;

    /// <summary>Size of an entry head, before its placements.</summary>
    public const int EntryHeadSize = 0x14;

    /// <summary>Size of one placement record.</summary>
    public const int PlacementSize = 40;

    /// <summary>Size of one spawn record.</summary>
    public const int SpawnSize = 24;

    /// <summary>Which of the three directories this is.</summary>
    public DataDirectoryKind Kind { get; }

    /// <summary>Entries in stored order.</summary>
    public IReadOnlyList<DataDirectoryEntry> Entries { get; }

    /// <summary>Level data following the directory, empty when there is none.</summary>
    public byte[] Trailing { get; }

    /// <summary>Non-fatal oddities noticed while parsing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    private DataDirectory(DataDirectoryKind kind, List<DataDirectoryEntry> entries,
                          byte[] trailing, List<string> warnings)
    {
        Kind = kind;
        Entries = entries;
        Trailing = trailing;
        Warnings = warnings;
    }

    /// <summary>Maps a block name onto one of the known directory layouts.</summary>
    public static DataDirectoryKind KindOf(string name) => name.ToLowerInvariant() switch
    {
        "map.dat" => DataDirectoryKind.Map,
        "bg.dat" => DataDirectoryKind.Background,
        "enemy.dat" => DataDirectoryKind.Enemy,
        _ => DataDirectoryKind.Unknown,
    };

    /// <summary>True if <paramref name="name"/> names one of the three known directory blocks.</summary>
    public static bool IsKnownName(string name) => KindOf(name) != DataDirectoryKind.Unknown;

    /// <summary>Parses a directory block.</summary>
    public static DataDirectory Parse(ReadOnlySpan<byte> data, string blockName)
    {
        if (data.Length < HeaderSize)
            throw new PacFormatException($"{blockName}: {data.Length} bytes is too short for a directory header.");

        DataDirectoryKind kind = KindOf(blockName);
        long tableSize = BinaryPrimitives.ReadUInt32BigEndian(data);
        long count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var warnings = new List<string>();

        if (count > 0x10000 || HeaderSize + count * 4 > data.Length)
            throw new PacFormatException($"{blockName}: entry count {count} does not fit in {data.Length} bytes.");

        if (tableSize > data.Length)
        {
            warnings.Add($"the header puts the end of the directory at 0x{tableSize:X}, past the end of the block.");
            tableSize = data.Length;
        }

        var offsets = new int[count];
        for (int i = 0; i < count; i++)
            offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(HeaderSize + i * 4)..]);

        var entries = new List<DataDirectoryEntry>((int)count);
        for (int i = 0; i < count; i++)
        {
            int start = HeaderSize + offsets[i];
            int end = i + 1 < count ? HeaderSize + offsets[i + 1] : (int)tableSize;
            if (start < 0 || start > tableSize || end < start)
            {
                warnings.Add($"entry {i} spans 0x{start:X}..0x{end:X}, which is outside the directory; skipped.");
                continue;
            }

            entries.Add(Parse(kind, i, start, data[start..Math.Min(end, (int)tableSize)], warnings));
        }

        return new DataDirectory(kind, entries, data[(int)tableSize..].ToArray(), warnings);
    }

    private static DataDirectoryEntry Parse(DataDirectoryKind kind, int index, int offset,
                                            ReadOnlySpan<byte> raw, List<string> warnings)
    {
        var entry = new DataDirectoryEntry
        {
            Index = index,
            Offset = offset,
            Raw = raw.ToArray(),
        };

        if (kind == DataDirectoryKind.Enemy)
        {
            if (raw.Length < 4)
                return entry;

            long declared = BinaryPrimitives.ReadUInt32BigEndian(raw);
            int spawns = (int)Math.Min(declared, (raw.Length - 4) / SpawnSize);
            if (spawns != declared)
                warnings.Add($"entry {index} announces {declared} spawn(s) but holds room for {spawns}.");

            for (int i = 0; i < spawns; i++)
            {
                ReadOnlySpan<byte> record = raw.Slice(4 + i * SpawnSize, SpawnSize);
                var values = new float[5];
                for (int f = 0; f < values.Length; f++)
                    values[f] = BinaryPrimitives.ReadSingleBigEndian(record[(4 + f * 4)..]);

                entry.Spawns.Add(new DataDirectorySpawn
                {
                    Flags = record[0],
                    Model = Ascii.Decode(record[1..4]),
                    Values = values,
                });
            }

            return entry;
        }

        if (raw.Length < EntryHeadSize)
            return entry;

        entry.Flags = BinaryPrimitives.ReadUInt16BigEndian(raw);
        entry.Name = Ascii.Decode(raw[2..10]);

        long announced = BinaryPrimitives.ReadUInt32BigEndian(raw[0x10..]);
        int placements = (int)Math.Min(announced, (raw.Length - EntryHeadSize) / PlacementSize);
        if (placements != announced)
            warnings.Add($"entry {index} ('{entry.Name}') announces {announced} placement(s) but holds room for {placements}.");

        for (int i = 0; i < placements; i++)
        {
            ReadOnlySpan<byte> record = raw.Slice(EntryHeadSize + i * PlacementSize, PlacementSize);
            var bounds = new float[4];
            for (int f = 0; f < bounds.Length; f++)
                bounds[f] = BinaryPrimitives.ReadSingleBigEndian(record[(8 + f * 4)..]);

            entry.Placements.Add(new DataDirectoryPlacement
            {
                Kind = BinaryPrimitives.ReadUInt32BigEndian(record),
                Bounds = bounds,
            });
        }

        return entry;
    }

    /// <summary>Renders the directory as the <c>directory.txt</c> listing.</summary>
    public string Describe(string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {title}  ({Kind} directory, {Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")})");
        if (Trailing.Length > 0)
            text.AppendLine($"# followed by {Trailing.Length:N0} bytes of level data, extracted verbatim to leveldata.bin");
        text.AppendLine("#");

        foreach (DataDirectoryEntry entry in Entries)
        {
            if (Kind == DataDirectoryKind.Enemy)
            {
                text.AppendLine($"  [{entry.Index:D2}] @0x{entry.Offset:X4}  {entry.Spawns.Count} spawn(s)");
                foreach (DataDirectorySpawn spawn in entry.Spawns)
                    text.AppendLine($"         model='{spawn.Model}'  flags=0x{spawn.Flags:X2}  [{Join(spawn.Values)}]");

                continue;
            }

            var line = new StringBuilder($"  [{entry.Index:D2}] @0x{entry.Offset:X4}  '{entry.Name}'");
            if (entry.Flags != 0)
                line.Append($"  flags=0x{entry.Flags:X4}");
            line.Append($"  {entry.Placements.Count} placement(s)");
            text.AppendLine(line.ToString());

            foreach (DataDirectoryPlacement placement in entry.Placements)
                text.AppendLine($"         kind={placement.Kind,-3}  [{Join(placement.Bounds)}]");
        }

        return text.ToString();
    }

    /// <summary>Formats floats the way the listings do: short, and never in exponent notation.</summary>
    private static string Join(IEnumerable<float> values) => string.Join(", ", values.Select(v =>
        float.IsFinite(v) ? v.ToString("0.#####", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Which of the stage directory blocks a <see cref="DataDirectory"/> is.</summary>
public enum DataDirectoryKind
{
    /// <summary>Not one of the three known blocks; only the offset table is trusted.</summary>
    Unknown,

    /// <summary><c>map.dat</c> - named stage objects and where they are placed.</summary>
    Map,

    /// <summary><c>bg.dat</c> - names of the background assets the stage references.</summary>
    Background,

    /// <summary><c>enemy.dat</c> - lists of spawn records, each naming a model and a position.</summary>
    Enemy,
}

/// <summary>One entry of a <see cref="DataDirectory"/>.</summary>
public sealed class DataDirectoryEntry
{
    /// <summary>Position in the offset table.</summary>
    public required int Index { get; init; }

    /// <summary>Offset of the entry within the block.</summary>
    public required int Offset { get; init; }

    /// <summary>The entry's bytes.</summary>
    public required byte[] Raw { get; init; }

    /// <summary>Field at 0x00. <c>bg.dat</c> uses 0x1000 throughout; <c>map.dat</c> leaves it zero.</summary>
    public ushort Flags { get; set; }

    /// <summary>Asset name, empty when the entry is too short to hold one.</summary>
    public string Name { get; set; } = "";

    /// <summary>Placements this entry carries. Always empty for <c>bg.dat</c>.</summary>
    public List<DataDirectoryPlacement> Placements { get; } = [];

    /// <summary>Spawn records, for <c>enemy.dat</c> entries.</summary>
    public List<DataDirectorySpawn> Spawns { get; } = [];
}

/// <summary>One placement record of a <c>map.dat</c> entry.</summary>
public sealed class DataDirectoryPlacement
{
    /// <summary>Placement kind. 5, 6 and 11 to 15 occur in the reference data.</summary>
    public required uint Kind { get; init; }

    /// <summary>The four floats the record carries, which read as a position and an extent.</summary>
    public required float[] Bounds { get; init; }
}

/// <summary>One placement record inside an <c>enemy.dat</c> entry.</summary>
public sealed class DataDirectorySpawn
{
    /// <summary>Leading byte of the record; zero throughout the reference data.</summary>
    public required byte Flags { get; init; }

    /// <summary>Three-character model reference, matching the stem of a model in the same archive.</summary>
    public required string Model { get; init; }

    /// <summary>The five floats that follow. The first three read as a position.</summary>
    public required float[] Values { get; init; }
}
