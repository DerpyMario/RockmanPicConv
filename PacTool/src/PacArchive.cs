using System.Buffers.Binary;
using System.Text;

namespace PacTool;

/// <summary>
/// Reads a <c>.pac</c> archive. The chain is walked once on open; payloads stay on disk and are
/// streamed on demand, so multi-megabyte archives never need to be held in memory.
/// </summary>
public sealed class PacArchive : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    /// <summary>Display name used in diagnostics, normally the archive path.</summary>
    public string SourceName { get; }

    /// <summary>Members in chain order.</summary>
    public IReadOnlyList<PacMember> Members { get; }

    /// <summary>Non-fatal oddities noticed while parsing. Empty for every retail archive.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Total archive length in bytes.</summary>
    public long Length => _stream.Length;

    private PacArchive(Stream stream, bool ownsStream, string sourceName,
                       List<PacMember> members, List<string> warnings)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        SourceName = sourceName;
        Members = members;
        Warnings = warnings;
    }

    /// <summary>Opens an archive from disk.</summary>
    public static PacArchive Open(string path, bool strict = false)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return Open(stream, path, strict, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens an archive from an existing seekable stream.</summary>
    public static PacArchive Open(Stream stream, string sourceName, bool strict = false, bool ownsStream = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("A seekable stream is required.", nameof(stream));

        var members = new List<PacMember>();
        var warnings = new List<string>();
        long length = stream.Length;
        long offset = 0;
        Span<byte> header = stackalloc byte[PacFormat.HeaderSize];

        while (offset < length)
        {
            long remaining = length - offset;
            if (remaining < PacFormat.HeaderSize)
                throw new PacFormatException(
                    $"{sourceName}: {remaining} trailing byte(s) at 0x{offset:X} are too few for a member header.");

            stream.Position = offset;
            stream.ReadExactly(header);

            if (!header[..4].SequenceEqual(PacFormat.Magic))
                throw new PacFormatException(
                    $"{sourceName}: expected magic 'CAPR' at 0x{offset:X}, found {Describe(header[..4])}. " +
                    (members.Count == 0
                        ? "This does not look like a CAPR archive."
                        : $"The chain broke after {members.Count} member(s)."));

            uint size = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
            uint reserved0 = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
            uint reserved1 = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
            string name = DecodeName(header.Slice(0x10, PacFormat.NameFieldSize), offset, warnings);

            if (size > remaining - PacFormat.HeaderSize)
                throw new PacFormatException(
                    $"{sourceName}: member {members.Count} ('{name}') at 0x{offset:X} claims 0x{size:X} bytes " +
                    $"but only 0x{remaining - PacFormat.HeaderSize:X} remain before end of file.");

            if (reserved0 != 0 || reserved1 != 0)
                warnings.Add($"member {members.Count} ('{name}') has non-zero reserved fields " +
                             $"(0x{reserved0:X8}, 0x{reserved1:X8}); they will be preserved.");

            if (size % PacFormat.DefaultAlignment != 0)
                warnings.Add($"member {members.Count} ('{name}') has size 0x{size:X}, " +
                             $"which is not a multiple of {PacFormat.DefaultAlignment}.");

            members.Add(new PacMember
            {
                Index = members.Count,
                Name = name,
                HeaderOffset = offset,
                Size = size,
                Reserved0 = reserved0,
                Reserved1 = reserved1,
            });

            offset += PacFormat.HeaderSize + size;
        }

        if (strict && warnings.Count > 0)
            throw new PacFormatException($"{sourceName}: {warnings[0]} (--strict)");

        return new PacArchive(stream, ownsStream, sourceName, members, warnings);
    }

    /// <summary>Copies a member's payload to <paramref name="destination"/>.</summary>
    public void CopyMemberTo(PacMember member, Stream destination)
    {
        _stream.Position = member.DataOffset;
        CopyExactly(_stream, destination, member.Size);
    }

    /// <summary>Reads a member's payload into memory.</summary>
    public byte[] ReadMember(PacMember member)
    {
        var buffer = new byte[member.Size];
        _stream.Position = member.DataOffset;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private static void CopyExactly(Stream source, Stream destination, uint count)
    {
        byte[] buffer = new byte[Math.Min(count == 0 ? 1 : count, 1 << 16)];
        uint left = count;
        while (left > 0)
        {
            int want = (int)Math.Min(left, (uint)buffer.Length);
            int got = source.Read(buffer, 0, want);
            if (got <= 0)
                throw new PacFormatException("Unexpected end of file while reading a member payload.");
            destination.Write(buffer, 0, got);
            left -= (uint)got;
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> field, long offset, List<string> warnings)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
            end = field.Length; // exactly 16 characters, no terminator

        for (int i = end + 1; i < field.Length; i++)
        {
            if (field[i] != 0)
            {
                warnings.Add($"name field at 0x{offset + 0x10:X} has non-zero bytes after its terminator; " +
                             "only the leading name is used.");
                break;
            }
        }

        var name = new StringBuilder(end);
        bool nonAscii = false;
        for (int i = 0; i < end; i++)
        {
            byte b = field[i];
            if (b is < 0x20 or > 0x7E)
            {
                nonAscii = true;
                name.Append('?');
            }
            else
            {
                name.Append((char)b);
            }
        }

        if (nonAscii)
            warnings.Add($"name field at 0x{offset + 0x10:X} contains non-ASCII bytes.");

        return name.ToString();
    }

    private static string Describe(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder("0x");
        foreach (byte b in bytes)
            sb.Append(b.ToString("X2"));
        sb.Append(" ('");
        foreach (byte b in bytes)
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        sb.Append("')");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }
}
