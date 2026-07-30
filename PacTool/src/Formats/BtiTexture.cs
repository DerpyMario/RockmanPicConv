using System.Buffers.Binary;
using PacTool.Gx;
using PacTool.Imaging;

namespace PacTool.Formats;

/// <summary>
/// A BTI texture header - Nintendo's standard GameCube texture descriptor. It appears both as a
/// standalone <c>.bti</c> file and, unchanged, as the per-texture header inside a BMD/BDL
/// <c>TEX1</c> section.
///
/// <code>
///   0x00  u8    format          GX texture format
///   0x01  u8    alphaSetting    0 opaque, 1 alpha-tested, 2 blended
///   0x02  u16   width
///   0x04  u16   height
///   0x06  u8    wrapS
///   0x07  u8    wrapT
///   0x08  u8    palettesEnabled
///   0x09  u8    paletteFormat   GxTlutFormat
///   0x0A  u16   paletteCount
///   0x0C  s32   paletteOffset   relative to the start of this header
///   0x10  s32   borderColor
///   0x14  u8    minFilter
///   0x15  u8    magFilter
///   0x16  s8    minLod
///   0x17  s8    maxLod
///   0x18  u8    mipCount
///   0x19  u8    reserved
///   0x1A  s16   lodBias         in 1/100 units
///   0x1C  s32   imageOffset     relative to the start of this header
/// </code>
///
/// The two offsets are relative to the header, which is what lets a <c>TEX1</c> section point
/// several headers at one shared image or palette.
/// </summary>
public sealed class BtiTexture
{
    /// <summary>Size of the header.</summary>
    public const int HeaderSize = 0x20;

    /// <summary>Name, from the containing section's string table. Empty for a standalone file.</summary>
    public string Name { get; set; } = "";

    /// <summary>GX texture format.</summary>
    public required GxTextureFormat Format { get; init; }

    /// <summary>How the game blends this texture: 0 opaque, 1 alpha-tested, 2 blended.</summary>
    public required byte AlphaSetting { get; init; }

    /// <summary>Width of the base level in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the base level in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Horizontal wrap mode: 0 clamp, 1 repeat, 2 mirror.</summary>
    public required byte WrapS { get; init; }

    /// <summary>Vertical wrap mode: 0 clamp, 1 repeat, 2 mirror.</summary>
    public required byte WrapT { get; init; }

    /// <summary>GX minification filter.</summary>
    public required byte MinFilter { get; init; }

    /// <summary>GX magnification filter.</summary>
    public required byte MagFilter { get; init; }

    /// <summary>Mip levels the header claims. Clamped to what the data can hold when decoding.</summary>
    public required int MipCount { get; init; }

    /// <summary>Level-of-detail bias, in hundredths.</summary>
    public required short LodBias { get; init; }

    /// <summary>Decoded TLUT, or null when the format is not paletted.</summary>
    public GxPalette? Palette { get; init; }

    /// <summary>The stored mip chain.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Offset the image data was read from, used to spot headers sharing one image.</summary>
    public required long ImageOffset { get; init; }

    /// <summary>
    /// Reads a header at <paramref name="offset"/> within <paramref name="data"/>. The palette and
    /// image offsets it holds are relative to <paramref name="offset"/> itself.
    /// </summary>
    public static BtiTexture Parse(ReadOnlySpan<byte> data, int offset, string sourceName)
    {
        if (offset < 0 || offset + HeaderSize > data.Length)
            throw new PacFormatException($"{sourceName}: a BTI header at 0x{offset:X} does not fit in {data.Length} bytes.");

        ReadOnlySpan<byte> header = data.Slice(offset, HeaderSize);
        var format = (GxTextureFormat)header[0];
        int width = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        int height = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        int paletteCount = BinaryPrimitives.ReadUInt16BigEndian(header[0x0A..]);
        int paletteOffset = BinaryPrimitives.ReadInt32BigEndian(header[0x0C..]);
        int mipCount = Math.Max(1, (int)header[0x18]);
        int imageOffset = BinaryPrimitives.ReadInt32BigEndian(header[0x1C..]);

        if (!GxTextureFormats.IsKnown(format))
            throw new PacFormatException($"{sourceName}: BTI at 0x{offset:X} uses GX format 0x{(int)format:X}, which this tool cannot decode.");
        if (width <= 0 || height <= 0)
            throw new PacFormatException($"{sourceName}: BTI at 0x{offset:X} is {width}x{height}.");

        long imageStart = offset + (long)imageOffset;
        if (imageStart < 0 || imageStart > data.Length)
            throw new PacFormatException($"{sourceName}: BTI '{width}x{height}' at 0x{offset:X} points its image data outside the file.");

        long wanted = GxTextureFormats.MipChainSize(format, width, height, mipCount);
        long available = data.Length - imageStart;
        if (wanted > available)
        {
            int fits = 1;
            while (fits < mipCount && GxTextureFormats.MipChainSize(format, width, height, fits + 1) <= available)
                fits++;
            mipCount = fits;
            wanted = Math.Min(GxTextureFormats.MipChainSize(format, width, height, mipCount), available);
        }

        GxPalette? palette = null;
        if (GxTextureFormats.IsPaletted(format) && header[8] != 0 && paletteCount > 0)
        {
            long paletteStart = offset + (long)paletteOffset;
            if (paletteStart >= 0 && paletteStart + paletteCount * 2 <= data.Length)
            {
                palette = GxPalette.Decode((GxTlutFormat)header[9],
                                           data.Slice((int)paletteStart, paletteCount * 2), paletteCount);
            }
        }

        return new BtiTexture
        {
            Format = format,
            AlphaSetting = header[1],
            Width = width,
            Height = height,
            WrapS = header[6],
            WrapT = header[7],
            MinFilter = header[0x14],
            MagFilter = header[0x15],
            MipCount = mipCount,
            LodBias = BinaryPrimitives.ReadInt16BigEndian(header[0x1A..]),
            Palette = palette,
            ImageOffset = imageStart,
            Data = data.Slice((int)imageStart, (int)wanted).ToArray(),
        };
    }

    /// <summary>True if <paramref name="data"/> plausibly is a standalone BTI file.</summary>
    public static bool LooksLikeBti(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + 32)
            return false;

        var format = (GxTextureFormat)data[0];
        int width = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int imageOffset = BinaryPrimitives.ReadInt32BigEndian(data[0x1C..]);

        return GxTextureFormats.IsKnown(format) &&
               width is > 0 and <= 4096 && height is > 0 and <= 4096 &&
               data[1] <= 2 && data[6] <= 2 && data[7] <= 2 &&
               imageOffset >= HeaderSize && imageOffset < data.Length &&
               GxTextureFormats.LevelSize(format, width, height) <= data.Length - imageOffset;
    }

    /// <summary>Width of mip level <paramref name="level"/>.</summary>
    public int LevelWidth(int level) => Math.Max(1, Width >> level);

    /// <summary>Height of mip level <paramref name="level"/>.</summary>
    public int LevelHeight(int level) => Math.Max(1, Height >> level);

    /// <summary>Decodes one mip level to RGBA.</summary>
    public Rgba32Image Decode(int level = 0)
    {
        if ((uint)level >= (uint)MipCount)
            throw new ArgumentOutOfRangeException(nameof(level), $"This texture has {MipCount} mip level(s).");

        long start = GxTextureFormats.LevelOffset(Format, Width, Height, level);
        long size = Math.Min(GxTextureFormats.LevelSize(Format, LevelWidth(level), LevelHeight(level)),
                             Data.Length - start);
        if (start >= Data.Length || size <= 0)
            throw new PacFormatException($"mip level {level} starts past the end of the stored data.");

        return GxImageDecoder.Decode(Format, Data.AsSpan((int)start, (int)size),
                                     LevelWidth(level), LevelHeight(level), Palette);
    }

    /// <summary>One-line summary for listings.</summary>
    public string Describe()
    {
        string mips = MipCount > 1 ? $" {MipCount} mips" : "";
        string palette = Palette is null ? "" : $" {Palette.Count}-entry {Palette.Format} TLUT";
        return $"{PicturePack.DescribeFormat(Format),-6} {Width,4}x{Height,-4}{mips}{palette}  {Data.Length,9:N0} B";
    }
}
