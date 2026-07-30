using System.Buffers.Binary;
using PacTool.Imaging;

namespace PacTool.Gx;

/// <summary>
/// Decodes GX texture data into <see cref="Rgba32Image"/>.
///
/// Every format stores whole tiles in row-major order, and the pixels inside a tile are also
/// row-major, so the decoders share one walk and differ only in how they turn a tile's bytes into
/// pixels. Images whose dimensions are not a multiple of the tile size are stored padded; the
/// padding is decoded and then discarded by <see cref="Rgba32Image.SetPixel"/>.
/// </summary>
public static class GxImageDecoder
{
    /// <summary>
    /// Decodes one mip level.
    /// </summary>
    /// <param name="format">Texture format; see <see cref="GxTextureFormat"/>.</param>
    /// <param name="data">The level's bytes. Shorter data decodes as far as it goes rather than throwing.</param>
    /// <param name="width">Level width in pixels.</param>
    /// <param name="height">Level height in pixels.</param>
    /// <param name="palette">Colours for a paletted format, or null.</param>
    public static Rgba32Image Decode(GxTextureFormat format, ReadOnlySpan<byte> data,
                                     int width, int height, GxPalette? palette = null)
    {
        if (!GxTextureFormats.IsKnown(format))
            throw new PacFormatException($"GX texture format 0x{(int)format:X} is not one this tool can decode.");

        var image = new Rgba32Image(width, height);
        int tileWidth = GxTextureFormats.TileWidth(format);
        int tileHeight = GxTextureFormats.TileHeight(format);
        int tileSize = GxTextureFormats.TileSize(format);
        int offset = 0;

        for (int tileY = 0; tileY < height; tileY += tileHeight)
        {
            for (int tileX = 0; tileX < width; tileX += tileWidth)
            {
                if (offset + tileSize > data.Length)
                    return image;

                DecodeTile(format, data.Slice(offset, tileSize), image, tileX, tileY, palette);
                offset += tileSize;
            }
        }

        return image;
    }

    private static void DecodeTile(GxTextureFormat format, ReadOnlySpan<byte> tile, Rgba32Image image,
                                   int originX, int originY, GxPalette? palette)
    {
        switch (format)
        {
            case GxTextureFormat.I4:
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        byte pair = tile[(y * 8 + x) / 2];
                        byte i = Expand4((x & 1) == 0 ? (byte)(pair >> 4) : (byte)(pair & 0x0F));
                        image.SetPixel(originX + x, originY + y, i, i, i, 0xFF);
                    }
                }

                break;

            case GxTextureFormat.I8:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        byte i = tile[y * 8 + x];
                        image.SetPixel(originX + x, originY + y, i, i, i, 0xFF);
                    }
                }

                break;

            case GxTextureFormat.Ia4:
                // AAAAIIII: alpha is the high nibble here, but IA8 below puts intensity first.
                // The two formats really are inconsistent with each other on this hardware.
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        byte packed = tile[y * 8 + x];
                        byte a = Expand4((byte)(packed >> 4));
                        byte i = Expand4((byte)(packed & 0x0F));
                        image.SetPixel(originX + x, originY + y, i, i, i, a);
                    }
                }

                break;

            case GxTextureFormat.Ia8:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int at = (y * 4 + x) * 2;
                        byte i = tile[at];
                        byte a = tile[at + 1];
                        image.SetPixel(originX + x, originY + y, i, i, i, a);
                    }
                }

                break;

            case GxTextureFormat.Rgb565:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ushort value = BinaryPrimitives.ReadUInt16BigEndian(tile[((y * 4 + x) * 2)..]);
                        (byte r, byte g, byte b) = FromRgb565(value);
                        image.SetPixel(originX + x, originY + y, r, g, b, 0xFF);
                    }
                }

                break;

            case GxTextureFormat.Rgb5A3:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ushort value = BinaryPrimitives.ReadUInt16BigEndian(tile[((y * 4 + x) * 2)..]);
                        (byte r, byte g, byte b, byte a) = FromRgb5A3(value);
                        image.SetPixel(originX + x, originY + y, r, g, b, a);
                    }
                }

                break;

            case GxTextureFormat.Rgba8:
                // A 4x4 tile is two 32-byte halves: sixteen AR pairs, then sixteen GB pairs.
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int at = (y * 4 + x) * 2;
                        image.SetPixel(originX + x, originY + y,
                                       r: tile[at + 1], g: tile[32 + at], b: tile[32 + at + 1], a: tile[at]);
                    }
                }

                break;

            case GxTextureFormat.C4:
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        byte pair = tile[(y * 8 + x) / 2];
                        int index = (x & 1) == 0 ? pair >> 4 : pair & 0x0F;
                        SetFromPalette(image, originX + x, originY + y, index, palette);
                    }
                }

                break;

            case GxTextureFormat.C8:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 8; x++)
                        SetFromPalette(image, originX + x, originY + y, tile[y * 8 + x], palette);
                }

                break;

            case GxTextureFormat.C14X2:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ushort value = BinaryPrimitives.ReadUInt16BigEndian(tile[((y * 4 + x) * 2)..]);
                        SetFromPalette(image, originX + x, originY + y, value & 0x3FFF, palette);
                    }
                }

                break;

            case GxTextureFormat.Cmpr:
                // An 8x8 tile is four DXT1 sub-blocks, themselves in row-major 2x2 order.
                for (int subY = 0; subY < 8; subY += 4)
                {
                    for (int subX = 0; subX < 8; subX += 4)
                    {
                        int at = (subY * 2 + subX) * 2;
                        DecodeDxt1Block(tile.Slice(at, 8), image, originX + subX, originY + subY);
                    }
                }

                break;

            default:
                throw new PacFormatException($"GX texture format 0x{(int)format:X} is not one this tool can decode.");
        }
    }

    /// <summary>
    /// Decodes one 4x4 DXT1 block. Two differences from the PC layout: the endpoint colours are
    /// big-endian, and the two-bit indices within each row byte run from the most significant pair
    /// (leftmost pixel) down.
    /// </summary>
    private static void DecodeDxt1Block(ReadOnlySpan<byte> block, Rgba32Image image, int originX, int originY)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16BigEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16BigEndian(block[2..]);

        Span<byte> red = stackalloc byte[4];
        Span<byte> green = stackalloc byte[4];
        Span<byte> blue = stackalloc byte[4];
        Span<byte> alpha = stackalloc byte[4];

        (red[0], green[0], blue[0]) = FromRgb565(c0);
        (red[1], green[1], blue[1]) = FromRgb565(c1);
        alpha[0] = alpha[1] = alpha[2] = alpha[3] = 0xFF;

        if (c0 > c1)
        {
            // Four opaque colours: two endpoints and two thirds along the line between them.
            for (int c = 0; c < 3; c++)
            {
                Span<byte> channel = c == 0 ? red : c == 1 ? green : blue;
                channel[2] = (byte)((channel[0] * 2 + channel[1] + 1) / 3);
                channel[3] = (byte)((channel[0] + channel[1] * 2 + 1) / 3);
            }
        }
        else
        {
            // Three colours plus a transparent index.
            for (int c = 0; c < 3; c++)
            {
                Span<byte> channel = c == 0 ? red : c == 1 ? green : blue;
                channel[2] = (byte)((channel[0] + channel[1]) / 2);
                channel[3] = 0;
            }

            alpha[3] = 0;
        }

        for (int y = 0; y < 4; y++)
        {
            byte indices = block[4 + y];
            for (int x = 0; x < 4; x++)
            {
                int index = (indices >> (6 - x * 2)) & 0x3;
                image.SetPixel(originX + x, originY + y, red[index], green[index], blue[index], alpha[index]);
            }
        }
    }

    private static void SetFromPalette(Rgba32Image image, int x, int y, int index, GxPalette? palette)
    {
        if (palette is null)
        {
            // No TLUT was supplied. Showing the raw indices as greyscale is more useful than
            // refusing outright: the shapes in the image stay legible, only the colours are wrong.
            byte level = (byte)(index & 0xFF);
            image.SetPixel(x, y, level, level, level, 0xFF);
            return;
        }

        (byte r, byte g, byte b, byte a) = palette[index];
        image.SetPixel(x, y, r, g, b, a);
    }

    /// <summary>Spreads a 4-bit value across the full 0-255 range (0 stays 0, 15 becomes 255).</summary>
    private static byte Expand4(byte value) => (byte)(value * 0x11);

    /// <summary>Unpacks an RGB565 word. Each channel is scaled so its maximum reaches 255.</summary>
    internal static (byte R, byte G, byte B) FromRgb565(ushort value) => (
        (byte)(((value >> 11) & 0x1F) * 255 / 31),
        (byte)(((value >> 5) & 0x3F) * 255 / 63),
        (byte)((value & 0x1F) * 255 / 31));

    /// <summary>
    /// Unpacks an RGB5A3 word. The top bit picks the encoding: set means opaque RGB555, clear means
    /// 4 bits per channel with 3 bits of alpha.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) FromRgb5A3(ushort value)
    {
        if ((value & 0x8000) != 0)
        {
            return ((byte)(((value >> 10) & 0x1F) * 255 / 31),
                    (byte)(((value >> 5) & 0x1F) * 255 / 31),
                    (byte)((value & 0x1F) * 255 / 31),
                    (byte)0xFF);
        }

        return ((byte)(((value >> 8) & 0x0F) * 0x11),
                (byte)(((value >> 4) & 0x0F) * 0x11),
                (byte)((value & 0x0F) * 0x11),
                (byte)(((value >> 12) & 0x07) * 255 / 7));
    }
}
