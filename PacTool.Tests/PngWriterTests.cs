using System.Buffers.Binary;
using System.IO.Compression;
using PacTool.Imaging;
using Xunit;

namespace PacTool.Tests;

public class PngWriterTests
{
    [Fact]
    public void AnOpaqueImageIsWrittenAsTruecolourWithoutAlpha()
    {
        var image = new Rgba32Image(3, 2);
        Fill(image, 10, 20, 30, 255);

        PngChunks png = Write(image);

        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(png.Header));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.Header.AsSpan(4)));
        Assert.Equal(8, png.Header[8]);
        Assert.Equal(2, png.Header[9]);         // colour type 2: truecolour
        Assert.Equal(3, png.Channels);
    }

    [Fact]
    public void AnImageWithAnyTransparencyKeepsItsAlphaChannel()
    {
        var image = new Rgba32Image(2, 1);
        Fill(image, 10, 20, 30, 255);
        image.SetPixel(1, 0, 40, 50, 60, 128);

        PngChunks png = Write(image);

        Assert.Equal(6, png.Header[9]);         // colour type 6: truecolour with alpha
        Assert.Equal(4, png.Channels);
    }

    [Fact]
    public void PixelsSurviveTheFilterAndDeflateRoundTrip()
    {
        var image = new Rgba32Image(4, 3);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
                image.SetPixel(x, y, (byte)(x * 60), (byte)(y * 80), (byte)(x + y), 255);
        }

        PngChunks png = Write(image);
        byte[] pixels = png.Unfilter();

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int at = (y * 4 + x) * 3;
                Assert.Equal((byte)(x * 60), pixels[at]);
                Assert.Equal((byte)(y * 80), pixels[at + 1]);
                Assert.Equal((byte)(x + y), pixels[at + 2]);
            }
        }
    }

    [Fact]
    public void EveryChunkCarriesACorrectCrc()
    {
        var image = new Rgba32Image(2, 2);
        Fill(image, 1, 2, 3, 255);

        // Write() validates each chunk's CRC as it walks the file, so reaching IEND is the assertion.
        PngChunks png = Write(image);

        Assert.True(png.SawEnd);
    }

    [Fact]
    public void ADegenerateSizeIsRejectedRatherThanWritingAnUnreadableFile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rgba32Image(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rgba32Image(4, -1));
    }

    private static void Fill(Rgba32Image image, byte r, byte g, byte b, byte a)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
                image.SetPixel(x, y, r, g, b, a);
        }
    }

    /// <summary>Writes the image and parses the result back, checking every chunk's CRC.</summary>
    private static PngChunks Write(Rgba32Image image)
    {
        using var stream = new MemoryStream();
        PngWriter.Write(image, stream);
        return PngChunks.Parse(stream.ToArray());
    }

    /// <summary>A PNG reader just complete enough to verify what the writer produced.</summary>
    private sealed class PngChunks
    {
        public required byte[] Header { get; init; }
        public required byte[] Data { get; init; }
        public required bool SawEnd { get; init; }

        public int Width => BinaryPrimitives.ReadInt32BigEndian(Header);
        public int Height => BinaryPrimitives.ReadInt32BigEndian(Header.AsSpan(4));
        public int Channels => Header[9] == 6 ? 4 : 3;

        public static PngChunks Parse(byte[] file)
        {
            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            Assert.True(file.AsSpan(0, 8).SequenceEqual(signature), "PNG signature");

            byte[]? header = null;
            var data = new MemoryStream();
            bool sawEnd = false;
            int at = 8;

            while (at + 12 <= file.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
                string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
                byte[] payload = file[(at + 8)..(at + 8 + length)];

                uint stated = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8 + length));
                Assert.Equal(Crc32(file.AsSpan(at + 4, 4 + length)), stated);

                switch (type)
                {
                    case "IHDR": header = payload; break;
                    case "IDAT": data.Write(payload); break;
                    case "IEND": sawEnd = true; break;
                }

                at += 12 + length;
            }

            Assert.NotNull(header);
            return new PngChunks { Header = header, Data = data.ToArray(), SawEnd = sawEnd };
        }

        /// <summary>Inflates the image data and reverses the per-row "Sub" filter.</summary>
        public byte[] Unfilter()
        {
            using var compressed = new MemoryStream(Data);
            using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            inflate.CopyTo(raw);

            byte[] filtered = raw.ToArray();
            int stride = Width * Channels;
            var pixels = new byte[Height * stride];

            for (int y = 0; y < Height; y++)
            {
                int source = y * (stride + 1);
                Assert.Equal(1, filtered[source]);          // the writer always uses filter type 1
                for (int i = 0; i < stride; i++)
                {
                    byte left = i >= Channels ? pixels[y * stride + i - Channels] : (byte)0;
                    pixels[y * stride + i] = (byte)(filtered[source + 1 + i] + left);
                }
            }

            return pixels;
        }

        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }

            return crc ^ 0xFFFFFFFF;
        }
    }
}
