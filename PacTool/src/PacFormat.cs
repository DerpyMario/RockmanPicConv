namespace PacTool;

/// <summary>
/// On-disk constants for the "CAPR" container, stored with the extension <c>.pac</c>.
///
/// The archive is a flat chain of chunks with no central directory. Each chunk is a
/// 32-byte big-endian header immediately followed by its payload; the next chunk starts
/// right after that payload, and the chain ends at end of file:
///
/// <code>
///   0x00  char[4]   magic      "CAPR"
///   0x04  u32       size       payload length in bytes
///   0x08  u32       reserved0  always 0 in retail data
///   0x0C  u32       reserved1  always 0 in retail data
///   0x10  char[16]  name       ASCII, NUL-padded (no terminator when exactly 16 chars)
///   0x20  u8[size]  payload
/// </code>
///
/// Member names are base names only - the original build tool (<c>RockmanFilePack</c>)
/// stripped the directory part of each path in its input list. Names are not unique:
/// the same file may legitimately appear twice in one archive.
/// </summary>
public static class PacFormat
{
    /// <summary>Size of a member header in bytes.</summary>
    public const int HeaderSize = 0x20;

    /// <summary>Size of the fixed-width name field in bytes.</summary>
    public const int NameFieldSize = 16;

    /// <summary>
    /// Payload alignment. Every one of the 730 members across the 196 retail archives has a
    /// size that is a multiple of 32, so packing keeps that invariant by default.
    /// </summary>
    public const int DefaultAlignment = 32;

    /// <summary>Chunk magic, big-endian ASCII.</summary>
    public static ReadOnlySpan<byte> Magic => "CAPR"u8;
}

/// <summary>Raised when an archive does not match <see cref="PacFormat"/>.</summary>
public sealed class PacFormatException : Exception
{
    public PacFormatException(string message) : base(message) { }
}
