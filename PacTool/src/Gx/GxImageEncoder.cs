using PacTool.Imaging;

namespace PacTool.Gx;

/// <summary>
/// Encodes an RGBA image back into GX texture data.
///
/// Only <see cref="GxTextureFormat.Rgba8"/> is written. It is the one format that survives the
/// trip without a decision to make: every other GX format either quantises colour, drops alpha or
/// compresses, and a converter that silently degraded an image would be worse than one that
/// declines to. Everything this tool already holds as GX data - Picture Pack textures, BTI
/// textures - is written straight back out unchanged rather than through here; this exists for the
/// images that only ever existed as RGBA, which is the Softimage PIC source art.
/// </summary>
public static class GxImageEncoder
{
    /// <summary>True when <paramref name="format"/> is one this encoder can produce.</summary>
    public static bool CanEncode(GxTextureFormat format) => format == GxTextureFormat.Rgba8;

    /// <summary>
    /// Encodes one level as RGBA8. The image is padded up to whole 4x4 tiles with transparent
    /// black, which is what the hardware stores and what <see cref="GxImageDecoder"/> discards on
    /// the way back.
    /// </summary>
    public static byte[] EncodeRgba8(Rgba32Image image)
    {
        const int TileWidth = 4, TileHeight = 4, TileSize = 64;
        int tilesX = (image.Width + TileWidth - 1) / TileWidth;
        int tilesY = (image.Height + TileHeight - 1) / TileHeight;
        long size = (long)tilesX * tilesY * TileSize;
        if (size > int.MaxValue)
            throw new PacFormatException($"a {image.Width}x{image.Height} RGBA8 texture is too large to encode.");

        var data = new byte[size];
        int offset = 0;
        for (int tileY = 0; tileY < image.Height; tileY += TileHeight)
        {
            for (int tileX = 0; tileX < image.Width; tileX += TileWidth)
            {
                // A tile is two 32-byte halves: sixteen AR pairs, then sixteen GB pairs.
                for (int y = 0; y < TileHeight; y++)
                {
                    for (int x = 0; x < TileWidth; x++)
                    {
                        int at = offset + (y * TileWidth + x) * 2;
                        (byte r, byte g, byte b, byte a) = Pixel(image, tileX + x, tileY + y);
                        data[at] = a;
                        data[at + 1] = r;
                        data[offset + 32 + (y * TileWidth + x) * 2] = g;
                        data[offset + 32 + (y * TileWidth + x) * 2 + 1] = b;
                    }
                }

                offset += TileSize;
            }
        }

        return data;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(Rgba32Image image, int x, int y)
    {
        if (x >= image.Width || y >= image.Height)
            return (0, 0, 0, 0);

        int at = (y * image.Width + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }
}
