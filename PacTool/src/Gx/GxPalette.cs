using System.Buffers.Binary;

namespace PacTool.Gx;

/// <summary>
/// A decoded texture look-up table. Entries are two bytes each on disk, in one of the three
/// <see cref="GxTlutFormat"/> encodings; this class holds them expanded to RGBA.
/// </summary>
public sealed class GxPalette
{
    private readonly byte[] _colors;

    /// <summary>Format the entries were stored in.</summary>
    public GxTlutFormat Format { get; }

    /// <summary>Number of entries.</summary>
    public int Count => _colors.Length / 4;

    /// <summary>
    /// The entries exactly as they were stored, two bytes each. Kept so a palette can be written
    /// back out - to a TPL, say - without a lossy trip through the expanded colours.
    /// </summary>
    public byte[] Raw { get; }

    private GxPalette(GxTlutFormat format, byte[] colors, byte[] raw)
    {
        Format = format;
        _colors = colors;
        Raw = raw;
    }

    /// <summary>Reads a TLUT. Indices past the end of the table decode as transparent black.</summary>
    public static GxPalette Decode(GxTlutFormat format, ReadOnlySpan<byte> data, int count)
    {
        count = Math.Max(0, Math.Min(count, data.Length / 2));
        var colors = new byte[count * 4];
        byte[] raw = data[..(count * 2)].ToArray();

        for (int i = 0; i < count; i++)
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
            byte r, g, b, a;

            switch (format)
            {
                case GxTlutFormat.Ia8:
                    r = g = b = (byte)(value >> 8);
                    a = (byte)(value & 0xFF);
                    break;
                case GxTlutFormat.Rgb565:
                    (r, g, b) = GxImageDecoder.FromRgb565(value);
                    a = 0xFF;
                    break;
                case GxTlutFormat.Rgb5A3:
                    (r, g, b, a) = GxImageDecoder.FromRgb5A3(value);
                    break;
                default:
                    throw new PacFormatException($"TLUT format {(int)format} is not one of IA8, RGB565 or RGB5A3.");
            }

            colors[i * 4] = r;
            colors[i * 4 + 1] = g;
            colors[i * 4 + 2] = b;
            colors[i * 4 + 3] = a;
        }

        return new GxPalette(format, colors, raw);
    }

    /// <summary>The colour at <paramref name="index"/>, or transparent black if it is out of range.</summary>
    public (byte R, byte G, byte B, byte A) this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                return (0, 0, 0, 0);

            int at = index * 4;
            return (_colors[at], _colors[at + 1], _colors[at + 2], _colors[at + 3]);
        }
    }
}
