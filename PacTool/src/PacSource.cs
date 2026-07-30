using System.Text;

namespace PacTool;

/// <summary>One member to be written into a new archive.</summary>
public sealed class PacSource
{
    /// <summary>Name to store in the 16-byte name field.</summary>
    public required string Name { get; init; }

    /// <summary>Payload length in bytes, before alignment padding.</summary>
    public required long Length { get; init; }

    /// <summary>Opens the payload for reading.</summary>
    public required Func<Stream> Open { get; init; }

    /// <summary>Where the payload came from, used in error messages.</summary>
    public required string Origin { get; init; }

    /// <summary>Header field at 0x08.</summary>
    public uint Reserved0 { get; init; }

    /// <summary>Header field at 0x0C.</summary>
    public uint Reserved1 { get; init; }

    /// <summary>Uses a file on disk as the payload, taking the stored name from its base name.</summary>
    public static PacSource FromFile(string path, string? name = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new PacFormatException($"Input file not found: {path}");

        return new PacSource
        {
            Name = name ?? info.Name,
            Length = info.Length,
            Origin = path,
            Open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
        };
    }

    /// <summary>Uses an in-memory buffer as the payload.</summary>
    public static PacSource FromBytes(string name, byte[] data, string origin = "<memory>") => new()
    {
        Name = name,
        Length = data.Length,
        Origin = origin,
        Open = () => new MemoryStream(data, writable: false),
    };

    /// <summary>
    /// Encodes <see cref="Name"/> into the fixed-width name field, rejecting anything the
    /// format cannot represent.
    /// </summary>
    public void WriteNameTo(Span<byte> field)
    {
        if (string.IsNullOrEmpty(Name))
            throw new PacFormatException($"{Origin}: member name is empty.");

        foreach (char c in Name)
        {
            if (c is < (char)0x20 or > (char)0x7E)
                throw new PacFormatException(
                    $"{Origin}: member name '{Name}' contains a non-ASCII character; " +
                    "the name field only holds printable ASCII.");
        }

        int byteCount = Encoding.ASCII.GetByteCount(Name);
        if (byteCount > PacFormat.NameFieldSize)
            throw new PacFormatException(
                $"{Origin}: member name '{Name}' is {byteCount} bytes; " +
                $"the name field holds at most {PacFormat.NameFieldSize}.");

        field.Clear();
        Encoding.ASCII.GetBytes(Name, field);
    }
}
