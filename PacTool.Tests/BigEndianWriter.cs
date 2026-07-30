using System.Buffers.Binary;
using System.Text;

namespace PacTool.Tests;

/// <summary>
/// Builds big-endian test fixtures. The formats under test are all big-endian and full of
/// self-referential offsets, so the tests construct their inputs byte by byte rather than
/// depending on sample files that may or may not be present.
/// </summary>
internal sealed class BigEndianWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>Bytes written so far.</summary>
    public int Length => (int)_stream.Length;

    public BigEndianWriter U8(int value)
    {
        _stream.WriteByte((byte)value);
        return this;
    }

    public BigEndianWriter U16(int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        _stream.Write(buffer);
        return this;
    }

    public BigEndianWriter U32(long value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
        _stream.Write(buffer);
        return this;
    }

    public BigEndianWriter F32(float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    /// <summary>Writes raw bytes.</summary>
    public BigEndianWriter Bytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        return this;
    }

    /// <summary>Writes ASCII without padding or a terminator.</summary>
    public BigEndianWriter Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

    /// <summary>Writes ASCII into a fixed-width field, NUL-padded.</summary>
    public BigEndianWriter Field(string text, int width)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(text);
        if (encoded.Length > width)
            throw new ArgumentException($"'{text}' does not fit in {width} bytes.", nameof(text));

        Bytes(encoded);
        return Zeros(width - encoded.Length);
    }

    /// <summary>Writes <paramref name="count"/> zero bytes.</summary>
    public BigEndianWriter Zeros(int count)
    {
        for (int i = 0; i < count; i++)
            _stream.WriteByte(0);

        return this;
    }

    /// <summary>Pads with zeros up to <paramref name="offset"/>.</summary>
    public BigEndianWriter PadTo(int offset) => Zeros(Math.Max(0, offset - Length));

    /// <summary>Overwrites four bytes at <paramref name="offset"/>, for patching offsets in afterwards.</summary>
    public BigEndianWriter PatchU32(int offset, long value)
    {
        byte[] buffer = _stream.GetBuffer();
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), (uint)value);
        return this;
    }

    /// <summary>Everything written so far.</summary>
    public byte[] ToArray() => _stream.ToArray();
}
