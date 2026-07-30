using PacTool.Gx;
using PacTool.Imaging;
using Xunit;

namespace PacTool.Tests;

public class GxTextureFormatTests
{
    /// <summary>
    /// Sizes taken from the shipped data. Each pair is a texture header's stated dimensions and
    /// mip count against the payload length that followed it, so these are the numbers the format
    /// has to reproduce for the container to parse at all.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 4, 128)]              // LightMap.pcp 'plug1H'
    [InlineData(128, 128, 8, 11008)]        // LightMap.pcp 'plug0H'
    [InlineData(256, 256, 8, 43744)]        // EnvMap.pcp 'EnvMap0'
    [InlineData(256, 256, 9, 43776)]        // Stage0b.pac 'blka07'
    [InlineData(512, 512, 10, 174848)]      // Stage0b.pac 'b0bbgalax'
    [InlineData(32, 32, 6, 768)]            // Stage0b.pac 'grdxxx'
    [InlineData(32, 64, 6, 1440)]           // Stage0b.pac 'b00locxb'
    [InlineData(256, 128, 8, 21920)]        // Stage0b.pac 'b0bwpzon'
    public void CmprMipChainMatchesShippedPayloadLengths(int width, int height, int levels, long expected)
    {
        Assert.Equal(expected, GxTextureFormats.MipChainSize(GxTextureFormat.Cmpr, width, height, levels));
        Assert.Equal(levels, GxTextureFormats.LevelsFilling(GxTextureFormat.Cmpr, width, height, expected));
    }

    [Fact]
    public void Rgb5A3MipChainMatchesShippedPayloadLengths()
    {
        // Card/card.pac stores a 96x32 banner and eight 32x32 icons, all single-level RGB5A3.
        Assert.Equal(6144, GxTextureFormats.MipChainSize(GxTextureFormat.Rgb5A3, 96, 32, 1));
        Assert.Equal(2048, GxTextureFormats.MipChainSize(GxTextureFormat.Rgb5A3, 32, 32, 1));
    }

    [Fact]
    public void LevelsAreRoundedUpToWholeTilesIndependently()
    {
        // A 1x1 CMPR level still occupies a whole 8x8 tile, which is why the tail of a chain
        // costs 32 bytes a level rather than shrinking towards zero.
        Assert.Equal(32, GxTextureFormats.LevelSize(GxTextureFormat.Cmpr, 1, 1));
        Assert.Equal(32, GxTextureFormats.LevelSize(GxTextureFormat.Cmpr, 8, 8));
        Assert.Equal(128, GxTextureFormats.LevelSize(GxTextureFormat.Cmpr, 16, 16));
    }

    [Fact]
    public void LevelsFillingRejectsALengthNoChainProduces()
    {
        // 8160 bytes is what the reference Python extractor truncated a 128x128 CMPR texture to.
        Assert.Equal(0, GxTextureFormats.LevelsFilling(GxTextureFormat.Cmpr, 128, 128, 8160));
        Assert.Equal(1, GxTextureFormats.LevelsFilling(GxTextureFormat.Cmpr, 128, 128, 8192));
    }

    [Theory]
    [InlineData(GxTextureFormat.I4, 8, 8, 4, 32)]
    [InlineData(GxTextureFormat.I8, 8, 4, 8, 32)]
    [InlineData(GxTextureFormat.Ia4, 8, 4, 8, 32)]
    [InlineData(GxTextureFormat.Ia8, 4, 4, 16, 32)]
    [InlineData(GxTextureFormat.Rgb565, 4, 4, 16, 32)]
    [InlineData(GxTextureFormat.Rgb5A3, 4, 4, 16, 32)]
    [InlineData(GxTextureFormat.Rgba8, 4, 4, 32, 64)]
    [InlineData(GxTextureFormat.C4, 8, 8, 4, 32)]
    [InlineData(GxTextureFormat.C8, 8, 4, 8, 32)]
    [InlineData(GxTextureFormat.C14X2, 4, 4, 16, 32)]
    [InlineData(GxTextureFormat.Cmpr, 8, 8, 4, 32)]
    public void TileGeometryIsConsistent(GxTextureFormat format, int width, int height, int bits, int tileSize)
    {
        Assert.Equal(width, GxTextureFormats.TileWidth(format));
        Assert.Equal(height, GxTextureFormats.TileHeight(format));
        Assert.Equal(bits, GxTextureFormats.BitsPerPixel(format));
        Assert.Equal(tileSize, GxTextureFormats.TileSize(format));
    }
}

public class GxImageDecoderTests
{
    [Fact]
    public void I8DecodesOneTileRowMajor()
    {
        // One 8x4 tile of ascending intensities.
        var tile = new byte[32];
        for (int i = 0; i < tile.Length; i++)
            tile[i] = (byte)(i * 8);

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.I8, tile, 8, 4);

        Assert.Equal((0, 0, 0, 255), PixelAt(image, 0, 0));
        Assert.Equal((8, 8, 8, 255), PixelAt(image, 1, 0));
        Assert.Equal((64, 64, 64, 255), PixelAt(image, 0, 1));
        Assert.Equal((248, 248, 248, 255), PixelAt(image, 7, 3));
    }

    [Fact]
    public void I4TakesTheHighNibbleFirst()
    {
        var tile = new byte[32];
        tile[0] = 0xF0;

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.I4, tile, 8, 8);

        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 0));
        Assert.Equal((0, 0, 0, 255), PixelAt(image, 1, 0));
    }

    [Fact]
    public void Ia4PutsAlphaInTheHighNibbleButIa8PutsIntensityFirst()
    {
        // The two formats disagree with each other on this hardware, so both directions are pinned.
        var ia4 = new byte[32];
        ia4[0] = 0x8F;                      // alpha 0x88, intensity 0xFF
        Rgba32Image fromIa4 = GxImageDecoder.Decode(GxTextureFormat.Ia4, ia4, 8, 4);
        Assert.Equal((255, 255, 255, 0x88), PixelAt(fromIa4, 0, 0));

        var ia8 = new byte[32];
        ia8[0] = 0x20;                      // intensity 0x20
        ia8[1] = 0x40;                      // alpha 0x40
        Rgba32Image fromIa8 = GxImageDecoder.Decode(GxTextureFormat.Ia8, ia8, 4, 4);
        Assert.Equal((0x20, 0x20, 0x20, 0x40), PixelAt(fromIa8, 0, 0));
    }

    [Fact]
    public void Rgb565ExpandsEachChannelToItsFullRange()
    {
        var tile = new byte[32];
        tile[0] = 0xFF;
        tile[1] = 0xFF;                     // white
        tile[2] = 0xF8;
        tile[3] = 0x00;                     // pure red

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Rgb565, tile, 4, 4);

        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 0));
        Assert.Equal((255, 0, 0, 255), PixelAt(image, 1, 0));
    }

    [Fact]
    public void Rgb5A3SwitchesEncodingOnTheTopBit()
    {
        var tile = new byte[32];
        tile[0] = 0xFF;
        tile[1] = 0xFF;                     // top bit set: opaque RGB555 white
        tile[2] = 0x0F;
        tile[3] = 0x00;                     // top bit clear: alpha 0, red 0xFF, green 0, blue 0

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Rgb5A3, tile, 4, 4);

        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 0));
        Assert.Equal((255, 0, 0, 0), PixelAt(image, 1, 0));
    }

    [Fact]
    public void Rgba8ReadsTheArHalfBlockThenTheGbHalfBlock()
    {
        var tile = new byte[64];
        tile[0] = 0x11;                     // alpha
        tile[1] = 0x22;                     // red
        tile[32] = 0x33;                    // green
        tile[33] = 0x44;                    // blue

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Rgba8, tile, 4, 4);

        Assert.Equal((0x22, 0x33, 0x44, 0x11), PixelAt(image, 0, 0));
    }

    [Fact]
    public void CmprFourColourModeInterpolatesBetweenTheEndpoints()
    {
        // c0 > c1 selects the opaque four-colour mode.
        byte[] tile = CmprTile(0xFFFF, 0x0000, 0b00_01_10_11);

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Cmpr, tile, 4, 4);

        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 0));   // index 0: c0
        Assert.Equal((0, 0, 0, 255), PixelAt(image, 1, 0));         // index 1: c1
        Assert.Equal((170, 170, 170, 255), PixelAt(image, 2, 0));   // index 2: two thirds of c0
        Assert.Equal((85, 85, 85, 255), PixelAt(image, 3, 0));      // index 3: one third of c0
    }

    [Fact]
    public void CmprThreeColourModeMakesIndexThreeTransparent()
    {
        // c0 <= c1 selects the three-colour mode with a transparent fourth index.
        byte[] tile = CmprTile(0x0000, 0xFFFF, 0b00_01_10_11);

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Cmpr, tile, 4, 4);

        Assert.Equal((0, 0, 0, 255), PixelAt(image, 0, 0));
        Assert.Equal((255, 255, 255, 255), PixelAt(image, 1, 0));
        Assert.Equal((127, 127, 127, 255), PixelAt(image, 2, 0));
        Assert.Equal((0, 0, 0, 0), PixelAt(image, 3, 0));
    }

    [Fact]
    public void CmprSubBlocksAreLaidOutTwoByTwoWithinTheTile()
    {
        // Four distinguishable sub-blocks: flat white, flat black, flat white, flat black.
        var tile = new byte[32];
        for (int block = 0; block < 4; block++)
        {
            ushort colour = block % 2 == 0 ? (ushort)0xFFFF : (ushort)0x0000;
            tile[block * 8] = (byte)(colour >> 8);
            tile[block * 8 + 1] = (byte)colour;
            tile[block * 8 + 2] = (byte)(colour >> 8);
            tile[block * 8 + 3] = (byte)colour;
        }

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Cmpr, tile, 8, 8);

        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 0));   // sub-block 0, top left
        Assert.Equal((0, 0, 0, 255), PixelAt(image, 4, 0));         // sub-block 1, top right
        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 4));   // sub-block 2, bottom left
        Assert.Equal((0, 0, 0, 255), PixelAt(image, 4, 4));         // sub-block 3, bottom right
    }

    [Fact]
    public void PaddingBeyondTheImageEdgeIsDiscarded()
    {
        // A 5x3 CMPR image still stores one whole 8x8 tile.
        var tile = new byte[32];
        tile[0] = 0xFF;
        tile[1] = 0xFF;
        tile[2] = 0xFF;
        tile[3] = 0xFF;

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.Cmpr, tile, 5, 3);

        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(5 * 3 * 4, image.Pixels.Length);
    }

    [Fact]
    public void TruncatedDataDecodesAsFarAsItGoesRatherThanThrowing()
    {
        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.I8, new byte[8], 8, 8);

        Assert.Equal(8, image.Width);
        Assert.Equal((0, 0, 0, 0), PixelAt(image, 0, 0));
    }

    [Fact]
    public void PalettedFormatsReadTheirColoursFromTheTlut()
    {
        // A two-entry RGB5A3 TLUT: opaque red, then transparent.
        var tlut = new byte[] { 0xFC, 0x00, 0x00, 0x00 };
        GxPalette palette = GxPalette.Decode(GxTlutFormat.Rgb5A3, tlut, 2);

        var tile = new byte[32];
        tile[0] = 0x01;                     // index 0 then index 1, packed as nibbles

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.C4, tile, 8, 8, palette);

        Assert.Equal((255, 0, 0, 255), PixelAt(image, 0, 0));
        Assert.Equal((0, 0, 0, 0), PixelAt(image, 1, 0));
    }

    [Fact]
    public void APalettedFormatWithoutATlutFallsBackToGreyscaleIndices()
    {
        var tile = new byte[32];
        tile[0] = 0x80;

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.C8, tile, 8, 4);

        Assert.Equal((0x80, 0x80, 0x80, 255), PixelAt(image, 0, 0));
    }

    [Fact]
    public void C14X2IgnoresTheTopTwoBitsOfTheIndex()
    {
        var entries = new byte[8];
        entries[2] = 0xFF;
        entries[3] = 0xFF;                  // index 1 is opaque white
        GxPalette palette = GxPalette.Decode(GxTlutFormat.Rgb565, entries, 4);

        var tile = new byte[32];
        tile[0] = 0xC0;
        tile[1] = 0x01;                     // 0xC001: the top bits are ignored, index is 1

        Rgba32Image image = GxImageDecoder.Decode(GxTextureFormat.C14X2, tile, 4, 4, palette);

        Assert.Equal((255, 255, 255, 255), PixelAt(image, 0, 0));
    }

    [Fact]
    public void AnUnknownFormatIsRejected()
    {
        Assert.Throws<PacFormatException>(() =>
            GxImageDecoder.Decode((GxTextureFormat)0x7, new byte[32], 4, 4));
    }

    /// <summary>Builds a CMPR tile whose first sub-block uses the given endpoints and index row.</summary>
    private static byte[] CmprTile(ushort c0, ushort c1, byte firstRowIndices)
    {
        var tile = new byte[32];
        tile[0] = (byte)(c0 >> 8);
        tile[1] = (byte)c0;
        tile[2] = (byte)(c1 >> 8);
        tile[3] = (byte)c1;
        tile[4] = firstRowIndices;
        return tile;
    }

    internal static (byte R, byte G, byte B, byte A) PixelAt(Rgba32Image image, int x, int y)
    {
        int at = (y * image.Width + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }
}

public class GxPaletteTests
{
    [Fact]
    public void Ia8EntriesPutIntensityFirst()
    {
        GxPalette palette = GxPalette.Decode(GxTlutFormat.Ia8, [0x30, 0x80], 1);

        Assert.Equal((0x30, 0x30, 0x30, 0x80), palette[0]);
    }

    [Fact]
    public void IndicesPastTheEndOfTheTableAreTransparent()
    {
        GxPalette palette = GxPalette.Decode(GxTlutFormat.Rgb565, [0xFF, 0xFF], 1);

        Assert.Equal(1, palette.Count);
        Assert.Equal((0, 0, 0, 0), palette[1]);
        Assert.Equal((0, 0, 0, 0), palette[-1]);
    }

    [Fact]
    public void ATableShorterThanItsCountIsTruncatedRatherThanOverread()
    {
        GxPalette palette = GxPalette.Decode(GxTlutFormat.Rgb565, [0xFF, 0xFF], 256);

        Assert.Equal(1, palette.Count);
    }
}
