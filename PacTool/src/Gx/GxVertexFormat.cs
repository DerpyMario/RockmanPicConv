namespace PacTool.Gx;

/// <summary>How an attribute reaches the graphics processor.</summary>
public enum GxAttributeMode
{
    /// <summary>The vertex does not carry this attribute.</summary>
    NotPresent = 0,

    /// <summary>The value sits in the vertex itself.</summary>
    Direct = 1,

    /// <summary>The vertex carries a one-byte index into an array.</summary>
    Index8 = 2,

    /// <summary>The vertex carries a two-byte index into an array.</summary>
    Index16 = 3,
}

/// <summary>Numeric type of an attribute's components.</summary>
public enum GxComponentFormat
{
    /// <summary>Unsigned 8-bit. Not valid for normals.</summary>
    UByte = 0,

    /// <summary>Signed 8-bit.</summary>
    Byte = 1,

    /// <summary>Unsigned 16-bit. Not valid for normals.</summary>
    UShort = 2,

    /// <summary>Signed 16-bit.</summary>
    Short = 3,

    /// <summary>32-bit IEEE float.</summary>
    Float = 4,
}

/// <summary>Packing of a colour attribute.</summary>
public enum GxColorFormat
{
    /// <summary>5 red, 6 green, 5 blue, 2 bytes.</summary>
    Rgb565 = 0,

    /// <summary>8 bits per channel, 3 bytes.</summary>
    Rgb888 = 1,

    /// <summary>8 bits per channel plus a pad byte, 4 bytes.</summary>
    Rgb888X = 2,

    /// <summary>4 bits per channel, 2 bytes.</summary>
    Rgba4444 = 3,

    /// <summary>6 bits per channel, 3 bytes.</summary>
    Rgba6666 = 4,

    /// <summary>8 bits per channel, 4 bytes.</summary>
    Rgba8888 = 5,
}

/// <summary>
/// One GX vertex layout: which attributes a vertex carries, in what order, and in what numeric
/// form. On hardware this is the pair of command-processor register groups the manual calls the
/// vertex descriptor (VCD, registers 0x50 and 0x60) and the vertex attribute table (VAT, registers
/// 0x70, 0x80 and 0x90), of which there are eight, selected by the low three bits of a primitive
/// opcode.
///
/// Attributes always appear in a vertex in this fixed order, skipping any that are not present:
/// the position-matrix index, the eight texture-matrix indices, position, normal, the two colours,
/// then the eight texture coordinates.
/// </summary>
public sealed class GxVertexFormat
{
    /// <summary>Number of texture coordinate sets the hardware supports.</summary>
    public const int TexCoordCount = 8;

    /// <summary>A one-byte matrix index precedes the vertex when set.</summary>
    public bool PositionMatrixIndex { get; init; }

    /// <summary>Per-texture matrix indices, one byte each when set.</summary>
    public bool[] TextureMatrixIndex { get; init; } = new bool[TexCoordCount];

    /// <summary>How the position reaches the hardware.</summary>
    public GxAttributeMode Position { get; init; } = GxAttributeMode.Direct;

    /// <summary>Position components: 2 for XY, 3 for XYZ.</summary>
    public int PositionComponents { get; init; } = 3;

    /// <summary>Numeric type of the position components.</summary>
    public GxComponentFormat PositionFormat { get; init; } = GxComponentFormat.Short;

    /// <summary>Fractional bits in a fixed-point position, so a stored value is divided by 2^n.</summary>
    public int PositionFraction { get; init; }

    /// <summary>How the normal reaches the hardware.</summary>
    public GxAttributeMode Normal { get; init; } = GxAttributeMode.NotPresent;

    /// <summary>True when the attribute is a normal, tangent and binormal triple rather than a single normal.</summary>
    public bool NormalTangentBinormal { get; init; }

    /// <summary>Numeric type of the normal components.</summary>
    public GxComponentFormat NormalFormat { get; init; } = GxComponentFormat.Byte;

    /// <summary>An indexed normal-tangent-binormal triple carries three indices rather than one.</summary>
    public bool NormalIndex3 { get; init; }

    /// <summary>How each colour reaches the hardware.</summary>
    public GxAttributeMode[] Color { get; init; } = [GxAttributeMode.NotPresent, GxAttributeMode.NotPresent];

    /// <summary>Packing of each colour.</summary>
    public GxColorFormat[] ColorFormat { get; init; } = [GxColorFormat.Rgba8888, GxColorFormat.Rgba8888];

    /// <summary>How each texture coordinate set reaches the hardware.</summary>
    public GxAttributeMode[] TexCoord { get; init; } = new GxAttributeMode[TexCoordCount];

    /// <summary>Components per texture coordinate: 1 for S, 2 for ST.</summary>
    public int[] TexCoordComponents { get; init; } = [2, 2, 2, 2, 2, 2, 2, 2];

    /// <summary>Numeric type of each texture coordinate.</summary>
    public GxComponentFormat[] TexCoordFormat { get; init; } =
        [GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short,
         GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short];

    /// <summary>Fractional bits in each fixed-point texture coordinate.</summary>
    public int[] TexCoordFraction { get; init; } = new int[TexCoordCount];

    /// <summary>
    /// Normals in fixed point have a fraction the hardware fixes rather than taking from the VAT:
    /// 6 bits for a signed byte and 14 for a signed short, so both cover roughly -2 to 2.
    /// </summary>
    public int NormalFraction => NormalFormat switch
    {
        GxComponentFormat.Byte or GxComponentFormat.UByte => 6,
        GxComponentFormat.Short or GxComponentFormat.UShort => 14,
        _ => 0,
    };

    /// <summary>Bytes one vertex occupies.</summary>
    public int VertexSize
    {
        get
        {
            int size = PositionMatrixIndex ? 1 : 0;
            foreach (bool present in TextureMatrixIndex)
                size += present ? 1 : 0;

            size += AttributeSize(Position, ComponentSize(PositionFormat) * PositionComponents);
            size += NormalSize();

            for (int i = 0; i < Color.Length; i++)
                size += AttributeSize(Color[i], ColorSize(ColorFormat[i]));

            for (int i = 0; i < TexCoordCount; i++)
                size += AttributeSize(TexCoord[i], ComponentSize(TexCoordFormat[i]) * TexCoordComponents[i]);

            return size;
        }
    }

    /// <summary>Offset of the position within a vertex, or -1 when it is absent.</summary>
    public int PositionOffset
    {
        get
        {
            if (Position == GxAttributeMode.NotPresent)
                return -1;

            int offset = PositionMatrixIndex ? 1 : 0;
            foreach (bool present in TextureMatrixIndex)
                offset += present ? 1 : 0;

            return offset;
        }
    }

    /// <summary>Offset of the normal within a vertex, or -1 when it is absent.</summary>
    public int NormalOffset
    {
        get
        {
            if (Normal == GxAttributeMode.NotPresent)
                return -1;

            int offset = PositionOffset < 0 ? 0 : PositionOffset;
            return offset + AttributeSize(Position, ComponentSize(PositionFormat) * PositionComponents);
        }
    }

    /// <summary>Offset of texture coordinate set <paramref name="set"/>, or -1 when it is absent.</summary>
    public int TexCoordOffset(int set)
    {
        if ((uint)set >= TexCoordCount || TexCoord[set] == GxAttributeMode.NotPresent)
            return -1;

        int offset = PositionMatrixIndex ? 1 : 0;
        foreach (bool present in TextureMatrixIndex)
            offset += present ? 1 : 0;

        offset += AttributeSize(Position, ComponentSize(PositionFormat) * PositionComponents);
        offset += NormalSize();
        for (int i = 0; i < Color.Length; i++)
            offset += AttributeSize(Color[i], ColorSize(ColorFormat[i]));

        for (int i = 0; i < set; i++)
            offset += AttributeSize(TexCoord[i], ComponentSize(TexCoordFormat[i]) * TexCoordComponents[i]);

        return offset;
    }

    private int NormalSize()
    {
        int direct = ComponentSize(NormalFormat) * (NormalTangentBinormal ? 9 : 3);
        if (Normal is GxAttributeMode.Direct)
            return direct;
        if (Normal is GxAttributeMode.NotPresent)
            return 0;

        // An indexed normal is one index, unless index3 splits a triple into three of them.
        int indexSize = Normal == GxAttributeMode.Index8 ? 1 : 2;
        return NormalIndex3 && NormalTangentBinormal ? indexSize * 3 : indexSize;
    }

    private static int AttributeSize(GxAttributeMode mode, int directSize) => mode switch
    {
        GxAttributeMode.Direct => directSize,
        GxAttributeMode.Index8 => 1,
        GxAttributeMode.Index16 => 2,
        _ => 0,
    };

    /// <summary>Bytes one component of the given numeric type occupies.</summary>
    public static int ComponentSize(GxComponentFormat format) => format switch
    {
        GxComponentFormat.UByte or GxComponentFormat.Byte => 1,
        GxComponentFormat.UShort or GxComponentFormat.Short => 2,
        _ => 4,
    };

    /// <summary>Bytes one colour of the given packing occupies.</summary>
    public static int ColorSize(GxColorFormat format) => format switch
    {
        GxColorFormat.Rgb565 or GxColorFormat.Rgba4444 => 2,
        GxColorFormat.Rgb888 or GxColorFormat.Rgba6666 => 3,
        _ => 4,
    };

    /// <summary>A short description of the layout, for listings.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (PositionMatrixIndex)
            parts.Add("pmtx u8");
        if (Position != GxAttributeMode.NotPresent)
            parts.Add($"pos {PositionFormat.ToString().ToLowerInvariant()}[{PositionComponents}]" +
                      (PositionFraction > 0 ? $"/2^{PositionFraction}" : ""));
        if (Normal != GxAttributeMode.NotPresent)
            parts.Add($"nrm {NormalFormat.ToString().ToLowerInvariant()}[{(NormalTangentBinormal ? 9 : 3)}]" +
                      (NormalFraction > 0 ? $"/2^{NormalFraction}" : ""));
        for (int i = 0; i < Color.Length; i++)
        {
            if (Color[i] != GxAttributeMode.NotPresent)
                parts.Add($"col{i} {ColorFormat[i]}");
        }

        for (int i = 0; i < TexCoordCount; i++)
        {
            if (TexCoord[i] != GxAttributeMode.NotPresent)
            {
                parts.Add($"tex{i} {TexCoordFormat[i].ToString().ToLowerInvariant()}[{TexCoordComponents[i]}]" +
                          (TexCoordFraction[i] > 0 ? $"/2^{TexCoordFraction[i]}" : ""));
            }
        }

        return $"{VertexSize} B: {string.Join(", ", parts)}";
    }
}
