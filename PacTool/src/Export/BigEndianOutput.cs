using System.Buffers.Binary;
using System.Text;

namespace PacTool.Export;

/// <summary>
/// Big-endian output with the offset patching these GameCube formats need: sizes and internal
/// pointers are only known once the thing they describe has been written, so they are reserved,
/// filled in afterwards, and never computed twice. Used by the J3D and TPL writers alike.
/// </summary>
public sealed class BigEndianOutput
{
    private readonly MemoryStream _stream = new();

    /// <summary>Bytes written so far.</summary>
    public int Length => (int)_stream.Length;

    public BigEndianOutput U8(int value)
    {
        _stream.WriteByte((byte)value);
        return this;
    }

    public BigEndianOutput S8(int value) => U8(value & 0xFF);

    public BigEndianOutput U16(int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        _stream.Write(buffer);
        return this;
    }

    public BigEndianOutput S16(int value) => U16(value & 0xFFFF);

    public BigEndianOutput U32(long value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
        _stream.Write(buffer);
        return this;
    }

    public BigEndianOutput F32(float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    /// <summary>Writes raw bytes.</summary>
    public BigEndianOutput Bytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        return this;
    }

    /// <summary>Writes ASCII with no padding or terminator.</summary>
    public BigEndianOutput Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

    /// <summary>Writes <paramref name="count"/> bytes of <paramref name="value"/>.</summary>
    public BigEndianOutput Fill(int count, byte value = 0)
    {
        for (int i = 0; i < count; i++)
            _stream.WriteByte(value);

        return this;
    }

    /// <summary>
    /// Pads up to a multiple of <paramref name="alignment"/>. J3D pads with the ASCII of
    /// "This is padding data to alignment", which readers rely on only for its length; pass
    /// <paramref name="fill"/> for formats that pad with a plain byte, as TPL does with zero.
    /// </summary>
    public BigEndianOutput Align(int alignment = 32, byte? fill = null)
    {
        const string Filler = "This is padding data to alignment.....";
        int target = (Length + alignment - 1) / alignment * alignment;
        for (int i = 0; Length < target; i++)
            _stream.WriteByte(fill ?? (byte)Filler[i % Filler.Length]);

        return this;
    }

    /// <summary>Overwrites a 32-bit value written earlier, for section sizes and internal offsets.</summary>
    public BigEndianOutput PatchU32(int at, long value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_stream.GetBuffer().AsSpan(at, 4), (uint)value);
        return this;
    }

    /// <summary>Overwrites a 16-bit value written earlier.</summary>
    public BigEndianOutput PatchU16(int at, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_stream.GetBuffer().AsSpan(at, 2), (ushort)value);
        return this;
    }

    /// <summary>Overwrites a 32-bit float written earlier.</summary>
    public BigEndianOutput PatchF32(int at, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(_stream.GetBuffer().AsSpan(at, 4), value);
        return this;
    }

    /// <summary>Everything written so far.</summary>
    public byte[] ToArray() => _stream.ToArray();

    /// <summary>
    /// Writes a J3D string table: a count, then a hash and a relative offset per string, then the
    /// strings themselves.
    /// </summary>
    public BigEndianOutput StringTable(IReadOnlyList<string> names)
    {
        int start = Length;
        U16(names.Count).U16(0xFFFF);
        int entries = Length;
        Fill(names.Count * 4);

        for (int i = 0; i < names.Count; i++)
        {
            PatchU16(entries + i * 4, Hash(names[i]));
            PatchU16(entries + i * 4 + 2, Length - start);
            Ascii(names[i]).U8(0);
        }

        return this;
    }

    /// <summary>The hash J3D string tables carry: a rolling multiply-and-add over the characters.</summary>
    public static int Hash(string name)
    {
        int hash = 0;
        foreach (char c in name)
            hash = (hash * 3 + c) & 0xFFFF;

        return hash;
    }
}
