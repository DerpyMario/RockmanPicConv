using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// The layout half of a stage block: what is placed where, area by area.
///
/// A <c>map.dat</c> or <c>bg.dat</c> is two containers back to back. The first is the
/// <see cref="DataDirectory"/> - the assets the stage uses and the collision box of each. The
/// second, which starts where the first says it ends, is this one, and it uses exactly the same
/// header: a size, a count, then that many offsets relative to 0x08.
///
/// <code>
///   0x00  u32        size        bytes of level data, measured from the start of this block
///   0x04  u32        count       areas
///   0x08  u32[count] offsets     each relative to 0x08
///   ...              area[count], area i running to the start of area i+1
/// </code>
///
/// Each of those is an <b>area</b> - one screen-sized region of the stage, of which a stage has
/// four or five. An area is a 64-byte head and then two runs of records:
///
/// <code>
///   0x00  u32       word0       role not established
///   0x04  u32       word1       role not established
///   0x08  f32       pointX      a point in the placement coordinate space; role not established
///   0x0C  f32       pointY      stored positive where placements are negative
///   0x10  u32       word4       0, 2, 4, 6 or 7
///   0x14  u32       stale       whatever the writing tool's buffer held
///   0x18  u32       placements
///   0x1C  u32       backdrops
///   0x20  u32       zero
///   0x24  u8[28]    stale
///   0x40  ...       placement[placements], 20 bytes each
///   ...             backdrop[backdrops], 32 bytes each
/// </code>
///
/// <c>64 + placements * 20 + backdrops * 32</c> reproduces the length of <b>all 180 areas</b> in
/// the reference data exactly, which is what fixes both record sizes.
///
/// A placement names one of the directory's entries and puts it somewhere:
///
/// <code>
///   0x00  u8      entry       index into the DataDirectory in front of this block
///   0x01  u8[3]   stale
///   0x04  f32     x
///   0x08  f32     y           negative: the stage hangs below the origin
///   0x0C  f32     z           0 throughout map.dat; the parallax depth in bg.dat
///   0x10  u8      zero
///   0x11  u8[3]   parameter   object-specific, and zero on 98% of placements
/// </code>
///
/// The entry index is what ties the two halves together, and it is not a guess: in 38 of the 44
/// blocks that have level data the highest index used is <i>exactly</i> one less than the number of
/// directory entries. Positions land on a five-unit editor grid - 31,812 of the 31,866 coordinates
/// in <c>map.dat</c> are a multiple of five - and the commonest collision box is ten by ten, so the
/// stage is built from blocks on a ten-unit lattice.
///
/// A backdrop record only ever appears in <c>bg.dat</c>, never in <c>map.dat</c>, and always in
/// identical pairs. It carries an RGBA colour and four floats, of which the first two read as a
/// position in the same space as the placements. What it drives is not established.
///
/// <code>
///   0x00  u32     zero
///   0x04  u8[4]   colour      RGBA; the alpha byte is 0xFF or 0x00
///   0x08  f32[4]  values      the first two read as a position, the fourth as a range
///   0x18  u32     wordA       1 on the first of a pair, 0 on the second
///   0x1C  u32     wordB       16 on the first of a pair, 1 on the second
/// </code>
/// </summary>
public sealed class StageLevel
{
    /// <summary>Size of the header before the offset table.</summary>
    public const int HeaderSize = 0x08;

    /// <summary>Size of an area head, before its records.</summary>
    public const int AreaHeadSize = 0x40;

    /// <summary>Size of one placement record.</summary>
    public const int PlacementSize = 20;

    /// <summary>Size of one backdrop record.</summary>
    public const int BackdropSize = 32;

    /// <summary>Areas in stored order.</summary>
    public IReadOnlyList<StageArea> Areas { get; }

    /// <summary>Bytes past the last area, which is the archive's own padding.</summary>
    public int TrailingBytes { get; }

    private StageLevel(List<StageArea> areas, int trailing)
    {
        Areas = areas;
        TrailingBytes = trailing;
    }

    /// <summary>Placements across every area.</summary>
    public int PlacementCount => Areas.Sum(a => a.Placements.Count);

    /// <summary>Backdrops across every area.</summary>
    public int BackdropCount => Areas.Sum(a => a.Backdrops.Count);

    /// <summary>
    /// True if <paramref name="data"/> starts with a level container. What follows a directory is
    /// usually this, but on a block with no level data it is the archive's padding, so the test has
    /// to be strict: the count has to be sane, the first offset has to land exactly at the end of
    /// the offset table, and the offsets have to ascend.
    /// </summary>
    public static bool LooksLikeLevel(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + 4)
            return false;

        long count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (count is < 1 or > 64 || HeaderSize + count * 4 > data.Length)
            return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[HeaderSize..]) != count * 4)
            return false;

        long previous = -1;
        for (int i = 0; i < count; i++)
        {
            long offset = BinaryPrimitives.ReadUInt32BigEndian(data[(HeaderSize + i * 4)..]);
            if (offset <= previous || HeaderSize + offset > data.Length)
                return false;

            previous = offset;
        }

        return true;
    }

    /// <summary>Parses the level container that follows a directory.</summary>
    public static StageLevel Parse(ReadOnlySpan<byte> data, string blockName, List<string> warnings)
    {
        if (!LooksLikeLevel(data))
            throw new PacFormatException($"{blockName}: the bytes after the directory are not a level container.");

        long size = BinaryPrimitives.ReadUInt32BigEndian(data);
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (size > data.Length)
        {
            warnings.Add($"the level container states {size:N0} bytes but only {data.Length:N0} are present; truncating.");
            size = data.Length;
        }

        var offsets = new int[count];
        for (int i = 0; i < count; i++)
            offsets[i] = HeaderSize + (int)BinaryPrimitives.ReadUInt32BigEndian(data[(HeaderSize + i * 4)..]);

        var areas = new List<StageArea>(count);
        for (int i = 0; i < count; i++)
        {
            int start = offsets[i];
            int end = i + 1 < count ? offsets[i + 1] : (int)size;
            if (end > data.Length || end < start)
            {
                warnings.Add($"area {i} spans 0x{start:X}..0x{end:X}, which is outside the block; skipped.");
                continue;
            }

            areas.Add(ParseArea(i, start, data[start..end], blockName, warnings));
        }

        return new StageLevel(areas, (int)Math.Max(0, data.Length - size));
    }

    private static StageArea ParseArea(int index, int offset, ReadOnlySpan<byte> raw,
                                       string blockName, List<string> warnings)
    {
        var area = new StageArea
        {
            Index = index,
            Offset = offset,
            Head = raw.Length >= AreaHeadSize ? raw[..AreaHeadSize].ToArray() : raw.ToArray(),
        };

        if (raw.Length < AreaHeadSize)
        {
            warnings.Add($"area {index} is {raw.Length} bytes, too short for a head.");
            return area;
        }

        area.Word0 = BinaryPrimitives.ReadUInt32BigEndian(raw);
        area.Word1 = BinaryPrimitives.ReadUInt32BigEndian(raw[4..]);
        area.PointX = BinaryPrimitives.ReadSingleBigEndian(raw[8..]);
        area.PointY = BinaryPrimitives.ReadSingleBigEndian(raw[0x0C..]);
        area.Word4 = BinaryPrimitives.ReadUInt32BigEndian(raw[0x10..]);

        long placements = BinaryPrimitives.ReadUInt32BigEndian(raw[0x18..]);
        long backdrops = BinaryPrimitives.ReadUInt32BigEndian(raw[0x1C..]);
        long wanted = AreaHeadSize + placements * PlacementSize + backdrops * BackdropSize;
        if (wanted != raw.Length)
        {
            warnings.Add($"area {index} announces {placements} placement(s) and {backdrops} backdrop(s), " +
                         $"which need {wanted:N0} bytes, but the area is {raw.Length:N0}; reading what fits.");
            placements = Math.Min(placements, (raw.Length - AreaHeadSize) / PlacementSize);
            backdrops = Math.Min(backdrops,
                                 (raw.Length - AreaHeadSize - placements * PlacementSize) / BackdropSize);
        }

        for (int i = 0; i < placements; i++)
        {
            ReadOnlySpan<byte> record = raw.Slice(AreaHeadSize + i * PlacementSize, PlacementSize);
            area.Placements.Add(new StagePlacement(
                record[0],
                BinaryPrimitives.ReadSingleBigEndian(record[4..]),
                BinaryPrimitives.ReadSingleBigEndian(record[8..]),
                BinaryPrimitives.ReadSingleBigEndian(record[0x0C..]),
                record[0x11], record[0x12], record[0x13]));
        }

        int backdropStart = AreaHeadSize + (int)placements * PlacementSize;
        for (int i = 0; i < backdrops; i++)
        {
            ReadOnlySpan<byte> record = raw.Slice(backdropStart + i * BackdropSize, BackdropSize);
            var values = new float[4];
            for (int v = 0; v < values.Length; v++)
                values[v] = BinaryPrimitives.ReadSingleBigEndian(record[(8 + v * 4)..]);

            area.Backdrops.Add(new StageBackdrop
            {
                Red = record[4],
                Green = record[5],
                Blue = record[6],
                Alpha = record[7],
                Values = values,
                WordA = BinaryPrimitives.ReadUInt32BigEndian(record[0x18..]),
                WordB = BinaryPrimitives.ReadUInt32BigEndian(record[0x1C..]),
            });
        }

        _ = blockName;
        return area;
    }

    /// <summary>Renders the layout as the <c>areas.txt</c> listing, resolving each placement's asset.</summary>
    public string Describe(string title, IReadOnlyList<DataDirectoryEntry> entries,
                           IReadOnlyList<DataDirectoryEntry>? spawnGroups = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"# layout of {title}  ({Areas.Count} area(s), {PlacementCount:N0} placement(s))");
        text.AppendLine("#");
        text.AppendLine("# An area is one region of the stage. Each placement names an entry of the directory");
        text.AppendLine("# in front of this block and puts it at a position; y is negative because the stage");
        text.AppendLine("# hangs below the origin, and positions sit on a five-unit grid.");
        text.AppendLine("#");

        foreach (StageArea area in Areas)
        {
            text.AppendLine($"[area {area.Index}] @0x{area.Offset:X4}  " +
                            $"{area.Placements.Count:N0} placement(s), {area.Backdrops.Count} backdrop(s)");
            text.AppendLine($"         point=({F(area.PointX)}, {F(area.PointY)})  " +
                            $"word0={area.Word0}  word1={area.Word1}  word4={area.Word4}");

            if (spawnGroups is not null && area.Index < spawnGroups.Count)
            {
                foreach (DataDirectorySpawn spawn in spawnGroups[area.Index].Spawns)
                {
                    text.AppendLine($"    spawn  '{spawn.Model}'  at ({F(spawn.X)}, {F(spawn.Y)}, {F(spawn.Z)})" +
                                    $"  facing {(spawn.Facing < 0 ? "left" : "right")}");
                }
            }

            foreach (StagePlacement placement in area.Placements)
            {
                string name = placement.Entry < entries.Count
                    ? $"'{entries[placement.Entry].Name}'"
                    : $"<entry {placement.Entry}, past the end of the directory>";

                var line = new StringBuilder($"    {placement.Entry,3}  {name,-12}  " +
                                             $"({F(placement.X)}, {F(placement.Y)}, {F(placement.Z)})");
                if (placement.HasParameter)
                    line.Append($"  parameter={placement.Parameter0},{placement.Parameter1},{placement.Parameter2}");

                text.AppendLine(line.ToString());
            }

            foreach (StageBackdrop backdrop in area.Backdrops)
                text.AppendLine($"    backdrop  #{backdrop.Red:X2}{backdrop.Green:X2}{backdrop.Blue:X2}{backdrop.Alpha:X2}" +
                                $"  [{string.Join(", ", backdrop.Values.Select(F))}]");

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Renders every placement as CSV, one row each.</summary>
    public string DescribePlacements(IReadOnlyList<DataDirectoryEntry> entries)
    {
        var text = new StringBuilder();
        text.AppendLine("area,entry,name,x,y,z,parameter0,parameter1,parameter2");
        foreach (StageArea area in Areas)
        {
            foreach (StagePlacement p in area.Placements)
            {
                string name = p.Entry < entries.Count ? entries[p.Entry].Name : "";
                text.AppendLine($"{area.Index},{p.Entry},{name},{F(p.X)},{F(p.Y)},{F(p.Z)}," +
                                $"{p.Parameter0},{p.Parameter1},{p.Parameter2}");
            }
        }

        return text.ToString();
    }

    private static string F(float value) =>
        float.IsFinite(value) ? value.ToString("0.#####", CultureInfo.InvariantCulture)
                              : value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One region of a stage: everything placed in it.</summary>
public sealed class StageArea
{
    /// <summary>Position in the area list.</summary>
    public required int Index { get; init; }

    /// <summary>Offset of the area within the level container.</summary>
    public required int Offset { get; init; }

    /// <summary>The 64-byte head, verbatim. Much of it past 0x20 is the writing tool's stale buffer.</summary>
    public required byte[] Head { get; init; }

    /// <summary>Head word at 0x00. Role not established.</summary>
    public uint Word0 { get; set; }

    /// <summary>Head word at 0x04. Role not established.</summary>
    public uint Word1 { get; set; }

    /// <summary>
    /// Head float at 0x08. A point in the same space as the placements; <c>map.dat</c> and
    /// <c>bg.dat</c> agree on it area for area, so it is a property of the area rather than of
    /// either file. What it marks is not established.
    /// </summary>
    public float PointX { get; set; }

    /// <summary>Head float at 0x0C, stored positive where placement y is negative.</summary>
    public float PointY { get; set; }

    /// <summary>Head word at 0x10: 0, 2, 4, 6 or 7 in the reference data. Role not established.</summary>
    public uint Word4 { get; set; }

    /// <summary>What is placed in this area.</summary>
    public List<StagePlacement> Placements { get; } = [];

    /// <summary>Backdrop records, which only <c>bg.dat</c> carries.</summary>
    public List<StageBackdrop> Backdrops { get; } = [];
}

/// <summary>One placed object: which asset, and where.</summary>
/// <param name="Entry">Index into the <see cref="DataDirectory"/> in front of the level container.</param>
/// <param name="X">Horizontal position, on a five-unit grid.</param>
/// <param name="Y">Vertical position; negative, because the stage hangs below the origin.</param>
/// <param name="Z">Depth: zero throughout <c>map.dat</c>, the parallax offset in <c>bg.dat</c>.</param>
/// <param name="Parameter0">First object-specific parameter byte.</param>
/// <param name="Parameter1">Second.</param>
/// <param name="Parameter2">Third.</param>
public readonly record struct StagePlacement(
    int Entry, float X, float Y, float Z,
    byte Parameter0, byte Parameter1, byte Parameter2)
{
    /// <summary>True when this placement carries a parameter, which 2% of them do.</summary>
    public bool HasParameter => Parameter0 != 0 || Parameter1 != 0 || Parameter2 != 0;
}

/// <summary>
/// A record that only <c>bg.dat</c> carries: a colour and four floats, written in identical pairs.
/// A good number of them are uninitialised - their floats run to millions - so what the game does
/// with them is reported rather than interpreted.
/// </summary>
public sealed class StageBackdrop
{
    /// <summary>Red channel.</summary>
    public required byte Red { get; init; }

    /// <summary>Green channel.</summary>
    public required byte Green { get; init; }

    /// <summary>Blue channel.</summary>
    public required byte Blue { get; init; }

    /// <summary>Alpha channel: 0xFF or 0x00 throughout.</summary>
    public required byte Alpha { get; init; }

    /// <summary>The four floats. The first two read as a position, the fourth as a range.</summary>
    public required float[] Values { get; init; }

    /// <summary>Word at 0x18: 1 on the first record of a pair, 0 on the second.</summary>
    public required uint WordA { get; init; }

    /// <summary>Word at 0x1C: 16 on the first record of a pair, 1 on the second.</summary>
    public required uint WordB { get; init; }

    /// <summary>True when the floats are finite and small enough to be a position rather than junk.</summary>
    public bool LooksInitialised =>
        Values.All(v => float.IsFinite(v) && Math.Abs(v) < 100_000f);
}
