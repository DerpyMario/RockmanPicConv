using PacTool.Formats;
using PacTool.Gx;
using PacTool.Imaging;
using Xunit;

namespace PacTool.Tests;

public class MpcModelTests
{
    [Fact]
    public void TheSkeletonIsReadDepthFirstWithItsDepthsDerivedFromTheTypes()
    {
        byte[] model = Build("chn22", [
            (0x01, "a01bon00", 0x1890, 0),
            (0x02, "eff22", 0, 3),
            (0x03, "chn1", 0, 0),
            (0x04, "body", 0x0946, 0),
            (0x05, "eff1", 0, 0x62),
        ], meshBytes: 64);

        Assert.True(MpcModel.LooksLikeModel(model));
        MpcModel parsed = MpcModel.Parse(model, "a01xxxxx.mpc");

        Assert.Empty(parsed.Warnings);
        Assert.Equal("chn22", parsed.RootName);
        Assert.Equal(64, parsed.MeshData.Length);
        Assert.Equal(5 * MpcModel.EntrySize, parsed.SkeletonData.Length);

        // The header's entry-shaped record is joint 0, so the table's five entries make six nodes
        // and every one of their depths sits one below it.
        Assert.Equal(6, parsed.Nodes.Count);
        Assert.Equal("chn22", parsed.Nodes[0].Name);
        Assert.Equal(MpcNodeType.RootChain, parsed.Nodes[0].Type);

        Assert.Equal([0, 1, 2, 2, 3, 4], parsed.Nodes.Select(n => n.Depth));
        Assert.Equal(
            [MpcNodeCategory.Chain, MpcNodeCategory.Bone, MpcNodeCategory.Effect,
             MpcNodeCategory.Chain, MpcNodeCategory.Part, MpcNodeCategory.Effect],
            parsed.Nodes.Select(n => n.Category));

        Assert.Equal("a01bon00", parsed.Nodes[1].Name);
        Assert.Equal(0x1890, parsed.Nodes[1].GeometryWord);
        Assert.Equal(0x62, parsed.Nodes[5].Parameter);
    }

    [Fact]
    public void TheListingShowsMeshOffsetsInBytesAsWellAsWords()
    {
        byte[] model = Build("chn5", [(0x01, "bone", 0x1890, 0)], meshBytes: 16);

        string listing = MpcModel.Parse(model, "test.mpc").DescribeSkeleton("test.mpc");

        Assert.Contains("word 0x1890", listing);
        Assert.Contains("byte 0x03120", listing);
    }

    [Fact]
    public void AnEntryCountBiggerThanTheFileIsClampedAndReported()
    {
        byte[] model = Build("chn5", [(0x01, "bone", 0, 0)], meshBytes: 0);
        model[3] = 0x40;                                // claim 64 entries

        MpcModel parsed = MpcModel.Parse(model, "test.mpc");

        // What is left is the header's record and the one entry the file actually holds.
        Assert.Equal(2, parsed.Nodes.Count);
        Assert.Contains(parsed.Warnings, w => w.Contains("truncating"));
    }

    [Fact]
    public void SomethingWithoutARootBoneIsNotTakenForAModel()
    {
        byte[] model = Build("chn5", [(0x04, "part", 0, 0)], meshBytes: 0);

        Assert.False(MpcModel.LooksLikeModel(model));
    }

    private static byte[] Build(string root, IReadOnlyList<(int Type, string Name, int Geometry, int Parameter)> nodes,
                                int meshBytes)
    {
        // The stored count takes in the header's record as well as the table.
        var writer = new BigEndianWriter()
            .U32(nodes.Count + 1)
            .U32(0xFD00)
            .Zeros(3).Field(root, 8).Zeros(2)           // the root chain, in an entry-shaped record
            .PadTo(MpcModel.EntryTableOffset);

        foreach ((int type, string name, int geometry, int parameter) in nodes)
        {
            writer.U16(0).U8(type).Field(name, 8).Zeros(5)
                  .U16(0).U16(0)
                  .U16(geometry).U16(parameter);
        }

        return writer.Zeros(meshBytes).ToArray();
    }
}

public class DataDirectoryTests
{
    [Fact]
    public void MapEntriesCarryAsManyCollisionBoxesAsTheirHeadAnnounces()
    {
        byte[] block = Build("map.dat", [
            Entry("b00blkmm", 0, [(14, [5f, -5f, -5f, 5f])]),
            Entry("b00togpu", 0, [(5, [0f, -8f, -2f, 2f])]),
            Entry("b00bardl", 0, []),
        ]);

        DataDirectory parsed = DataDirectory.Parse(block, "map.dat");

        Assert.Empty(parsed.Warnings);
        Assert.Equal(DataDirectoryKind.Map, parsed.Kind);
        Assert.Equal(3, parsed.Entries.Count);

        Assert.Equal("b00blkmm", parsed.Entries[0].Name);
        DataDirectoryBox box = Assert.Single(parsed.Entries[0].Boxes);
        Assert.Equal(14u, box.Kind);
        Assert.Equal((5f, -5f, -5f, 5f), (box.Top, box.Bottom, box.Left, box.Right));
        Assert.Equal((10f, 10f), (box.Width, box.Height));

        Assert.Equal(5u, parsed.Entries[1].Boxes[0].Kind);
        Assert.Empty(parsed.Entries[2].Boxes);
    }

    [Fact]
    public void EntryLengthsFollowTheTwentyPlusFortyRule()
    {
        // 20 + boxes * 40 is what every map and background entry in the shipped data measures.
        byte[] block = Build("map.dat", [
            Entry("six", 0, Enumerable.Repeat((11u, new[] { 1f, 2f, 3f, 4f }), 6).ToList()),
        ]);

        DataDirectoryEntry entry = Assert.Single(DataDirectory.Parse(block, "map.dat").Entries);

        Assert.Equal(20 + 6 * 40, entry.Raw.Length);
        Assert.Equal(6, entry.Boxes.Count);
    }

    [Fact]
    public void BackgroundEntriesAreNamesOnly()
    {
        byte[] block = Build("bg.dat", [
            Entry("b00stram", 0x1000, []),
            Entry("b0b1bgxa", 0x1000, []),
        ]);

        DataDirectory parsed = DataDirectory.Parse(block, "bg.dat");

        Assert.Equal(DataDirectoryKind.Background, parsed.Kind);
        Assert.All(parsed.Entries, e => Assert.Equal(0x1000, e.Flags));
        Assert.All(parsed.Entries, e => Assert.Equal(20, e.Raw.Length));
    }

    [Fact]
    public void EnemyEntriesAreSpawnLists()
    {
        byte[] block = Build("enemy.dat", [
            SpawnList([("D2d", [485f, -250f, 0f, -1f, 0f]), ("D39", [785f, -260f, 0f, -1f, 0f])]),
            SpawnList([]),
        ]);

        DataDirectory parsed = DataDirectory.Parse(block, "enemy.dat");

        Assert.Equal(DataDirectoryKind.Enemy, parsed.Kind);
        Assert.Equal(2, parsed.Entries[0].Spawns.Count);
        Assert.Equal("D2d", parsed.Entries[0].Spawns[0].Model);
        Assert.Equal(485f, parsed.Entries[0].Spawns[0].Values[0]);
        Assert.Equal(4, parsed.Entries[1].Raw.Length);
        Assert.Empty(parsed.Entries[1].Spawns);
    }

    [Fact]
    public void LevelDataAfterTheDirectoryIsKeptSeparate()
    {
        byte[] directory = Build("map.dat", [Entry("only", 0, [])]);
        byte[] block = [.. directory, .. Enumerable.Repeat((byte)0xAB, 100)];

        DataDirectory parsed = DataDirectory.Parse(block, "map.dat");

        Assert.Equal(100, parsed.Trailing.Length);
        Assert.All(parsed.Trailing, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void ADirectorySizeBeyondTheBlockIsClampedAndReported()
    {
        byte[] block = Build("enemy.dat", [SpawnList([])]);
        block[3] = 0xFF;                                // a table size past the end of the block

        DataDirectory parsed = DataDirectory.Parse(block, "enemy.dat");

        Assert.Contains(parsed.Warnings, w => w.Contains("past the end of the block"));
        Assert.Single(parsed.Entries);
    }

    [Fact]
    public void OnlyTheThreeKnownNamesAreTreatedAsDirectories()
    {
        Assert.True(DataDirectory.IsKnownName("map.dat"));
        Assert.True(DataDirectory.IsKnownName("enemy.dat"));
        Assert.False(DataDirectory.IsKnownName("playdemo0.dat"));
    }

    private static byte[] Entry(string name, int flags, IReadOnlyList<(uint Kind, float[] Bounds)> placements)
    {
        var writer = new BigEndianWriter().U16(flags).Field(name, 8).Zeros(6).U32(placements.Count);
        foreach ((uint kind, float[] bounds) in placements)
        {
            writer.U32(kind).U32(0);
            foreach (float value in bounds)
                writer.F32(value);
            writer.Zeros(16);
        }

        return writer.ToArray();
    }

    private static byte[] SpawnList(IReadOnlyList<(string Model, float[] Values)> spawns)
    {
        var writer = new BigEndianWriter().U32(spawns.Count);
        foreach ((string model, float[] values) in spawns)
        {
            writer.U8(0).Field(model, 3);
            foreach (float value in values)
                writer.F32(value);
        }

        return writer.ToArray();
    }

    private static byte[] Build(string name, IReadOnlyList<byte[]> entries)
    {
        _ = name;
        int tableSize = DataDirectory.HeaderSize + entries.Count * 4 + entries.Sum(e => e.Length);
        var writer = new BigEndianWriter().U32(tableSize).U32(entries.Count);

        int offset = entries.Count * 4;
        foreach (byte[] entry in entries)
        {
            writer.U32(offset);
            offset += entry.Length;
        }

        foreach (byte[] entry in entries)
            writer.Bytes(entry);

        return writer.ToArray();
    }
}

public class SoftimagePicTests
{
    [Fact]
    public void LiteralAndRepeatedRunsBothDecode()
    {
        // One 4x1 row: two literal pixels, then a run of two.
        byte[] pic = Build(4, 1, rgb: writer => writer
            .U8(1).Bytes([10, 11, 12]).Bytes([20, 21, 22])   // lead 1: two literal pixels
            .U8(129).Bytes([30, 31, 32]),                    // lead 129: a run of 129 - 127 = 2
            alpha: writer => writer.U8(131).U8(255));        // a run of four opaque pixels

        SoftimagePic parsed = SoftimagePic.Parse(pic, "test.pic");

        Assert.Equal(4, parsed.Width);
        Assert.Equal((10, 11, 12, 255), Pixel(parsed.Image, 0, 0));
        Assert.Equal((20, 21, 22, 255), Pixel(parsed.Image, 1, 0));
        Assert.Equal((30, 31, 32, 255), Pixel(parsed.Image, 2, 0));
        Assert.Equal((30, 31, 32, 255), Pixel(parsed.Image, 3, 0));
    }

    [Fact]
    public void ALongRunTakesItsLengthFromTheNextTwoBytes()
    {
        byte[] pic = Build(300, 1,
            rgb: writer => writer.U8(128).U16(300).Bytes([7, 8, 9]),
            alpha: writer => writer.U8(128).U16(300).U8(64));

        SoftimagePic parsed = SoftimagePic.Parse(pic, "test.pic");

        Assert.Equal((7, 8, 9, 64), Pixel(parsed.Image, 0, 0));
        Assert.Equal((7, 8, 9, 64), Pixel(parsed.Image, 299, 0));
    }

    [Fact]
    public void ARunThatWouldOverrunTheScanLineIsRejected()
    {
        byte[] pic = Build(4, 1,
            rgb: writer => writer.U8(200).Bytes([1, 2, 3]),  // a run of 73 into a four-pixel row
            alpha: writer => writer.U8(131).U8(255));

        Assert.Throws<PacFormatException>(() => SoftimagePic.Parse(pic, "test.pic"));
    }

    [Fact]
    public void TheCommentAndChannelLayoutAreReported()
    {
        byte[] pic = Build(2, 1,
            rgb: writer => writer.U8(129).Bytes([1, 2, 3]),
            alpha: writer => writer.U8(129).U8(255));

        SoftimagePic parsed = SoftimagePic.Parse(pic, "test.pic");

        Assert.Equal("built for a test", parsed.Comment);
        Assert.Equal(2, parsed.Channels.Count);
        Assert.Contains("RGB RLE + A RLE", parsed.Describe());
    }

    [Fact]
    public void SomethingWithoutThePictTagIsRejected()
    {
        Assert.False(SoftimagePic.LooksLikePic(new byte[200]));
        Assert.Throws<PacFormatException>(() => SoftimagePic.Parse(new byte[200], "test.pic"));
    }

    private static (byte, byte, byte, byte) Pixel(Rgba32Image image, int x, int y) =>
        GxImageDecoderTests.PixelAt(image, x, y);

    /// <summary>A PIC of one flat colour, for tests that only care that there is an image.</summary>
    internal static byte[] Solid(int width, int height) => Build(width, height,
        rgb: writer => writer.U8(127 + width).Bytes([64, 128, 192]),
        alpha: writer => writer.U8(127 + width).U8(255));

    private static byte[] Build(int width, int height, Action<BigEndianWriter> rgb, Action<BigEndianWriter> alpha)
    {
        var writer = new BigEndianWriter()
            .U32(0x5380F634)
            .F32(2.62f)
            .Field("built for a test", 80)
            .Ascii("PICT")
            .U16(width).U16(height)
            .F32(1f)
            .U16(3).U16(0)
            .U8(1).U8(8).U8(2).U8(0xE0)                 // chained RGB packet, run-length encoded
            .U8(0).U8(8).U8(2).U8(0x10);                // final alpha packet

        for (int y = 0; y < height; y++)
        {
            rgb(writer);
            alpha(writer);
        }

        return writer.ToArray();
    }
}

public class Yaz0Tests
{
    [Fact]
    public void LiteralsAndBackReferencesBothDecompress()
    {
        // "abcabcabc": three literals, then a back-reference that overlaps its own source.
        byte[] compressed = new BigEndianWriter()
            .Ascii("Yaz0").U32(9).Zeros(8)
            .U8(0b1110_0000)
            .Ascii("abc")
            .U16((4 << 12) | (3 - 1))                   // six bytes, three back
            .ToArray();

        byte[] decompressed = Yaz0.Decompress(compressed, "test");

        Assert.Equal("abcabcabc"u8.ToArray(), decompressed);
    }

    [Fact]
    public void ALongBackReferenceTakesItsLengthFromAThirdByte()
    {
        byte[] compressed = new BigEndianWriter()
            .Ascii("Yaz0").U32(20).Zeros(8)
            .U8(0b1100_0000)
            .Ascii("xy")
            .U16(0x0001).U8(18 - 0x12)                  // length nibble 0, so a third byte follows
            .ToArray();

        byte[] decompressed = Yaz0.Decompress(compressed, "test");

        Assert.Equal(20, decompressed.Length);
        Assert.Equal("xyxyxyxyxyxyxyxyxyxy"u8.ToArray(), decompressed);
    }

    [Fact]
    public void ContentIsIdentifiedThroughAYaz0Wrapper()
    {
        byte[] inner = PicturePackTests.Build([
            PicturePackTests.Texture("tex", GxTextureFormat.Cmpr, 8, 8, maxLod: 0),
        ]);
        byte[] compressed = CompressAsLiterals(inner);

        ContentKind kind = ContentSniffer.Identify("thing.bin", compressed, out bool wasCompressed);

        Assert.True(wasCompressed);
        Assert.Equal(ContentKind.PicturePack, kind);
        Assert.Equal(inner, Yaz0.Decompress(compressed, "thing.bin"));
    }

    [Fact]
    public void ATruncatedStreamIsReportedRatherThanReturningShortData()
    {
        byte[] compressed = new BigEndianWriter().Ascii("Yaz0").U32(16).Zeros(8).U8(0xFF).Ascii("ab").ToArray();

        Assert.Throws<PacFormatException>(() => Yaz0.Decompress(compressed, "test"));
    }

    /// <summary>Wraps data in a Yaz0 stream that stores every byte as a literal.</summary>
    private static byte[] CompressAsLiterals(byte[] data)
    {
        var writer = new BigEndianWriter().Ascii("Yaz0").U32(data.Length).Zeros(8);
        for (int i = 0; i < data.Length; i += 8)
        {
            writer.U8(0xFF);
            writer.Bytes(data.AsSpan(i, Math.Min(8, data.Length - i)));
        }

        return writer.ToArray();
    }
}
