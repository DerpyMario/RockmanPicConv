using System.Buffers.Binary;
using System.IO.Compression;

namespace PacTool.Imaging;

/// <summary>
/// Writes PNG files. Deliberately minimal - one interlace-free image, 8 bits per channel, either
/// truecolour or truecolour with alpha - so that the tool keeps its "no external dependencies"
/// property. Compression comes from <see cref="ZLibStream"/>, which is part of the framework.
/// </summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Writes <paramref name="image"/> to a file, dropping the alpha channel if it is unused.</summary>
    public static void Save(Rgba32Image image, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(image, stream);
    }

    /// <summary>Writes <paramref name="image"/> to <paramref name="destination"/>.</summary>
    public static void Write(Rgba32Image image, Stream destination)
    {
        bool opaque = image.IsOpaque();
        int channels = opaque ? 3 : 4;

        destination.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], image.Height);
        header[8] = 8;                              // bit depth
        header[9] = (byte)(opaque ? 2 : 6);         // colour type: truecolour, optionally with alpha
        header[10] = 0;                             // deflate
        header[11] = 0;                             // adaptive filtering
        header[12] = 0;                             // no interlacing
        WriteChunk(destination, "IHDR"u8, header);

        WriteChunk(destination, "IDAT"u8, Deflate(image, channels));
        WriteChunk(destination, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Compresses the pixel rows. Each row is prefixed with filter type 1 ("Sub"), which predicts
    /// every byte from the pixel to its left. Textures of this vintage are full of flat runs and
    /// gradients, so that costs one byte per row and saves considerably more than filter 0 would.
    /// </summary>
    private static byte[] Deflate(Rgba32Image image, int channels)
    {
        int stride = image.Width * channels;
        byte[] row = new byte[1 + stride];
        row[0] = 1;

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                int source = y * image.Width * 4;
                for (int x = 0; x < image.Width; x++)
                {
                    int target = 1 + x * channels;
                    row[target] = image.Pixels[source];
                    row[target + 1] = image.Pixels[source + 1];
                    row[target + 2] = image.Pixels[source + 2];
                    if (channels == 4)
                        row[target + 3] = image.Pixels[source + 3];
                    source += 4;
                }

                // Filter in place, back to front, so each byte still sees its unfiltered neighbour.
                for (int i = stride; i > channels; i--)
                    row[i] -= row[i - channels];

                deflate.Write(row, 0, row.Length);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        destination.Write(length);
        destination.Write(type);
        destination.Write(data);

        uint crc = Crc32.Compute(Crc32.Compute(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        destination.Write(checksum);
    }
}

/// <summary>The CRC-32 that PNG chunks carry, kept here so the project needs no NuGet package.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    /// <summary>Folds <paramref name="data"/> into a running, un-finalised CRC.</summary>
    public static uint Compute(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc;
    }
}
