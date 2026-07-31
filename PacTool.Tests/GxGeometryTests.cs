using PacTool.Formats;
using PacTool.Gx;
using Xunit;

namespace PacTool.Tests;

public class GxVertexFormatTests
{
    [Fact]
    public void TheTwoLayoutsTheShippedDisplayListsUseMeasureThirteenAndSeventeenBytes()
    {
        Assert.Equal(13, ScnVertexFormats.FixedTexCoord.VertexSize);
        Assert.Equal(17, ScnVertexFormats.FloatTexCoord.VertexSize);
    }

    [Fact]
    public void AttributesSitAtTheOffsetsTheFixedOrderImplies()
    {
        GxVertexFormat format = ScnVertexFormats.FixedTexCoord;

        Assert.Equal(0, format.PositionOffset);     // position first: 3 shorts
        Assert.Equal(6, format.NormalOffset);       // then the normal: 3 bytes
        Assert.Equal(9, format.TexCoordOffset(0));  // then texture coordinate 0
        Assert.Equal(-1, format.TexCoordOffset(1)); // which is the only one present
    }

    [Fact]
    public void MatrixIndicesPushEveryOtherAttributeAlong()
    {
        var format = new GxVertexFormat
        {
            PositionMatrixIndex = true,
            TextureMatrixIndex = [true, false, false, false, false, false, false, false],
            Position = GxAttributeMode.Direct,
            PositionFormat = GxComponentFormat.Float,
            Normal = GxAttributeMode.Direct,
            NormalFormat = GxComponentFormat.Byte,
        };

        Assert.Equal(2, format.PositionOffset);      // two index bytes first
        Assert.Equal(14, format.NormalOffset);       // then twelve bytes of position
        Assert.Equal(17, format.VertexSize);
    }

    [Theory]
    // Direct positions: two or three components at one, two or four bytes each.
    [InlineData(GxAttributeMode.Direct, GxComponentFormat.Byte, 3, 3)]
    [InlineData(GxAttributeMode.Direct, GxComponentFormat.Short, 3, 6)]
    [InlineData(GxAttributeMode.Direct, GxComponentFormat.Short, 2, 4)]
    [InlineData(GxAttributeMode.Direct, GxComponentFormat.Float, 3, 12)]
    // An indexed attribute costs only its index, whatever the array holds.
    [InlineData(GxAttributeMode.Index8, GxComponentFormat.Float, 3, 1)]
    [InlineData(GxAttributeMode.Index16, GxComponentFormat.Float, 3, 2)]
    [InlineData(GxAttributeMode.NotPresent, GxComponentFormat.Float, 3, 0)]
    public void PositionSizeFollowsTheHardwareTable(GxAttributeMode mode, GxComponentFormat format,
                                                    int components, int expected)
    {
        var layout = new GxVertexFormat
        {
            Position = mode,
            PositionFormat = format,
            PositionComponents = components,
        };

        Assert.Equal(expected, layout.VertexSize);
    }

    [Fact]
    public void ANormalTangentBinormalTripleCostsThreeTimesAsMuch()
    {
        var single = new GxVertexFormat
        {
            Position = GxAttributeMode.NotPresent,
            Normal = GxAttributeMode.Direct,
            NormalFormat = GxComponentFormat.Byte,
        };
        var triple = new GxVertexFormat
        {
            Position = GxAttributeMode.NotPresent,
            Normal = GxAttributeMode.Direct,
            NormalFormat = GxComponentFormat.Byte,
            NormalTangentBinormal = true,
        };

        Assert.Equal(3, single.VertexSize);
        Assert.Equal(9, triple.VertexSize);
    }

    [Fact]
    public void IndexThreeSplitsAnIndexedTripleIntoThreeIndices()
    {
        var one = new GxVertexFormat
        {
            Position = GxAttributeMode.NotPresent,
            Normal = GxAttributeMode.Index16,
            NormalTangentBinormal = true,
        };
        var three = new GxVertexFormat
        {
            Position = GxAttributeMode.NotPresent,
            Normal = GxAttributeMode.Index16,
            NormalTangentBinormal = true,
            NormalIndex3 = true,
        };

        Assert.Equal(2, one.VertexSize);
        Assert.Equal(6, three.VertexSize);
    }

    [Theory]
    [InlineData(GxColorFormat.Rgb565, 2)]
    [InlineData(GxColorFormat.Rgb888, 3)]
    [InlineData(GxColorFormat.Rgb888X, 4)]
    [InlineData(GxColorFormat.Rgba4444, 2)]
    [InlineData(GxColorFormat.Rgba6666, 3)]
    [InlineData(GxColorFormat.Rgba8888, 4)]
    public void ColourSizeFollowsItsPacking(GxColorFormat format, int expected)
    {
        Assert.Equal(expected, GxVertexFormat.ColorSize(format));
    }

    [Fact]
    public void FixedPointNormalsUseTheFractionTheHardwareFixes()
    {
        // The VAT has no field for it: a signed byte normal is always 1/64, a short always 1/16384.
        Assert.Equal(6, new GxVertexFormat { NormalFormat = GxComponentFormat.Byte }.NormalFraction);
        Assert.Equal(14, new GxVertexFormat { NormalFormat = GxComponentFormat.Short }.NormalFraction);
        Assert.Equal(0, new GxVertexFormat { NormalFormat = GxComponentFormat.Float }.NormalFraction);
    }
}

public class GxVertexReaderTests
{
    [Fact]
    public void FixedPointComponentsAreDividedByTheirFraction()
    {
        // 0x2500 as a short with an 8-bit fraction is 37; 0x0e00 is 14; 0xd000 is -48.
        byte[] vertex = [0x25, 0x00, 0x0E, 0x00, 0xD0, 0x00, 0x00, 0x40, 0x00, 0x3F, 0x80, 0x00, 0x00, 0, 0, 0, 0];

        (float x, float y, float z) = GxVertexReader.ReadPosition(ScnVertexFormats.FloatTexCoord, vertex);

        Assert.Equal(37f, x);
        Assert.Equal(14f, y);
        Assert.Equal(-48f, z);
    }

    [Fact]
    public void ASignedByteNormalComesOutUnitLength()
    {
        byte[] vertex = [0, 0, 0, 0, 0, 0, 0x00, 0x40, 0x00, 0, 0, 0, 0];

        (float x, float y, float z) = GxVertexReader.ReadNormal(ScnVertexFormats.FixedTexCoord, vertex);

        Assert.Equal(0f, x);
        Assert.Equal(1f, y);
        Assert.Equal(0f, z);
    }

    [Fact]
    public void AFloatTextureCoordinateIsNotScaledByAnyFraction()
    {
        byte[] vertex = [0, 0, 0, 0, 0, 0, 0, 0x40, 0, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        (float u, float v) = GxVertexReader.ReadTexCoord(ScnVertexFormats.FloatTexCoord, vertex, 0);

        Assert.Equal(1f, u);
        Assert.Equal(0f, v);
    }

    [Fact]
    public void AFixedPointTextureCoordinateIs()
    {
        // 0x0100 with an 8-bit fraction is 1.0, 0x0080 is 0.5.
        byte[] vertex = [0, 0, 0, 0, 0, 0, 0, 0x40, 0, 0x01, 0x00, 0x00, 0x80];

        (float u, float v) = GxVertexReader.ReadTexCoord(ScnVertexFormats.FixedTexCoord, vertex, 0);

        Assert.Equal(1f, u);
        Assert.Equal(0.5f, v);
    }
}

public class GxDisplayListTests
{
    [Fact]
    public void PrimitiveOpcodesSplitIntoAShapeAndAnAttributeTableIndex()
    {
        // 0x99 is a triangle strip through attribute table 1.
        byte[] list = Strip(vat: 1, vertexSize: 4, vertexCount: 3);

        GxDisplayList parsed = Assert.IsType<GxDisplayList>(GxDisplayList.Parse(list, _ => 4));

        GxPrimitiveCommand command = Assert.Single(parsed.Primitives);
        Assert.Equal(GxPrimitive.TriangleStrip, command.Primitive);
        Assert.Equal(1, command.Vat);
        Assert.Equal(3, command.VertexCount);
        Assert.Equal(3, command.VertexOffset);
        Assert.Equal(1, command.TriangleCount);
    }

    [Fact]
    public void TrailingNopPaddingEndsTheListCleanly()
    {
        byte[] list = [.. Strip(vat: 0, vertexSize: 2, vertexCount: 3), 0, 0, 0, 0];

        GxDisplayList parsed = Assert.IsType<GxDisplayList>(GxDisplayList.Parse(list, _ => 2));

        Assert.Equal(9, parsed.Consumed);
        Assert.Equal(3, parsed.VertexCount);
    }

    [Fact]
    public void NonZeroBytesAfterTheEndMeanTheWalkWentWrong()
    {
        byte[] list = [.. Strip(vat: 0, vertexSize: 2, vertexCount: 3), 0, 0, 0xAB, 0];

        Assert.Null(GxDisplayList.Parse(list, _ => 2));
    }

    [Fact]
    public void AWrongVertexSizeIsRejectedRatherThanSilentlyMisread()
    {
        byte[] list = Strip(vat: 0, vertexSize: 4, vertexCount: 3);

        Assert.Null(GxDisplayList.Parse(list, _ => 8));   // would run past the end
        Assert.NotNull(GxDisplayList.Parse(list, _ => 4));
    }

    [Fact]
    public void RegisterLoadsAreSteppedOverAndRecorded()
    {
        // A command-processor write, then a transform-unit write of two values, then a draw.
        byte[] list =
        [
            0x08, 0x50, 0x00, 0x00, 0x02, 0x00,                     // CP register 0x50 = 0x200
            0x10, 0x00, 0x01, 0x10, 0x00, 1, 2, 3, 4, 5, 6, 7, 8,   // XF: two 32-bit values
            0x48,                                                    // invalidate vertex cache
            .. Strip(vat: 0, vertexSize: 2, vertexCount: 3),
        ];

        GxDisplayList parsed = Assert.IsType<GxDisplayList>(GxDisplayList.Parse(list, _ => 2));

        Assert.Equal((0x50, 0x200u), Assert.Single(parsed.CommandProcessorWrites));
        Assert.Single(parsed.Primitives);
    }

    [Fact]
    public void AnUnknownOpcodeStopsTheWalk()
    {
        Assert.Null(GxDisplayList.Parse([0x77, 0x00, 0x00], _ => 2));
    }

    [Theory]
    [InlineData(GxPrimitive.TriangleStrip, 5, 3)]
    [InlineData(GxPrimitive.TriangleFan, 5, 3)]
    [InlineData(GxPrimitive.Triangles, 6, 2)]
    [InlineData(GxPrimitive.Quads, 8, 4)]
    [InlineData(GxPrimitive.Quads2, 4, 2)]
    [InlineData(GxPrimitive.Lines, 4, 0)]
    [InlineData(GxPrimitive.Points, 4, 0)]
    public void TriangleCountsFollowTheShape(GxPrimitive primitive, int vertices, int expected)
    {
        var command = new GxPrimitiveCommand
        {
            Offset = 0,
            Primitive = primitive,
            Vat = 0,
            VertexCount = vertices,
            VertexOffset = 3,
        };

        Assert.Equal(expected, command.TriangleCount);
        Assert.Equal(expected, GxDisplayList.Triangulate(command).Count());
    }

    [Fact]
    public void AStripAlternatesItsWindingSoEveryTriangleFacesTheSameWay()
    {
        var command = new GxPrimitiveCommand
        {
            Offset = 0,
            Primitive = GxPrimitive.TriangleStrip,
            Vat = 0,
            VertexCount = 4,
            VertexOffset = 3,
        };

        Assert.Equal([(0, 1, 2), (2, 1, 3)], GxDisplayList.Triangulate(command).ToList());
    }

    [Fact]
    public void AFanRadiatesFromItsFirstVertex()
    {
        var command = new GxPrimitiveCommand
        {
            Offset = 0,
            Primitive = GxPrimitive.TriangleFan,
            Vat = 0,
            VertexCount = 4,
            VertexOffset = 3,
        };

        Assert.Equal([(0, 1, 2), (0, 2, 3)], GxDisplayList.Triangulate(command).ToList());
    }

    /// <summary>One triangle strip command with the requested number of blank vertices.</summary>
    internal static byte[] Strip(int vat, int vertexSize, int vertexCount)
    {
        var writer = new BigEndianWriter().U8(0x98 | vat).U16(vertexCount);
        return writer.Zeros(vertexCount * vertexSize).ToArray();
    }
}
