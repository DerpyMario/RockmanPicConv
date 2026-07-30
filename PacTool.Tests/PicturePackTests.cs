using PacTool.Formats;
using PacTool.Gx;
using PacTool.Imaging;
using Xunit;

namespace PacTool.Tests;

public class PicturePackTests
{
    [Fact]
    public void APlainPackParsesItsTexturesAndHasNoSceneTable()
    {
        byte[] pack = Build([
            Texture("first", GxTextureFormat.Cmpr, 8, 8, maxLod: 3),
            Texture("second", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0),
        ]);

        PicturePack parsed = PicturePack.Parse(pack, "test.pcp");

        Assert.Empty(parsed.Warnings);
        Assert.Null(parsed.Scene);
        Assert.Equal(2, parsed.Textures.Count);

        Assert.Equal("first", parsed.Textures[0].Name);
        Assert.Equal(GxTextureFormat.Cmpr, parsed.Textures[0].Format);
        Assert.Equal(4, parsed.Textures[0].MipLevels);
        Assert.Equal(128, parsed.Textures[0].Data.Length);

        Assert.Equal("second", parsed.Textures[1].Name);
        Assert.Equal(1, parsed.Textures[1].MipLevels);
        Assert.Equal(32, parsed.Textures[1].Data.Length);
    }

    [Fact]
    public void MipLevelsComeFromThePayloadLengthNotOnlyFromMaxLod()
    {
        // A header that claims four levels but only carries the base one is reported, not trusted.
        byte[] pack = Build([Texture("odd", GxTextureFormat.Cmpr, 8, 8, maxLod: 3, levels: 1)]);

        PicturePack parsed = PicturePack.Parse(pack, "test.pcp");

        Assert.Equal(1, parsed.Textures[0].MipLevels);
        Assert.Contains(parsed.Warnings, w => w.Contains("maxLod 3") && w.Contains("1 level"));
    }

    [Fact]
    public void EachMipLevelDecodesAtItsOwnSize()
    {
        byte[] pack = Build([Texture("chain", GxTextureFormat.Cmpr, 16, 16, maxLod: 4)]);

        PicturePackTexture texture = PicturePack.Parse(pack, "test.pcp").Textures[0];

        Assert.Equal(5, texture.MipLevels);
        Assert.Equal((16, 16), Size(texture.Decode(0)));
        Assert.Equal((8, 8), Size(texture.Decode(1)));
        Assert.Equal((4, 4), Size(texture.Decode(2)));
        Assert.Equal((1, 1), Size(texture.Decode(4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => texture.Decode(5));
    }

    [Fact]
    public void ASceneTableAfterTheTexturesIsPickedUp()
    {
        var textures = new List<byte[]> { Texture("tex", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0) };
        var scene = new BigEndianWriter()
            .U16(1).Zeros(30)                                   // section header, one entry
            .U32(0).Field("plate", 8).Zeros(20)                 // named entry
            .Bytes(new byte[32]);                               // one record of command data
        byte[] pack = Build(textures, scene.ToArray());

        PicturePack parsed = PicturePack.Parse(pack, "test.scn");

        Assert.NotNull(parsed.Scene);
        Assert.Equal(96, parsed.Scene!.Raw.Length);
        Assert.Equal("plate", Assert.Single(parsed.Scene.Entries).Name);
    }

    [Fact]
    public void ATruncatedTexturePayloadIsClampedAndReported()
    {
        byte[] pack = Build([Texture("cut", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0)]);
        byte[] truncated = pack[..^8];

        PicturePack parsed = PicturePack.Parse(truncated, "test.pcp");

        Assert.Contains(parsed.Warnings, w => w.Contains("only 0x18 remain"));
        Assert.Equal(24, parsed.Textures[0].Data.Length);
    }

    [Fact]
    public void SniffingFindsAPackWhateverItIsCalled()
    {
        byte[] pack = Build([Texture("tex", GxTextureFormat.Cmpr, 8, 8, maxLod: 0)]);

        Assert.Equal(ContentKind.PicturePack, ContentSniffer.Identify("renamed.bin", pack, out bool compressed));
        Assert.False(compressed);
    }

    [Fact]
    public void SomethingThatIsNotAPackIsNotMistakenForOne()
    {
        Assert.False(PicturePack.LooksLikePicturePack(new byte[128]));
        Assert.False(PicturePack.LooksLikePicturePack([1, 2, 3]));
    }

    private static (int Width, int Height) Size(Rgba32Image image) => (image.Width, image.Height);

    /// <summary>Builds one texture header plus a payload of the right length for its mip chain.</summary>
    internal static byte[] Texture(string name, GxTextureFormat format, int width, int height,
                                   int maxLod, int? levels = null)
    {
        long size = GxTextureFormats.MipChainSize(format, width, height, levels ?? maxLod + 1);
        var payload = new byte[size];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 7);

        return new BigEndianWriter()
            .Field(name, 16)
            .U32((int)format)
            .U32(size)
            .U16(width).U16(height)
            .U32(0).U32(0)                              // wrapS, wrapT
            .U32(maxLod > 0 ? 5 : 1).U32(1).U32(0)      // minFilter, magFilter, lodBias
            .U8(0).U8(0).U8(maxLod).U8(0)
            .Zeros(12)
            .Bytes(payload)
            .ToArray();
    }

    /// <summary>Wraps textures in a container header, optionally followed by a scene table.</summary>
    internal static byte[] Build(IEnumerable<byte[]> textures, byte[]? scene = null)
    {
        var list = textures.ToList();
        int tableSize = PicturePack.HeaderSize + list.Sum(t => t.Length);

        var writer = new BigEndianWriter().U32(tableSize).U32(list.Count).Zeros(0x18);
        foreach (byte[] texture in list)
            writer.Bytes(texture);
        if (scene is not null)
            writer.Bytes(scene);

        return writer.ToArray();
    }
}

public class SceneTableTests
{
    [Fact]
    public void RecordsAreClassifiedByShapeRatherThanByName()
    {
        var table = new BigEndianWriter()
            .U16(2).Zeros(30)                                       // section header
            .U32(0).Field("shapeA", 8).Field("M0", 4).Zeros(4)
                .Bytes([0x80, 0x80, 0x80, 0xFF]).Zeros(8)           // entry with a colour at 0x14
            .Bytes(Enumerable.Repeat((byte)0x99, 64).ToArray())     // two records of command data
            .U32(0).Field("shapeB", 8).Zeros(20);

        SceneTable scene = SceneTable.Parse(table.ToArray(), 0x100);

        Assert.Equal(0x100, scene.Offset);
        List<SceneRecord> records = scene.Records.ToList();
        Assert.Equal(SceneRecordKind.SectionHeader, records[0].Kind);
        Assert.Equal(2, records[0].Count);

        Assert.Equal(SceneRecordKind.Entry, records[1].Kind);
        Assert.Equal("shapeA", records[1].Name);
        Assert.Equal("M0", records[1].Tag);
        Assert.Equal([0x80, 0x80, 0x80, 0xFF], records[1].Field14);
        Assert.Equal("shapeA.M0", records[1].FileStem);

        Assert.Equal(SceneRecordKind.Payload, records[2].Kind);
        Assert.Equal(64, records[2].Raw.Length);
        Assert.Same(records[1], records[2].Owner);
        Assert.Single(records[1].Payloads);

        Assert.Equal("shapeB", records[3].Name);
    }

    [Fact]
    public void ShortRunsOfPrintableBytesInsideCommandDataAreNotReadAsNames()
    {
        // 41 70 00 00 is the float 15.0, which the two-character name "Ap" would otherwise match.
        var table = new BigEndianWriter()
            .U32(0).Ascii("Ap").Zeros(26)
            .U32(0).Field("realname", 8).Zeros(20);

        SceneTable scene = SceneTable.Parse(table.ToArray(), 0);

        Assert.Equal("realname", Assert.Single(scene.Entries).Name);
    }
}
