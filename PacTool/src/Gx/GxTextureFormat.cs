namespace PacTool.Gx;

/// <summary>
/// Texture formats understood by the GameCube's GX graphics processor. The numeric values are
/// the ones the hardware uses, and they are what every container in this repository stores:
/// the <c>format</c> word of a Picture Pack sub-section, the <c>format</c> byte of a BTI header
/// and the same byte inside a BMD/BDL <c>TEX1</c> section all use this enumeration.
/// </summary>
/// <remarks>
/// Note that this is <em>not</em> the enumeration in <c>sdk/include/rockman.h</c>. That header is
/// a reconstruction and numbers the formats sequentially from <c>IA4 = 0</c>; the shipped data
/// disagrees with it. <c>Card/card.pac</c> stores a 96x32 texture as format 5 in 6144 bytes, which
/// is 2 bytes per pixel and therefore <see cref="Rgb5A3"/> here, not <c>CI4</c> as that header
/// would have it. Every one of the 3048 sub-sections in the reference data has a payload length
/// that <see cref="MipChainSize"/> reproduces exactly under this numbering.
/// </remarks>
public enum GxTextureFormat
{
    /// <summary>4-bit intensity, greyscale, no alpha.</summary>
    I4 = 0x0,

    /// <summary>8-bit intensity, greyscale, no alpha.</summary>
    I8 = 0x1,

    /// <summary>4-bit alpha in the high nibble, 4-bit intensity in the low nibble.</summary>
    Ia4 = 0x2,

    /// <summary>8-bit intensity then 8-bit alpha, in that byte order.</summary>
    Ia8 = 0x3,

    /// <summary>16-bit colour, 5 red / 6 green / 5 blue, opaque.</summary>
    Rgb565 = 0x4,

    /// <summary>16-bit colour; the top bit selects opaque RGB555 or RGB444 with 3-bit alpha.</summary>
    Rgb5A3 = 0x5,

    /// <summary>32-bit colour stored as an AR half-block followed by a GB half-block.</summary>
    Rgba8 = 0x6,

    /// <summary>4-bit palette index into a TLUT.</summary>
    C4 = 0x8,

    /// <summary>8-bit palette index into a TLUT.</summary>
    C8 = 0x9,

    /// <summary>14-bit palette index, stored in a 16-bit word with the top two bits ignored.</summary>
    C14X2 = 0xA,

    /// <summary>DXT1-derived block compression, 4 bits per pixel. Called S3TC on other hardware.</summary>
    Cmpr = 0xE,
}

/// <summary>Colour formats a texture look-up table (palette) can use.</summary>
public enum GxTlutFormat
{
    /// <summary>8-bit intensity then 8-bit alpha.</summary>
    Ia8 = 0,

    /// <summary>16-bit colour, 5 red / 6 green / 5 blue, opaque.</summary>
    Rgb565 = 1,

    /// <summary>16-bit colour; the top bit selects opaque RGB555 or RGB444 with 3-bit alpha.</summary>
    Rgb5A3 = 2,
}

/// <summary>Static geometry of the GX texture formats: tile size, bit depth and mip chain length.</summary>
public static class GxTextureFormats
{
    /// <summary>True if <paramref name="format"/> is one this build can decode.</summary>
    public static bool IsKnown(GxTextureFormat format) => format switch
    {
        GxTextureFormat.I4 or GxTextureFormat.I8 or GxTextureFormat.Ia4 or GxTextureFormat.Ia8 or
        GxTextureFormat.Rgb565 or GxTextureFormat.Rgb5A3 or GxTextureFormat.Rgba8 or
        GxTextureFormat.C4 or GxTextureFormat.C8 or GxTextureFormat.C14X2 or
        GxTextureFormat.Cmpr => true,
        _ => false,
    };

    /// <summary>True if <paramref name="format"/> reads its colours from a TLUT.</summary>
    public static bool IsPaletted(GxTextureFormat format) =>
        format is GxTextureFormat.C4 or GxTextureFormat.C8 or GxTextureFormat.C14X2;

    /// <summary>
    /// Width in pixels of the tile the hardware stores as one contiguous run of bytes. An image is
    /// laid out as whole tiles in row-major order, so its dimensions are rounded up to a multiple
    /// of this before any size is computed.
    /// </summary>
    public static int TileWidth(GxTextureFormat format) => format switch
    {
        GxTextureFormat.I4 or GxTextureFormat.C4 or GxTextureFormat.Cmpr => 8,
        GxTextureFormat.I8 or GxTextureFormat.Ia4 or GxTextureFormat.C8 => 8,
        _ => 4,
    };

    /// <summary>Height in pixels of one stored tile. See <see cref="TileWidth"/>.</summary>
    public static int TileHeight(GxTextureFormat format) => format switch
    {
        GxTextureFormat.I4 or GxTextureFormat.C4 or GxTextureFormat.Cmpr => 8,
        _ => 4,
    };

    /// <summary>Bits each pixel occupies once stored.</summary>
    public static int BitsPerPixel(GxTextureFormat format) => format switch
    {
        GxTextureFormat.I4 or GxTextureFormat.C4 or GxTextureFormat.Cmpr => 4,
        GxTextureFormat.I8 or GxTextureFormat.Ia4 or GxTextureFormat.C8 => 8,
        GxTextureFormat.Rgba8 => 32,
        _ => 16,
    };

    /// <summary>Bytes one tile occupies. Always 32 apart from <see cref="GxTextureFormat.Rgba8"/>, which uses 64.</summary>
    public static int TileSize(GxTextureFormat format) =>
        TileWidth(format) * TileHeight(format) * BitsPerPixel(format) / 8;

    /// <summary>Number of entries a full TLUT holds for a paletted format.</summary>
    public static int MaxPaletteEntries(GxTextureFormat format) => format switch
    {
        GxTextureFormat.C4 => 16,
        GxTextureFormat.C8 => 256,
        GxTextureFormat.C14X2 => 16384,
        _ => 0,
    };

    /// <summary>Bytes one mip level of the given size occupies, including the padding up to whole tiles.</summary>
    public static long LevelSize(GxTextureFormat format, int width, int height)
    {
        int tw = TileWidth(format);
        int th = TileHeight(format);
        long paddedWidth = (Math.Max(1, width) + tw - 1) / tw * tw;
        long paddedHeight = (Math.Max(1, height) + th - 1) / th * th;
        return paddedWidth * paddedHeight * BitsPerPixel(format) / 8;
    }

    /// <summary>
    /// Bytes a chain of <paramref name="levels"/> mip levels occupies. Level <c>i</c> measures
    /// <c>max(1, width >> i)</c> by <c>max(1, height >> i)</c>, and each level is padded up to
    /// whole tiles independently - which is why an 8x8 CMPR chain of four levels is 128 bytes
    /// rather than the 42.7 a naive quarter-each-time sum would predict.
    /// </summary>
    public static long MipChainSize(GxTextureFormat format, int width, int height, int levels)
    {
        long total = 0;
        for (int i = 0; i < Math.Max(1, levels); i++)
            total += LevelSize(format, Math.Max(1, width >> i), Math.Max(1, height >> i));
        return total;
    }

    /// <summary>Offset of mip level <paramref name="level"/> from the start of the chain.</summary>
    public static long LevelOffset(GxTextureFormat format, int width, int height, int level) =>
        level <= 0 ? 0 : MipChainSize(format, width, height, level);

    /// <summary>
    /// Number of mip levels that exactly fill <paramref name="available"/> bytes, or 0 when no
    /// level count does. Used to sanity-check a stored payload length against its header.
    /// </summary>
    public static int LevelsFilling(GxTextureFormat format, int width, int height, long available)
    {
        long total = 0;
        int maxLevels = 1;
        while ((width >> maxLevels) > 0 || (height >> maxLevels) > 0)
            maxLevels++;

        for (int level = 1; level <= maxLevels; level++)
        {
            total += LevelSize(format, Math.Max(1, width >> (level - 1)), Math.Max(1, height >> (level - 1)));
            if (total == available)
                return level;
            if (total > available)
                break;
        }

        return 0;
    }
}
