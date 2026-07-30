namespace PacTool.Imaging;

/// <summary>A decoded image: 8 bits per channel, four channels, one row after another, no padding.</summary>
public sealed class Rgba32Image
{
    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Pixels as R, G, B, A bytes, row-major from the top-left.</summary>
    public byte[] Pixels { get; }

    public Rgba32Image(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Image size {width}x{height} is not positive.");

        long bytes = (long)width * height * 4;
        if (bytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), $"Image size {width}x{height} is too large to decode.");

        Width = width;
        Height = height;
        Pixels = new byte[bytes];
    }

    /// <summary>Writes one pixel. Coordinates outside the image are ignored, which keeps tile
    /// decoders free to walk whole tiles that overhang the right or bottom edge.</summary>
    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;

        int i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    /// <summary>True when every pixel is fully opaque, which lets the encoder drop the alpha channel.</summary>
    public bool IsOpaque()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
        {
            if (Pixels[i] != 0xFF)
                return false;
        }

        return true;
    }
}
