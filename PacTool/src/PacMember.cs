namespace PacTool;

/// <summary>One chunk of a <c>.pac</c> archive, as found on disk.</summary>
public sealed class PacMember
{
    /// <summary>Index of this member in the chain, starting at zero.</summary>
    public required int Index { get; init; }

    /// <summary>Name as stored in the 16-byte name field, with the NUL padding removed.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute offset of the member header within the archive.</summary>
    public required long HeaderOffset { get; init; }

    /// <summary>Payload length in bytes, as stated by the header.</summary>
    public required uint Size { get; init; }

    /// <summary>Header field at 0x08. Zero in all retail data; preserved for exact rebuilds.</summary>
    public uint Reserved0 { get; init; }

    /// <summary>Header field at 0x0C. Zero in all retail data; preserved for exact rebuilds.</summary>
    public uint Reserved1 { get; init; }

    /// <summary>Absolute offset of the payload within the archive.</summary>
    public long DataOffset => HeaderOffset + PacFormat.HeaderSize;

    /// <summary>Total on-disk footprint of this member, header included.</summary>
    public long TotalSize => PacFormat.HeaderSize + (long)Size;
}
