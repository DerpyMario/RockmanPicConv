using PacTool.Extract;
using PacTool.Formats;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The stage layout: the container that follows a directory, its areas, and the placements that
/// tie the two halves of a stage block together.
/// </summary>
public class StageLevelTests
{
    [Fact]
    public void AnAreaIsAHeadThenPlacementsThenBackdrops()
    {
        byte[] block = Block([
            Area(pointX: 517, pointY: 119, word0: 11, word1: 4, word4: 2,
                 placements: [
                     Placement(0, 55, -205, 0),
                     Placement(2, 65, -205, 0, 100, 1, 3),
                 ],
                 backdrops: [Backdrop(0x40, 0x40, 0x40, 0xFF, [50, -265, 0, 104], 1, 16),
                             Backdrop(0x40, 0x40, 0x40, 0xFF, [50, -265, 0, 104], 0, 1)]),
        ]);

        var warnings = new List<string>();
        StageLevel level = StageLevel.Parse(block, "map.dat", warnings);

        Assert.Empty(warnings);
        StageArea area = Assert.Single(level.Areas);
        Assert.Equal(517f, area.PointX);
        Assert.Equal(119f, area.PointY);
        Assert.Equal(11u, area.Word0);
        Assert.Equal(4u, area.Word1);
        Assert.Equal(2u, area.Word4);

        Assert.Equal(2, area.Placements.Count);
        Assert.Equal(new StagePlacement(0, 55, -205, 0, 0, 0, 0), area.Placements[0]);
        Assert.False(area.Placements[0].HasParameter);

        Assert.Equal(new StagePlacement(2, 65, -205, 0, 100, 1, 3), area.Placements[1]);
        Assert.True(area.Placements[1].HasParameter);

        // Two stored records, one backdrop: the pair shares everything but its trailing words.
        Assert.Equal(2, area.BackdropRecords);
        StageBackdrop backdrop = Assert.Single(area.Backdrops);
        Assert.Equal((0x40, 0x40, 0x40, 0xFF), (backdrop.Red, backdrop.Green, backdrop.Blue, backdrop.Alpha));
        Assert.Equal([50f, -265f, 0f, 104f], backdrop.Values);
        Assert.Equal((50f, -265f), (backdrop.X, backdrop.Y));
        Assert.Equal((1u, 16u, 0u, 1u),
                     (backdrop.FirstWordA, backdrop.FirstWordB, backdrop.SecondWordA, backdrop.SecondWordB));
        Assert.True(backdrop.BodiesMatch);
        Assert.True(backdrop.LooksInitialised);
        Assert.False(area.IsUnused);
    }

    [Fact]
    public void AnUnfilledBackdropIsToldApartFromARealOne()
    {
        // 45 of the 182 pairs in the reference data were never written; their floats run to
        // millions, which is what separates them from a position.
        byte[] block = Block([
            Area(backdrops: [
                Backdrop(0x40, 0x40, 0x40, 0xFF, [1275334.5f, 743009.06f, 497375.47f, 43431676f], 1, 16),
                Backdrop(0x40, 0x40, 0x40, 0xFF, [1275334.5f, 743009.06f, 497375.47f, 43431676f], 0, 1),
                Backdrop(0x00, 0x40, 0x40, 0xFF, [50, -265, 0, 104], 1, 16),
                Backdrop(0x00, 0x40, 0x40, 0xFF, [50, -265, 0, 104], 0, 1),
            ]),
        ]);

        StageArea area = Assert.Single(StageLevel.Parse(block, "bg.dat", []).Areas);

        Assert.Equal(2, area.Backdrops.Count);
        Assert.False(area.Backdrops[0].LooksInitialised);
        Assert.True(area.Backdrops[1].LooksInitialised);
    }

    [Fact]
    public void AnAreaWithWordFourZeroIsMarkedUnused()
    {
        // All twelve such areas in the reference data are empty, and every implausible pointX in
        // the corpus belongs to one of them.
        byte[] block = Block([
            Area(word4: 0, pointX: 30755, pointY: 160),
            Area(word4: 2, pointX: 450, pointY: 160, placements: [Placement(0, 0, 0, 0)]),
        ]);

        StageLevel level = StageLevel.Parse(block, "map.dat", []);

        Assert.True(level.Areas[0].IsUnused);
        Assert.False(level.Areas[1].IsUnused);
        Assert.Contains("unused", level.Describe("test", []));
    }

    [Fact]
    public void AnOddBackdropCountIsReported()
    {
        byte[] block = Block([Area(backdrops: [Backdrop(1, 2, 3, 4, [0, 0, 0, 1], 1, 16)])]);

        var warnings = new List<string>();
        StageLevel level = StageLevel.Parse(block, "bg.dat", warnings);

        Assert.Empty(Assert.Single(level.Areas).Backdrops);
        Assert.Contains(warnings, w => w.Contains("does not divide into pairs"));
    }

    [Fact]
    public void AnAreaIsExactlySixtyFourPlusItsRecords()
    {
        // This is what fixes both record sizes: nothing else reproduces the stored area lengths.
        byte[] block = Block([
            Area(placements: [Placement(0, 0, 0, 0), Placement(1, 10, 0, 0), Placement(2, 20, 0, 0)],
                 backdrops: [Backdrop(1, 2, 3, 4, [0, 0, 0, 1], 1, 16),
                             Backdrop(1, 2, 3, 4, [0, 0, 0, 1], 0, 1)]),
        ]);

        Assert.Equal(StageLevel.HeaderSize + 4 + StageLevel.AreaHeadSize +
                     3 * StageLevel.PlacementSize + 2 * StageLevel.BackdropSize, block.Length);
        Assert.Equal(3, StageLevel.Parse(block, "map.dat", []).PlacementCount);
    }

    [Fact]
    public void SeveralAreasEachKeepTheirOwnRecords()
    {
        byte[] block = Block([
            Area(pointX: 1, placements: [Placement(0, 1, 0, 0)]),
            Area(pointX: 2, placements: [Placement(1, 2, 0, 0), Placement(2, 3, 0, 0)]),
            Area(pointX: 3),
        ]);

        StageLevel level = StageLevel.Parse(block, "map.dat", []);

        Assert.Equal([1f, 2f, 3f], level.Areas.Select(a => a.PointX));
        Assert.Equal([1, 2, 0], level.Areas.Select(a => a.Placements.Count));
        Assert.Equal([0, 1, 2], level.Areas.Select(a => a.Index));
    }

    [Fact]
    public void ArchivePaddingIsNotMistakenForALayout()
    {
        // A block with no layout is followed by the archive's own alignment padding, and reading
        // that as a container would invent areas out of nothing.
        Assert.False(StageLevel.LooksLikeLevel(new byte[24]));
        Assert.False(StageLevel.LooksLikeLevel([0x00, 0x00, 0x62, 0x30, 0x30, 0x62, 0x6C, 0x6B]));

        // The first offset has to land exactly at the end of the offset table.
        byte[] wrong = new BigEndianWriter().U32(64).U32(1).U32(8).Zeros(56).ToArray();
        Assert.False(StageLevel.LooksLikeLevel(wrong));
    }

    [Fact]
    public void AnAreaWhoseCountsDoNotFitIsReadAsFarAsItGoes()
    {
        byte[] block = Block([Area(placements: [Placement(0, 0, 0, 0)])]);

        // Claim four placements where there is room for one.
        block[block.Length - StageLevel.AreaHeadSize - StageLevel.PlacementSize + 0x1B] = 4;

        var warnings = new List<string>();
        StageLevel level = StageLevel.Parse(block, "map.dat", warnings);

        Assert.Single(Assert.Single(level.Areas).Placements);
        Assert.Contains(warnings, w => w.Contains("announces 4 placement(s)"));
    }

    [Fact]
    public void ADirectoryCarriesItsLayoutAlongWithItsEntries()
    {
        byte[] entries = Directory([("b00blkmm", 14, 5, -5, -5, 5), ("b00movmm", 14, 0, -10, -5, 5)]);
        byte[] layout = Block([
            Area(placements: [Placement(0, 100, -200, 0), Placement(1, 110, -200, 0)]),
        ]);

        DataDirectory parsed = DataDirectory.Parse([.. entries, .. layout], "map.dat");

        Assert.Empty(parsed.Warnings);
        Assert.Equal(["b00blkmm", "b00movmm"], parsed.Entries.Select(e => e.Name));

        DataDirectoryBox box = Assert.Single(parsed.Entries[0].Boxes);
        Assert.Equal(14u, box.Kind);
        Assert.Equal((5f, -5f, -5f, 5f), (box.Top, box.Bottom, box.Left, box.Right));
        Assert.Equal((10f, 10f), (box.Width, box.Height));

        StageLevel level = Assert.IsType<StageLevel>(parsed.Level);
        Assert.Equal(2, Assert.Single(level.Areas).Placements.Count);
    }

    [Fact]
    public void ABlockWithoutALayoutKeepsItsPaddingOutOfTheWay()
    {
        byte[] block = [.. Directory([("b00blkmm", 14, 5, -5, -5, 5)]), 0, 0, 0, 0, 0, 0, 0, 0];

        DataDirectory parsed = DataDirectory.Parse(block, "map.dat");

        Assert.Null(parsed.Level);
        Assert.Equal(8, parsed.Trailing.Length);
    }

    [Fact]
    public void TheListingResolvesEachPlacementToItsAsset()
    {
        byte[] entries = Directory([("b00blkmm", 14, 5, -5, -5, 5)]);
        byte[] layout = Block([Area(placements: [Placement(0, 100, -200, 0), Placement(9, 0, 0, 0)])]);
        DataDirectory parsed = DataDirectory.Parse([.. entries, .. layout], "map.dat");

        string listing = parsed.Level!.Describe("test", parsed.Entries);

        Assert.Contains("'b00blkmm'", listing);
        Assert.Contains("(100, -200, 0)", listing);
        Assert.Contains("past the end of the directory", listing);
    }

    [Fact]
    public void TheCsvHasOneRowPerPlacement()
    {
        byte[] entries = Directory([("b00blkmm", 14, 5, -5, -5, 5)]);
        byte[] layout = Block([Area(placements: [Placement(0, 100, -200, 0, 0, 1, 2)])]);
        DataDirectory parsed = DataDirectory.Parse([.. entries, .. layout], "map.dat");

        string[] lines = parsed.Level!.DescribePlacements(parsed.Entries)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("area,entry,name,x,y,z,parameter0,parameter1,parameter2", lines[0].TrimEnd('\r'));
        Assert.Equal("0,0,b00blkmm,100,-200,0,0,1,2", lines[1].TrimEnd('\r'));
    }

    [Fact]
    public void EachBoxIsDrawnWhereTheLayoutPutsIt()
    {
        // The whole chain in one check: the box comes from the directory, the position from the
        // layout, and the quad has to end up at their sum.
        byte[] entries = Directory([("b00blkmm", 14, 5, -5, -5, 5)]);
        byte[] layout = Block([Area(placements: [Placement(0, 100, -200, 0)])]);
        DataDirectory parsed = DataDirectory.Parse([.. entries, .. layout], "map.dat");

        string obj = Assert.IsType<string>(StageLayoutWriter.Build(parsed.Level!.Areas[0], parsed.Entries));
        string[] lines = obj.Split('\n').Select(l => l.Trim()).ToArray();

        Assert.Contains("v 95 -205 0", lines);
        Assert.Contains("v 105 -205 0", lines);
        Assert.Contains("v 105 -195 0", lines);
        Assert.Contains("v 95 -195 0", lines);
        Assert.Contains("f 1 2 3 4", lines);
        Assert.Contains("g b00blkmm_kind14", lines);
    }

    [Fact]
    public void AnUninitialisedRecordIsNotDrawn()
    {
        // An area left uninitialised would otherwise contribute a quad a million units across.
        byte[] entries = Directory([("wild", 14, 1e30f, -1e30f, -1e30f, 1e30f)]);
        byte[] layout = Block([Area(placements: [Placement(0, 0, 0, 0)])]);
        DataDirectory parsed = DataDirectory.Parse([.. entries, .. layout], "map.dat");

        Assert.Null(StageLayoutWriter.Build(parsed.Level!.Areas[0], parsed.Entries));
    }

    [Fact]
    public void EnemySpawnsCarryAModelAPositionAndAFacing()
    {
        byte[] block = new BigEndianWriter()
            .U32(8 + 4 + 4 + 24 * 2)
            .U32(1)
            .U32(4)
            .U32(2)
            .U8(0).Ascii("D2d").F32(485).F32(-250).F32(0).F32(-1).F32(0)
            .U8(0).Ascii("C0b").F32(340).F32(-90).F32(0).F32(1).F32(0)
            .ToArray();

        DataDirectory parsed = DataDirectory.Parse(block, "enemy.dat");

        Assert.Equal(DataDirectoryKind.Enemy, parsed.Kind);
        DataDirectoryEntry area = Assert.Single(parsed.Entries);
        Assert.Equal(2, area.Spawns.Count);

        Assert.Equal("D2d", area.Spawns[0].Model);
        Assert.Equal((485f, -250f, 0f), (area.Spawns[0].X, area.Spawns[0].Y, area.Spawns[0].Z));
        Assert.Equal(-1f, area.Spawns[0].Facing);
        Assert.Equal(1f, area.Spawns[1].Facing);

        Assert.Contains("facing left", parsed.Describe("enemy.dat"));
        Assert.Contains("facing right", parsed.Describe("enemy.dat"));
    }

    /// <summary>A level container: a size, a count, offsets relative to 0x08, then the areas.</summary>
    internal static byte[] Block(IReadOnlyList<byte[]> areas)
    {
        var writer = new BigEndianWriter().U32(0).U32(areas.Count);
        int at = areas.Count * 4;
        foreach (byte[] area in areas)
        {
            writer.U32(at);
            at += area.Length;
        }

        foreach (byte[] area in areas)
            writer.Bytes(area);

        return writer.PatchU32(0, writer.Length).ToArray();
    }

    /// <summary>One area: a 64-byte head, then its placements and backdrops.</summary>
    internal static byte[] Area(float pointX = 450, float pointY = 160,
                                uint word0 = 1, uint word1 = 1, uint word4 = 2,
                                IReadOnlyList<byte[]>? placements = null,
                                IReadOnlyList<byte[]>? backdrops = null)
    {
        placements ??= [];
        backdrops ??= [];
        var writer = new BigEndianWriter()
            .U32(word0).U32(word1)
            .F32(pointX).F32(pointY)
            .U32(word4)
            .U32(0)                                     // stale in the reference data
            .U32(placements.Count).U32(backdrops.Count)
            .Zeros(StageLevel.AreaHeadSize - 0x20);

        foreach (byte[] p in placements)
            writer.Bytes(p);
        foreach (byte[] b in backdrops)
            writer.Bytes(b);

        return writer.ToArray();
    }

    internal static byte[] Placement(int entry, float x, float y, float z,
                                     int p0 = 0, int p1 = 0, int p2 = 0) =>
        new BigEndianWriter()
            .U8(entry).Zeros(3)
            .F32(x).F32(y).F32(z)
            .U8(0).U8(p0).U8(p1).U8(p2)
            .ToArray();

    internal static byte[] Backdrop(int r, int g, int b, int a, float[] values, uint wordA, uint wordB)
    {
        var writer = new BigEndianWriter().U32(0).U8(r).U8(g).U8(b).U8(a);
        foreach (float v in values)
            writer.F32(v);

        return writer.U32(wordA).U32(wordB).ToArray();
    }

    /// <summary>A directory of named assets, each with one collision box.</summary>
    internal static byte[] Directory(
        IReadOnlyList<(string Name, uint Kind, float Top, float Bottom, float Left, float Right)> entries)
    {
        var bodies = new List<byte[]>();
        foreach ((string name, uint kind, float top, float bottom, float left, float right) in entries)
        {
            bodies.Add(new BigEndianWriter()
                .U16(0).Field(name, 8).Zeros(6)
                .U32(1)
                .U32(kind).U32(0)
                .F32(top).F32(bottom).F32(left).F32(right)
                .Zeros(16)
                .ToArray());
        }

        var writer = new BigEndianWriter().U32(0).U32(bodies.Count);
        int at = bodies.Count * 4;
        foreach (byte[] body in bodies)
        {
            writer.U32(at);
            at += body.Length;
        }

        foreach (byte[] body in bodies)
            writer.Bytes(body);

        return writer.PatchU32(0, writer.Length).ToArray();
    }
}
