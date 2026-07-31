using System.Buffers.Binary;
using System.Text;

namespace PacTool.Export;

/// <summary>
/// Big-endian output with the offset patching every J3D section needs: sizes and internal pointers
/// are only known once the thing they describe has been written.
/// </summary>
public sealed class J3dWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>Bytes written so far.</summary>
    public int Length => (int)_stream.Length;

    public J3dWriter U8(int value)
    {
        _stream.WriteByte((byte)value);
        return this;
    }

    public J3dWriter S8(int value) => U8(value & 0xFF);

    public J3dWriter U16(int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        _stream.Write(buffer);
        return this;
    }

    public J3dWriter S16(int value) => U16(value & 0xFFFF);

    public J3dWriter U32(long value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
        _stream.Write(buffer);
        return this;
    }

    public J3dWriter F32(float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    /// <summary>Writes raw bytes.</summary>
    public J3dWriter Bytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        return this;
    }

    /// <summary>Writes ASCII with no padding or terminator.</summary>
    public J3dWriter Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

    /// <summary>Writes <paramref name="count"/> bytes of <paramref name="value"/>.</summary>
    public J3dWriter Fill(int count, byte value = 0)
    {
        for (int i = 0; i < count; i++)
            _stream.WriteByte(value);

        return this;
    }

    /// <summary>
    /// Pads up to a multiple of <paramref name="alignment"/>. J3D pads with the ASCII of
    /// "This is padding data to alignment", which readers rely on only for its length.
    /// </summary>
    public J3dWriter Align(int alignment = 32)
    {
        const string Filler = "This is padding data to alignment.....";
        int target = (Length + alignment - 1) / alignment * alignment;
        for (int i = 0; Length < target; i++)
            _stream.WriteByte((byte)Filler[i % Filler.Length]);

        return this;
    }

    /// <summary>Overwrites a 32-bit value written earlier, for section sizes and internal offsets.</summary>
    public J3dWriter PatchU32(int at, long value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_stream.GetBuffer().AsSpan(at, 4), (uint)value);
        return this;
    }

    /// <summary>Overwrites a 16-bit value written earlier.</summary>
    public J3dWriter PatchU16(int at, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_stream.GetBuffer().AsSpan(at, 2), (ushort)value);
        return this;
    }

    /// <summary>Everything written so far.</summary>
    public byte[] ToArray() => _stream.ToArray();

    /// <summary>
    /// Writes a J3D string table: a count, then a hash and a relative offset per string, then the
    /// strings themselves.
    /// </summary>
    public J3dWriter StringTable(IReadOnlyList<string> names)
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
