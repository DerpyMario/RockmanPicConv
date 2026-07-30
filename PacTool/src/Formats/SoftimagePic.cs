using System.Buffers.Binary;
using PacTool.Imaging;

namespace PacTool.Formats;

/// <summary>
/// A Softimage PIC image - the source art the effect textures were authored from. The reference
/// data has 99 of them under <c>StageData/fixed/Effect/</c>, all written by the same Photoshop
/// plug-in, whose credit line sits in the comment field.
///
/// <code>
///   0x00  u32       magic       0x5380F634
///   0x04  f32       version
///   0x08  char[80]  comment
///   0x58  char[4]   "PICT"
///   0x5C  u16       width
///   0x5E  u16       height
///   0x60  f32       pixelAspect
///   0x64  u16       fields      3 for a full frame
///   0x66  u16       padding
///   0x68  ...       channel packets, then the scan lines
/// </code>
///
/// A channel packet is four bytes: a "chained" flag, the bits per component, an encoding
/// (0 raw, 2 run-length) and a mask naming the channels it carries - 0x80 red, 0x40 green,
/// 0x20 blue, 0x10 alpha. Packets repeat while the chained flag is set, so the usual layout is one
/// RLE packet for RGB followed by one for alpha. Every scan line is encoded independently, one
/// channel group at a time.
///
/// The run-length encoding is the "mixed" variant: a lead byte under 128 introduces that many plus
/// one literal pixels, 128 introduces a 16-bit run length, and anything above 128 is a run of
/// <c>lead - 127</c>. Reading it that way consumes all 99 reference files exactly to their last
/// byte, which the off-by-one alternatives do not.
/// </summary>
public sealed class SoftimagePic
{
    private const uint Magic = 0x5380F634;

    /// <summary>The comment field, which names the tool that wrote the file.</summary>
    public required string Comment { get; init; }

    /// <summary>File format version from the header.</summary>
    public required float Version { get; init; }

    /// <summary>Width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Channel packets in stored order.</summary>
    public required IReadOnlyList<SoftimagePicChannel> Channels { get; init; }

    /// <summary>The decoded image.</summary>
    public required Rgba32Image Image { get; init; }

    /// <summary>True if <paramref name="data"/> starts with a Softimage PIC header.</summary>
    public static bool LooksLikePic(ReadOnlySpan<byte> data) =>
        data.Length >= 0x68 &&
        BinaryPrimitives.ReadUInt32BigEndian(data) == Magic &&
        data.Slice(0x58, 4).SequenceEqual("PICT"u8);

    /// <summary>Decodes a Softimage PIC.</summary>
    public static SoftimagePic Parse(ReadOnlySpan<byte> data, string sourceName)
    {
        if (!LooksLikePic(data))
            throw new PacFormatException($"{sourceName}: not a Softimage PIC (no 0x5380F634 magic or 'PICT' tag).");

        int width = BinaryPrimitives.ReadUInt16BigEndian(data[0x5C..]);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[0x5E..]);
        if (width <= 0 || height <= 0)
            throw new PacFormatException($"{sourceName}: image is {width}x{height}.");

        var channels = new List<SoftimagePicChannel>();
        int offset = 0x68;
        while (offset + 4 <= data.Length)
        {
            var channel = new SoftimagePicChannel
            {
                BitsPerComponent = data[offset + 1],
                Encoding = data[offset + 2],
                ChannelMask = data[offset + 3],
            };
            channels.Add(channel);

            bool chained = data[offset] != 0;
            offset += 4;
            if (!chained)
                break;
        }

        if (channels.Count == 0)
            throw new PacFormatException($"{sourceName}: the file declares no channel packets.");

        foreach (SoftimagePicChannel channel in channels)
        {
            if (channel.BitsPerComponent != 8)
                throw new PacFormatException($"{sourceName}: {channel.BitsPerComponent} bits per component is not supported; only 8 is.");
            if (channel.Encoding is not (0 or 2))
                throw new PacFormatException($"{sourceName}: channel encoding {channel.Encoding} is not raw (0) or run-length (2).");
        }

        var image = new Rgba32Image(width, height);
        // Channels the file does not carry stay at their defaults: opaque black.
        for (int i = 3; i < image.Pixels.Length; i += 4)
            image.Pixels[i] = 0xFF;

        // Channel packets carry disjoint channels, so one RGBA scratch row collects them all.
        int written = channels.Aggregate(0, (mask, c) => mask | c.ChannelMask);
        var row = new byte[width * 4];

        for (int y = 0; y < height; y++)
        {
            foreach (SoftimagePicChannel channel in channels)
                offset = ReadRow(data, offset, row, width, channel, sourceName);

            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                if ((written & 0x80) != 0) image.Pixels[at] = row[x * 4];
                if ((written & 0x40) != 0) image.Pixels[at + 1] = row[x * 4 + 1];
                if ((written & 0x20) != 0) image.Pixels[at + 2] = row[x * 4 + 2];
                if ((written & 0x10) != 0) image.Pixels[at + 3] = row[x * 4 + 3];
            }
        }

        return new SoftimagePic
        {
            Comment = Ascii.Decode(data.Slice(8, 80)),
            Version = BinaryPrimitives.ReadSingleBigEndian(data[4..]),
            Width = width,
            Height = height,
            Channels = channels,
            Image = image,
        };
    }

    /// <summary>
    /// Decodes one scan line for one channel packet into <paramref name="row"/>, which holds RGBA
    /// bytes per pixel regardless of how many components the packet carries.
    /// </summary>
    private static int ReadRow(ReadOnlySpan<byte> data, int offset, byte[] row, int width,
                               SoftimagePicChannel channel, string sourceName)
    {
        if (channel.Encoding == 0)
        {
            for (int x = 0; x < width; x++)
                offset = ReadPixel(data, offset, row, x, channel, sourceName);

            return offset;
        }

        int written = 0;
        while (written < width)
        {
            if (offset >= data.Length)
                throw new PacFormatException($"{sourceName}: the pixel data ends part-way through a scan line.");

            int count = data[offset++];
            bool repeat = count >= 128;

            if (count == 128)
            {
                // A long run: a 16-bit length, then the single pixel to repeat.
                if (offset + 2 > data.Length)
                    throw new PacFormatException($"{sourceName}: a run length is cut off by the end of the file.");
                count = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                offset += 2;
            }
            else if (repeat)
            {
                // A short run: 1 to 127 copies of the pixel that follows.
                count -= 127;
            }
            else
            {
                // Literal pixels: 1 to 128 of them, each stored in full.
                count += 1;
            }

            if (written + count > width)
                throw new PacFormatException($"{sourceName}: a run of {count} would overrun scan line width {width}.");
            if (repeat)
            {
                int source = offset;
                offset = ReadPixel(data, offset, row, written, channel, sourceName);
                for (int i = 1; i < count; i++)
                    ReadPixel(data, source, row, written + i, channel, sourceName);
                written += count;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    offset = ReadPixel(data, offset, row, written + i, channel, sourceName);
                written += count;
            }

            if (count == 0)
                throw new PacFormatException($"{sourceName}: a zero-length run would never finish the scan line.");
        }

        return offset;
    }

    /// <summary>Copies one pixel's components out of the stream and into the channels the packet names.</summary>
    private static int ReadPixel(ReadOnlySpan<byte> data, int offset, byte[] row, int x,
                                 SoftimagePicChannel channel, string sourceName)
    {
        if (offset + channel.ComponentCount > data.Length)
            throw new PacFormatException($"{sourceName}: the pixel data ends part-way through a pixel.");

        int at = x * 4;
        if ((channel.ChannelMask & 0x80) != 0) row[at] = data[offset++];
        if ((channel.ChannelMask & 0x40) != 0) row[at + 1] = data[offset++];
        if ((channel.ChannelMask & 0x20) != 0) row[at + 2] = data[offset++];
        if ((channel.ChannelMask & 0x10) != 0) row[at + 3] = data[offset++];
        return offset;
    }

    /// <summary>One-line summary for listings.</summary>
    public string Describe() =>
        $"Softimage PIC {Width}x{Height}, {Channels.Count} channel packet(s): " +
        string.Join(" + ", Channels.Select(c => c.Describe()));
}

/// <summary>One channel packet of a <see cref="SoftimagePic"/>.</summary>
public sealed class SoftimagePicChannel
{
    /// <summary>Bits each component occupies. Only 8 is supported.</summary>
    public required byte BitsPerComponent { get; init; }

    /// <summary>0 for raw pixels, 2 for run-length encoding.</summary>
    public required byte Encoding { get; init; }

    /// <summary>Bit mask of the channels carried: 0x80 red, 0x40 green, 0x20 blue, 0x10 alpha.</summary>
    public required byte ChannelMask { get; init; }

    /// <summary>Number of components stored per pixel by this packet.</summary>
    public int ComponentCount => System.Numerics.BitOperations.PopCount((uint)(ChannelMask & 0xF0));

    /// <summary>One-line summary.</summary>
    public string Describe()
    {
        string channels = string.Concat(
            (ChannelMask & 0x80) != 0 ? "R" : "",
            (ChannelMask & 0x40) != 0 ? "G" : "",
            (ChannelMask & 0x20) != 0 ? "B" : "",
            (ChannelMask & 0x10) != 0 ? "A" : "");
        return $"{(channels.Length > 0 ? channels : "-")} {(Encoding == 2 ? "RLE" : "raw")}";
    }
}
