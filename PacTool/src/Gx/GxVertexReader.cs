using System.Buffers.Binary;

namespace PacTool.Gx;

/// <summary>Reads the direct attributes out of a vertex laid out by a <see cref="GxVertexFormat"/>.</summary>
public static class GxVertexReader
{
    /// <summary>
    /// Reads the position. Fixed-point components are divided by 2 to the power of the format's
    /// position fraction, which is what turns the stored integers back into model units.
    /// </summary>
    public static (float X, float Y, float Z) ReadPosition(GxVertexFormat format, ReadOnlySpan<byte> vertex)
    {
        int at = format.PositionOffset;
        if (at < 0 || format.Position != GxAttributeMode.Direct)
            return (0, 0, 0);

        float scale = Scale(format.PositionFormat, format.PositionFraction);
        int size = GxVertexFormat.ComponentSize(format.PositionFormat);
        float x = ReadComponent(format.PositionFormat, vertex, at) * scale;
        float y = ReadComponent(format.PositionFormat, vertex, at + size) * scale;
        float z = format.PositionComponents >= 3
            ? ReadComponent(format.PositionFormat, vertex, at + size * 2) * scale
            : 0f;
        return (x, y, z);
    }

    /// <summary>
    /// Reads the normal. Its fraction is fixed by the hardware rather than taken from the attribute
    /// table: 6 bits for a signed byte, 14 for a signed short.
    /// </summary>
    public static (float X, float Y, float Z) ReadNormal(GxVertexFormat format, ReadOnlySpan<byte> vertex)
    {
        int at = format.NormalOffset;
        if (at < 0 || format.Normal != GxAttributeMode.Direct)
            return (0, 0, 0);

        float scale = Scale(format.NormalFormat, format.NormalFraction);
        int size = GxVertexFormat.ComponentSize(format.NormalFormat);
        return (ReadComponent(format.NormalFormat, vertex, at) * scale,
                ReadComponent(format.NormalFormat, vertex, at + size) * scale,
                ReadComponent(format.NormalFormat, vertex, at + size * 2) * scale);
    }

    /// <summary>Reads one texture coordinate set.</summary>
    public static (float U, float V) ReadTexCoord(GxVertexFormat format, ReadOnlySpan<byte> vertex, int set)
    {
        int at = format.TexCoordOffset(set);
        if (at < 0 || format.TexCoord[set] != GxAttributeMode.Direct)
            return (0, 0);

        float scale = Scale(format.TexCoordFormat[set], format.TexCoordFraction[set]);
        int size = GxVertexFormat.ComponentSize(format.TexCoordFormat[set]);
        float u = ReadComponent(format.TexCoordFormat[set], vertex, at) * scale;
        float v = format.TexCoordComponents[set] >= 2
            ? ReadComponent(format.TexCoordFormat[set], vertex, at + size) * scale
            : 0f;
        return (u, v);
    }

    /// <summary>Reads one component in whichever numeric type the format names. Floats are not scaled.</summary>
    private static float ReadComponent(GxComponentFormat format, ReadOnlySpan<byte> data, int at) => format switch
    {
        GxComponentFormat.UByte => data[at],
        GxComponentFormat.Byte => (sbyte)data[at],
        GxComponentFormat.UShort => BinaryPrimitives.ReadUInt16BigEndian(data[at..]),
        GxComponentFormat.Short => BinaryPrimitives.ReadInt16BigEndian(data[at..]),
        _ => BinaryPrimitives.ReadSingleBigEndian(data[at..]),
    };

    /// <summary>
    /// The divisor a fixed-point attribute needs. A float attribute carries its own scale, so the
    /// fraction field does not apply to it - the hardware ignores it there too.
    /// </summary>
    private static float Scale(GxComponentFormat format, int fraction) =>
        format == GxComponentFormat.Float ? 1f : 1f / (1 << fraction);
}
