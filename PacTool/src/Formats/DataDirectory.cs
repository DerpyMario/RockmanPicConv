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
/// <c>map.dat</c> and <c>bg.dat</c> share one entry format: a 20-byte head naming an asset,
/// followed by however many 40-byte collision boxes the head's count asks for. <c>bg.dat</c> only
/// ever names assets, so its entries always have a count of zero and are exactly 20 bytes.
///
/// <code>
///   0x00  u16       flags        0x1000 throughout bg.dat, 0 in map.dat
///   0x02  char[8]   name
///   0x0A  u8[6]     reserved     zero in all 1373 entries
///   0x10  u32       boxes
///   0x14  ...       box[boxes], 40 bytes each:
///                     0x00  u32     kind      5, 6, 11 to 15 in the reference data
///                     0x04  u32     reserved
///                     0x08  f32     top
///                     0x0C  f32     bottom
///                     0x10  f32     left
///                     0x14  f32     right
///                     0x18  u32     parameter only kind 5 ever sets it: 200, 2 or 40
///                     0x1C  u8[12]  reserved
/// </code>
///
/// The four floats are an axis-aligned box in the asset's own space, not a position: the first is
/// at or above the second and the third at or below the fourth in 840 of the 842 boxes, and the
/// commonest size is exactly ten by ten - the block the stages are built from.
///
/// An <c>enemy.dat</c> entry is one area's spawn list: a count, then one 24-byte record each. The
/// entries line up with the areas of the level layout beside them, and do so in all 20 stages that
/// have both.
///
/// <code>
///   0x00  u32       spawns
///   0x04  ...       spawn[spawns], 24 bytes each:
///                     0x00  u8      flags     0 throughout the reference data
///                     0x01  char[3] model     the stem of a model in the same archive
///                     0x04  f32     x
///                     0x08  f32     y
///                     0x0C  f32     z         0 throughout
///                     0x10  f32     facing    -1 left, +1 right
///                     0x14  f32     reserved  0 throughout
/// </code>
///
/// Both entry sizes are confirmed rather than assumed: <c>20 + boxes * 40</c> reproduces the
/// stored length of all 1373 map and background entries in the reference data, and
/// <c>4 + spawns * 24</c> all 82 enemy entries.
///
/// Everything past <c>tableSize</c> is the stage layout, in a container of the same shape as this
/// one; see <see cref="StageLevel"/>.
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

    /// <summary>The stage layout that follows the directory, or null when there is none.</summary>
    public StageLevel? Level { get; }

    /// <summary>Everything past the directory, verbatim. Empty when there is none.</summary>
    public byte[] Trailing { get; }

    /// <summary>Non-fatal oddities noticed while parsing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    private DataDirectory(DataDirectoryKind kind, List<DataDirectoryEntry> entries,
                          StageLevel? level, byte[] trailing, List<string> warnings)
    {
        Kind = kind;
        Entries = entries;
        Level = level;
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

        // What follows the directory is the stage layout - unless the block has none, in which case
        // it is the archive's own alignment padding and has to be left alone.
        ReadOnlySpan<byte> trailing = data[(int)tableSize..];
        StageLevel? level = null;
        if (StageLevel.LooksLikeLevel(trailing))
        {
            try
            {
                level = StageLevel.Parse(trailing, blockName, warnings);
            }
            catch (PacFormatException ex)
            {
                warnings.Add(ex.Message);
            }
        }

        return new DataDirectory(kind, entries, level, trailing.ToArray(), warnings);
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
                    X = values[0],
                    Y = values[1],
                    Z = values[2],
                    Facing = values[3],
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
        int boxes = (int)Math.Min(announced, (raw.Length - EntryHeadSize) / PlacementSize);
        if (boxes != announced)
            warnings.Add($"entry {index} ('{entry.Name}') announces {announced} box(es) but holds room for {boxes}.");

        for (int i = 0; i < boxes; i++)
        {
            ReadOnlySpan<byte> record = raw.Slice(EntryHeadSize + i * PlacementSize, PlacementSize);
            var bounds = new float[4];
            for (int f = 0; f < bounds.Length; f++)
                bounds[f] = BinaryPrimitives.ReadSingleBigEndian(record[(8 + f * 4)..]);

            entry.Boxes.Add(new DataDirectoryBox
            {
                Kind = BinaryPrimitives.ReadUInt32BigEndian(record),
                Top = bounds[0],
                Bottom = bounds[1],
                Left = bounds[2],
                Right = bounds[3],
                Parameter = BinaryPrimitives.ReadUInt32BigEndian(record[0x18..]),
            });
        }

        return entry;
    }

    /// <summary>Renders the directory as the <c>directory.txt</c> listing.</summary>
    public string Describe(string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {title}  ({Kind} directory, {Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")})");
        if (Level is { } level)
        {
            text.AppendLine($"# followed by the stage layout: {level.Areas.Count} area(s), " +
                            $"{level.PlacementCount:N0} placement(s); see areas.txt");
        }
        else if (Trailing.Length > 0)
        {
            text.AppendLine($"# followed by {Trailing.Length:N0} byte(s) that are not a layout container");
        }

        if (Kind != DataDirectoryKind.Enemy)
        {
            text.AppendLine("#");
            text.AppendLine("# Each entry names an asset. A box is that asset's collision extent in its own");
            text.AppendLine("# space - top, bottom, left, right - which the layout then places.");
        }

        text.AppendLine("#");

        foreach (DataDirectoryEntry entry in Entries)
        {
            if (Kind == DataDirectoryKind.Enemy)
            {
                text.AppendLine($"  [{entry.Index:D2}] @0x{entry.Offset:X4}  area {entry.Index}, {entry.Spawns.Count} spawn(s)");
                foreach (DataDirectorySpawn spawn in entry.Spawns)
                {
                    text.AppendLine($"         '{spawn.Model}'  at ({Join([spawn.X, spawn.Y, spawn.Z])})" +
                                    $"  facing {(spawn.Facing < 0 ? "left" : "right")}");
                }

                continue;
            }

            var line = new StringBuilder($"  [{entry.Index:D2}] @0x{entry.Offset:X4}  '{entry.Name}'");
            if (entry.Flags != 0)
                line.Append($"  flags=0x{entry.Flags:X4}");
            line.Append($"  {entry.Boxes.Count} box(es)");
            text.AppendLine(line.ToString());

            foreach (DataDirectoryBox box in entry.Boxes)
            {
                text.AppendLine($"         kind={box.Kind,-3}  top={F(box.Top)} bottom={F(box.Bottom)} " +
                                $"left={F(box.Left)} right={F(box.Right)}  ({F(box.Width)} x {F(box.Height)})" +
                                (box.Parameter != 0 ? $"  parameter={box.Parameter}" : ""));
            }
        }

        return text.ToString();
    }

    /// <summary>Formats floats the way the listings do: short, and never in exponent notation.</summary>
    private static string Join(IEnumerable<float> values) => string.Join(", ", values.Select(F));

    private static string F(float value) =>
        float.IsFinite(value) ? value.ToString("0.#####", CultureInfo.InvariantCulture)
                              : value.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>Collision boxes this asset carries. Always empty for <c>bg.dat</c>.</summary>
    public List<DataDirectoryBox> Boxes { get; } = [];

    /// <summary>Spawn records, for <c>enemy.dat</c> entries.</summary>
    public List<DataDirectorySpawn> Spawns { get; } = [];
}

/// <summary>
/// One collision box of a <c>map.dat</c> entry: an axis-aligned rectangle in the asset's own
/// space, which the stage layout then places. The four floats are a box rather than a position -
/// <see cref="Top"/> is at or above <see cref="Bottom"/> and <see cref="Left"/> at or below
/// <see cref="Right"/> in 840 of the 842 boxes in the reference data - and the commonest size is
/// exactly ten by ten, the block the stages are built from.
/// </summary>
public sealed class DataDirectoryBox
{
    /// <summary>What the box does. 5, 6 and 11 to 15 occur in the reference data.</summary>
    public required uint Kind { get; init; }

    /// <summary>Upper edge.</summary>
    public required float Top { get; init; }

    /// <summary>Lower edge.</summary>
    public required float Bottom { get; init; }

    /// <summary>Left edge.</summary>
    public required float Left { get; init; }

    /// <summary>Right edge.</summary>
    public required float Right { get; init; }

    /// <summary>Word at 0x18. Only kind 5 ever sets it, to 200, 2 or 40.</summary>
    public required uint Parameter { get; init; }

    /// <summary>Width of the box.</summary>
    public float Width => Right - Left;

    /// <summary>Height of the box.</summary>
    public float Height => Top - Bottom;
}

/// <summary>One spawn record inside an <c>enemy.dat</c> entry.</summary>
public sealed class DataDirectorySpawn
{
    /// <summary>Leading byte of the record; zero throughout the reference data.</summary>
    public required byte Flags { get; init; }

    /// <summary>Three-character model reference, matching the stem of a model in the same archive.</summary>
    public required string Model { get; init; }

    /// <summary>Horizontal position.</summary>
    public required float X { get; init; }

    /// <summary>Vertical position; negative, like every other stage coordinate.</summary>
    public required float Y { get; init; }

    /// <summary>Depth. Zero throughout the reference data.</summary>
    public required float Z { get; init; }

    /// <summary>Which way the enemy faces: -1 left, +1 right, and nothing else occurs.</summary>
    public required float Facing { get; init; }

    /// <summary>The five floats verbatim; the last is zero throughout.</summary>
    public required float[] Values { get; init; }
}
