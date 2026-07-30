using System.Buffers.Binary;

namespace PacTool;

/// <summary>Writes a <c>.pac</c> archive from an ordered list of members.</summary>
public static class PacBuilder
{
    /// <summary>Builds an archive on disk. The output is written to a temporary file and moved
    /// into place, so a failure part-way through cannot leave a truncated archive behind.</summary>
    public static void Build(string outputPath, IReadOnlyList<PacSource> sources, int alignment)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Environment.ProcessId}.tmp");

        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Build(output, sources, alignment);
            }
            File.Move(temporary, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Builds an archive into <paramref name="destination"/>.</summary>
    public static void Build(Stream destination, IReadOnlyList<PacSource> sources, int alignment)
    {
        if (alignment < 1)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be at least 1.");

        Span<byte> header = stackalloc byte[PacFormat.HeaderSize];
        byte[] buffer = new byte[1 << 16];

        foreach (PacSource source in sources)
        {
            long padded = Align(source.Length, alignment);
            if (padded > uint.MaxValue)
                throw new PacFormatException(
                    $"{source.Origin}: payload is {source.Length} bytes; a member cannot exceed {uint.MaxValue}.");

            header.Clear();
            PacFormat.Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..8], (uint)padded);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..12], source.Reserved0);
            BinaryPrimitives.WriteUInt32BigEndian(header[12..16], source.Reserved1);
            source.WriteNameTo(header.Slice(0x10, PacFormat.NameFieldSize));
            destination.Write(header);

            long written = 0;
            using (Stream payload = source.Open())
            {
                int got;
                while ((got = payload.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += got;
                    if (written > source.Length)
                        throw new PacFormatException(
                            $"{source.Origin}: grew while being packed; expected {source.Length} bytes.");
                    destination.Write(buffer, 0, got);
                }
            }

            if (written != source.Length)
                throw new PacFormatException(
                    $"{source.Origin}: read {written} bytes but expected {source.Length}.");

            WriteZeros(destination, padded - written, buffer);
        }
    }

    /// <summary>Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>.</summary>
    public static long Align(long value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    private static void WriteZeros(Stream destination, long count, byte[] scratch)
    {
        if (count <= 0)
            return;

        Array.Clear(scratch);
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, scratch.Length);
            destination.Write(scratch, 0, chunk);
            count -= chunk;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Leaving a stray temp file behind is not worth masking the original failure.
        }
    }
}
