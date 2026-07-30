using System.Buffers.Binary;

namespace PacTool.Formats;

/// <summary>
/// Nintendo's Yaz0 run-length compression. GameCube models are usually stored compressed, so this
/// runs first when a file is sniffed: a Yaz0 wrapper is peeled off and the contents are identified
/// again.
///
/// <code>
///   0x00  char[4]  "Yaz0"
///   0x04  u32      decompressedSize
///   0x08  u8[8]    reserved
///   0x10  ...      groups of a control byte and the eight chunks it describes
/// </code>
///
/// Each control byte's bits run from the most significant down. A set bit copies one literal byte;
/// a clear bit introduces a back-reference whose first nibble is the length minus two, or, when
/// that nibble is zero, a third byte holding the length minus 0x12.
/// </summary>
public static class Yaz0
{
    /// <summary>True if <paramref name="data"/> starts with a Yaz0 header.</summary>
    public static bool IsCompressed(ReadOnlySpan<byte> data) =>
        data.Length >= 0x10 && data[..4].SequenceEqual("Yaz0"u8);

    /// <summary>Decompresses a Yaz0 stream.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data, string sourceName)
    {
        if (!IsCompressed(data))
            throw new PacFormatException($"{sourceName}: not a Yaz0 stream.");

        uint size = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (size > 0x4000_0000)
            throw new PacFormatException($"{sourceName}: the header claims {size:N0} decompressed bytes, which is not plausible.");

        var output = new byte[size];
        int source = 0x10;
        int target = 0;
        byte control = 0;
        int bitsLeft = 0;

        while (target < output.Length)
        {
            if (bitsLeft == 0)
            {
                if (source >= data.Length)
                    throw new PacFormatException($"{sourceName}: the stream ends {output.Length - target:N0} byte(s) early.");
                control = data[source++];
                bitsLeft = 8;
            }

            bitsLeft--;
            if ((control & 0x80) != 0)
            {
                if (source >= data.Length)
                    throw new PacFormatException($"{sourceName}: the stream ends part-way through a literal.");
                output[target++] = data[source++];
            }
            else
            {
                if (source + 2 > data.Length)
                    throw new PacFormatException($"{sourceName}: the stream ends part-way through a back-reference.");

                int pair = BinaryPrimitives.ReadUInt16BigEndian(data[source..]);
                source += 2;

                int distance = (pair & 0x0FFF) + 1;
                int length = pair >> 12;
                if (length == 0)
                {
                    if (source >= data.Length)
                        throw new PacFormatException($"{sourceName}: the stream ends part-way through a long back-reference.");
                    length = data[source++] + 0x12;
                }
                else
                {
                    length += 2;
                }

                int from = target - distance;
                if (from < 0)
                    throw new PacFormatException($"{sourceName}: a back-reference at output offset {target} reaches before the start of the data.");

                // Copy one byte at a time: runs routinely overlap their own source.
                for (int i = 0; i < length && target < output.Length; i++)
                    output[target++] = output[from + i];
            }

            control <<= 1;
        }

        return output;
    }
}
