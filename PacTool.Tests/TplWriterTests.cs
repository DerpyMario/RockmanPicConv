using System.Buffers.Binary;
using PacTool.Export;
using PacTool.Formats;
using PacTool.Gx;
using PacTool.Imaging;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The TPL writer. A TPL is read back here through a reader written against the layout rather than
/// against the writer, so a mistake in one does not cancel out a mistake in the other.
/// </summary>
public class TplWriterTests
{
    [Fact]
    public void ATextureBankReadsBackWithItsHeadersIntact()
    {
        byte[] data = Payload(GxTextureFormat.Rgb565, 8, 8, 1, 0x40);
        byte[] bytes = TplWriter.Build([
            new TplTexture
            {
                Name = "first", Format = GxTextureFormat.Rgb565, Width = 8, Height = 8,
                MipLevels = 1, Data = data, WrapS = 1, WrapT = 2, MinFilter = 5, MagFilter = 1,
            },
        ]);

        Tpl tpl = Tpl.Parse(bytes);

        Assert.Equal(TplWriter.Version, tpl.VersionWord);
        TplEntry entry = Assert.Single(tpl.Entries);
        Assert.Equal(8, entry.Width);
        Assert.Equal(8, entry.Height);
        Assert.Equal((uint)GxTextureFormat.Rgb565, entry.Format);
        Assert.Equal(1u, entry.WrapS);
        Assert.Equal(2u, entry.WrapT);
        Assert.Equal(5u, entry.MinFilter);
        Assert.Equal(0, entry.MinLod);
        Assert.Equal(0, entry.MaxLod);
        Assert.Equal(data, entry.Image);
    }

    [Fact]
    public void HeightComesBeforeWidth()
    {
        // The one field order in this format that is the other way round from everything else here.
        byte[] bytes = TplWriter.Build([Texture(GxTextureFormat.Rgb565, 32, 8)]);
        TplEntry entry = Assert.Single(Tpl.Parse(bytes).Entries);

        Assert.Equal(32, entry.Width);
        Assert.Equal(8, entry.Height);
    }

    [Fact]
    public void SeveralTexturesEachGetTheirOwnHeaderAndData()
    {
        byte[] bytes = TplWriter.Build([
            Texture(GxTextureFormat.Cmpr, 8, 8, fill: 0x11),
            Texture(GxTextureFormat.Rgb5A3, 4, 4, fill: 0x22),
            Texture(GxTextureFormat.I8, 8, 4, fill: 0x33),
        ]);

        Tpl tpl = Tpl.Parse(bytes);

        Assert.Equal(3, tpl.Entries.Count);
        Assert.Equal([(uint)GxTextureFormat.Cmpr, (uint)GxTextureFormat.Rgb5A3, (uint)GxTextureFormat.I8],
                     tpl.Entries.Select(e => e.Format));
        Assert.All(tpl.Entries, e => Assert.Equal(0, e.ImageOffset % TplWriter.DataAlignment));
        Assert.All(tpl.Entries.Select((e, i) => (e, i)),
                   pair => Assert.All(pair.e.Image, b => Assert.Equal(0x11 * (pair.i + 1), b)));
    }

    [Fact]
    public void AMipChainIsAdvertisedThroughTheMaximumLodOnly()
    {
        // 32x32 CMPR with four levels: the header carries the top level, and maxLod says how many
        // more follow it.
        byte[] bytes = TplWriter.Build([Texture(GxTextureFormat.Cmpr, 32, 32, levels: 4)]);
        TplEntry entry = Assert.Single(Tpl.Parse(bytes).Entries);

        Assert.Equal(0, entry.MinLod);
        Assert.Equal(3, entry.MaxLod);
        Assert.Equal(GxTextureFormats.MipChainSize(GxTextureFormat.Cmpr, 32, 32, 4), entry.Image.Length);
    }

    [Fact]
    public void AChainCutShortByTheSourceIsAdvertisedAtWhatItActuallyHolds()
    {
        // A header that claims four levels but only carries two would otherwise send a reader off
        // the end of the data.
        byte[] truncated = new byte[GxTextureFormats.MipChainSize(GxTextureFormat.Cmpr, 32, 32, 2)];
        byte[] bytes = TplWriter.Build([
            new TplTexture
            {
                Name = "short", Format = GxTextureFormat.Cmpr, Width = 32, Height = 32,
                MipLevels = 4, Data = truncated,
            },
        ]);

        TplEntry entry = Assert.Single(Tpl.Parse(bytes).Entries);

        Assert.Equal(1, entry.MaxLod);
        Assert.Equal(truncated.Length, entry.Image.Length);
    }

    [Fact]
    public void APalettedTextureCarriesItsTableAndItsEntriesUnchanged()
    {
        byte[] raw = [0xFC, 0x00, 0x80, 0x1F];              // opaque red, opaque blue in RGB5A3
        var palette = GxPalette.Decode(GxTlutFormat.Rgb5A3, raw, 2);
        byte[] bytes = TplWriter.Build([
            new TplTexture
            {
                Name = "indexed", Format = GxTextureFormat.C4, Width = 8, Height = 8,
                MipLevels = 1, Data = Payload(GxTextureFormat.C4, 8, 8, 1, 0x01), Palette = palette,
            },
        ]);

        TplEntry entry = Assert.Single(Tpl.Parse(bytes).Entries);

        Assert.NotEqual(0, entry.PaletteHeaderOffset);
        Assert.Equal(2, entry.PaletteCount);
        Assert.Equal((uint)GxTlutFormat.Rgb5A3, entry.PaletteFormat);
        Assert.Equal(raw, entry.PaletteData);
        Assert.Equal(0, entry.PaletteDataOffset % TplWriter.DataAlignment);
    }

    [Fact]
    public void AnUnpalettedTextureHasNoPaletteHeaderAtAll()
    {
        TplEntry entry = Assert.Single(Tpl.Parse(TplWriter.Build([Texture(GxTextureFormat.Cmpr, 8, 8)])).Entries);

        Assert.Equal(0, entry.PaletteHeaderOffset);
    }

    [Fact]
    public void APalettedTextureWithoutATableIsRefusedRatherThanWrittenBroken()
    {
        var texture = new TplTexture
        {
            Name = "indexed", Format = GxTextureFormat.C8, Width = 8, Height = 8,
            MipLevels = 1, Data = Payload(GxTextureFormat.C8, 8, 8, 1, 0),
        };

        PacFormatException error = Assert.Throws<PacFormatException>(() => TplWriter.Build([texture]));
        Assert.Contains("carries no palette", error.Message);
        Assert.Throws<PacFormatException>(() => TplWriter.Build([]));
    }

    [Fact]
    public void APicturePackTextureCrossesOverWithItsBytesUntouched()
    {
        var source = new PicturePackTexture
        {
            Index = 0, Name = "grdxxx", Format = GxTextureFormat.Cmpr, Width = 32, Height = 32,
            WrapS = 1, WrapT = 0, MinFilter = 5, MagFilter = 1, MinLod = 0, MaxLod = 5,
            MipLevels = 6, HeaderOffset = 0,
            Data = Payload(GxTextureFormat.Cmpr, 32, 32, 6, 0xAB),
        };

        TplEntry entry = Assert.Single(Tpl.Parse(TplWriter.Build([TplTexture.From(source)])).Entries);

        Assert.Equal(source.Data, entry.Image);
        Assert.Equal(5, entry.MaxLod);
        Assert.Equal(1u, entry.WrapS);
        Assert.Equal(5u, entry.MinFilter);
    }

    [Fact]
    public void AnImageThatWasNeverAGxTextureIsEncodedAsRgba8()
    {
        // Source art has no GX format of its own, so it becomes the one format that keeps every
        // colour and every alpha value.
        var image = new Rgba32Image(4, 4);
        image.SetPixel(0, 0, 10, 20, 30, 40);
        image.SetPixel(3, 3, 200, 210, 220, 230);

        TplEntry entry = Assert.Single(Tpl.Parse(TplWriter.Build([TplTexture.From(image, "art")])).Entries);

        Assert.Equal((uint)GxTextureFormat.Rgba8, entry.Format);
        Rgba32Image decoded = GxImageDecoder.Decode(GxTextureFormat.Rgba8, entry.Image, 4, 4);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(7, 3)]
    [InlineData(16, 9)]
    public void TheRgba8EncoderRoundTripsThroughTheDecoder(int width, int height)
    {
        var image = new Rgba32Image(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, (byte)(x * 17), (byte)(y * 23), (byte)(x + y), (byte)(255 - x));
        }

        byte[] encoded = GxImageEncoder.EncodeRgba8(image);

        Assert.Equal(GxTextureFormats.LevelSize(GxTextureFormat.Rgba8, width, height), encoded.Length);
        Assert.Equal(image.Pixels, GxImageDecoder.Decode(GxTextureFormat.Rgba8, encoded, width, height).Pixels);
    }

    private static TplTexture Texture(GxTextureFormat format, int width, int height,
                                      int levels = 1, byte fill = 0) => new()
    {
        Name = format.ToString(),
        Format = format,
        Width = width,
        Height = height,
        MipLevels = levels,
        Data = Payload(format, width, height, levels, fill),
    };

    private static byte[] Payload(GxTextureFormat format, int width, int height, int levels, byte fill)
    {
        var data = new byte[GxTextureFormats.MipChainSize(format, width, height, levels)];
        Array.Fill(data, fill);
        return data;
    }
}

/// <summary>One texture as read back out of a TPL.</summary>
internal sealed record TplEntry(
    int Width, int Height, uint Format, int ImageOffset, byte[] Image,
    uint WrapS, uint WrapT, uint MinFilter, uint MagFilter, float LodBias,
    int MinLod, int MaxLod,
    int PaletteHeaderOffset, int PaletteCount, uint PaletteFormat, int PaletteDataOffset, byte[] PaletteData);

/// <summary>
/// A reader for the TPL container, written from the layout rather than from the writer so that the
/// two are genuinely independent. Deliberately strict: anything out of range throws.
/// </summary>
internal sealed class Tpl
{
    public required uint VersionWord { get; init; }

    public required IReadOnlyList<TplEntry> Entries { get; init; }

    public static Tpl Parse(byte[] b)
    {
        uint version = U32(b, 0);
        int count = (int)U32(b, 4);
        int table = (int)U32(b, 8);
        Assert.InRange(count, 1, 4096);
        Assert.InRange(table + count * 8, 0, b.Length);

        var entries = new List<TplEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int imageHeader = (int)U32(b, table + i * 8);
            int paletteHeader = (int)U32(b, table + i * 8 + 4);
            Assert.InRange(imageHeader + TplWriter.ImageHeaderSize, 0, b.Length);

            int height = U16(b, imageHeader);
            int width = U16(b, imageHeader + 2);
            uint format = U32(b, imageHeader + 4);
            int imageOffset = (int)U32(b, imageHeader + 8);
            int maxLod = b[imageHeader + 0x22];

            long size = GxTextureFormats.MipChainSize((GxTextureFormat)format, width, height, maxLod + 1);
            Assert.InRange(imageOffset + size, 0, b.Length);

            int paletteCount = 0, paletteData = 0;
            uint paletteFormat = 0;
            byte[] palette = [];
            if (paletteHeader != 0)
            {
                Assert.InRange(paletteHeader + TplWriter.PaletteHeaderSize, 0, b.Length);
                paletteCount = U16(b, paletteHeader);
                paletteFormat = U32(b, paletteHeader + 4);
                paletteData = (int)U32(b, paletteHeader + 8);
                Assert.InRange(paletteData + paletteCount * 2, 0, b.Length);
                palette = b[paletteData..(paletteData + paletteCount * 2)];
            }

            entries.Add(new TplEntry(
                width, height, format, imageOffset, b[imageOffset..(imageOffset + (int)size)],
                U32(b, imageHeader + 0x0C), U32(b, imageHeader + 0x10),
                U32(b, imageHeader + 0x14), U32(b, imageHeader + 0x18),
                BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(imageHeader + 0x1C)),
                b[imageHeader + 0x21], maxLod,
                paletteHeader, paletteCount, paletteFormat, paletteData, palette));
        }

        return new Tpl { VersionWord = version, Entries = entries };
    }

    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
}
