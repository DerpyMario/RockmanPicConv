using PacTool.Extract;
using PacTool.Formats;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The encoding every character and enemy model uses: plain triangle strips of 20-byte skinned
/// vertices, with no GX opcodes anywhere.
/// </summary>
public class SkinnedMeshTests
{
    [Fact]
    public void AStripOfSkinnedVerticesDecodes()
    {
        byte[] body = Body([
            [Vertex((1, 2, 3), (0, 1, 0), (0.5f, 0.25f), (7, -1, -1), (128, 0, 0)),
             Vertex((4, 5, 6), (1, 0, 0), (1, 0), (7, 9, -1), (64, 64, 0)),
             Vertex((7, 8, 9), (0, 0, -1), (0, 1), (9, -1, -1), (128, 0, 0)),
             Vertex((10, 11, 12), (0, -1, 0), (0.25f, 0.75f), (7, 9, 11), (25, 102, 1))],
        ]);

        ScnMesh mesh = Assert.IsType<ScnMesh>(
            ScnMesh.DecodeSkinned(body, declaredVertices: 4, declaredTriangles: 2, declaredStrips: 1));

        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(1, mesh.PrimitiveCount);
        Assert.Contains("skinned strips", mesh.Description);

        Assert.Equal(new ScnVertex(1, 2, 3, 0, 1, 0, 0.5f, 0.25f), mesh.Vertices[0]);
        Assert.Equal(new ScnVertex(4, 5, 6, 1, 0, 0, 1, 0), mesh.Vertices[1]);

        // Strips alternate their winding, the same as a GX triangle strip.
        Assert.Equal([(0, 1, 2), (2, 1, 3)], mesh.Triangles);
    }

    [Fact]
    public void JointIndicesAndWeightsComeOutOfTheLastSevenBytes()
    {
        byte[] body = Body([
            [Vertex((0, 0, 0), (0, 1, 0), (0, 0), (7, -1, -1), (128, 0, 0)),
             Vertex((1, 0, 0), (0, 1, 0), (0, 0), (7, 9, -1), (64, 64, 0)),
             Vertex((0, 0, 1), (0, 1, 0), (0, 0), (7, 9, 11), (25, 102, 1))],
        ]);

        ScnMesh mesh = Assert.IsType<ScnMesh>(ScnMesh.DecodeSkinned(body, 3, 1, 1));
        IReadOnlyList<ScnSkinBinding> skin = Assert.IsAssignableFrom<IReadOnlyList<ScnSkinBinding>>(mesh.Skin);

        // 0xFF marks an unused slot, and the weights are in 1/128 units.
        Assert.Equal(new ScnSkinBinding(7, -1, -1, 1f, 0f, 0f), skin[0]);
        Assert.Equal(new ScnSkinBinding(7, 9, -1, 0.5f, 0.5f, 0f), skin[1]);
        Assert.Equal([(7, 1f)], skin[0].Influences);
        Assert.Equal([(7, 0.5f), (9, 0.5f)], skin[1].Influences);

        Assert.Equal([7, 9, 11], mesh.Joints);
    }

    [Fact]
    public void SeveralStripsAreConcatenatedWithTheirIndicesRebased()
    {
        byte[] body = Body([
            [Vertex((0, 0, 0)), Vertex((1, 0, 0)), Vertex((0, 0, 1))],
            [Vertex((5, 0, 0)), Vertex((6, 0, 0)), Vertex((5, 0, 1)), Vertex((6, 0, 1))],
        ]);

        ScnMesh mesh = Assert.IsType<ScnMesh>(ScnMesh.DecodeSkinned(body, 7, 3, 2));

        Assert.Equal(7, mesh.Vertices.Count);
        Assert.Equal([(0, 1, 2), (3, 4, 5), (5, 4, 6)], mesh.Triangles);
        Assert.Equal(5f, mesh.Vertices[3].X);
    }

    [Fact]
    public void CountsThatDoNotDescribeStripsAreRefused()
    {
        byte[] body = Body([[Vertex((0, 0, 0)), Vertex((1, 0, 0)), Vertex((0, 0, 1))]]);

        // A strip of n vertices makes n - 2 triangles, so these are impossible.
        Assert.Null(ScnMesh.DecodeSkinned(body, 3, 2, 1));
        Assert.Null(ScnMesh.DecodeSkinned(body, 4, 2, 1));
    }

    [Fact]
    public void ABodyThatDoesNotEndOnTheLastVertexIsRefused()
    {
        byte[] body = Body([[Vertex((0, 0, 0)), Vertex((1, 0, 0)), Vertex((0, 0, 1))]]);

        Assert.Null(ScnMesh.DecodeSkinned(body.AsSpan(0, body.Length - 4), 3, 1, 1));
        Assert.Null(ScnMesh.DecodeSkinned([.. body, 0, 0, 0, 0], 3, 1, 1));
    }

    [Fact]
    public void TheBodySizeIsAVertexCountPerStripPlusTheVertices()
    {
        Assert.Equal(20 * 1512 + 2 * 229, ScnMesh.SkinnedBodySize(1512, 229));   // m01xxxxx.scn
        Assert.Equal(20 * 318 + 2 * 43, ScnMesh.SkinnedBodySize(318, 43));       // d2dxxxxx.scn
    }

    [Fact]
    public void TheSceneTableFindsSkinnedShapesAndRealignsAfterEachBody()
    {
        // A skinned body ends wherever its last vertex does, so the record after it sits at the
        // next 32-byte boundary rather than immediately afterwards.
        byte[] first = Body([[Vertex((1, 0, 0)), Vertex((2, 0, 0)), Vertex((3, 0, 0))]]);
        var table = new BigEndianWriter()
            .U16(2).Zeros(30)
            .Bytes(SkinnedRecord("bodyA", "M0", vertices: 3, triangles: 1, strips: 1))
            .Bytes(first);
        table.PadTo((table.Length + 31) / 32 * 32);
        table.Bytes(SkinnedRecord("bodyB", "M1", vertices: 3, triangles: 1, strips: 1)).Bytes(first);
        table.PadTo((table.Length + 31) / 32 * 32);
        table.U32(0).Field("after", 8).Zeros(20);

        SceneTable scene = SceneTable.Parse(table.ToArray(), 0);
        List<SceneRecord> records = scene.Records.ToList();

        Assert.Equal(SceneRecordKind.SectionHeader, records[0].Kind);
        Assert.Equal(SceneRecordKind.SkinnedShape, records[1].Kind);
        Assert.Equal("bodyA", records[1].Name);
        Assert.Equal(3, records[1].VertexCount);
        Assert.Equal(1, records[1].PrimitiveCount);
        Assert.Equal(SceneRecordKind.SkinnedShape, records[2].Kind);
        Assert.Equal("bodyB", records[2].Name);
        Assert.Equal(SceneRecordKind.Entry, records[3].Kind);
        Assert.Equal("after", records[3].Name);
    }

    [Fact]
    public void SkinWeightsAreWrittenAsTheirOwnTableBecauseObjHasNowhereForThem()
    {
        byte[] body = Body([
            [Vertex((0, 0, 0), (0, 1, 0), (0, 0), (7, -1, -1), (128, 0, 0)),
             Vertex((1, 0, 0), (0, 1, 0), (0, 0), (7, 9, -1), (64, 64, 0)),
             Vertex((0, 0, 1), (0, 1, 0), (0, 0), (9, -1, -1), (128, 0, 0))],
        ]);
        ScnMesh mesh = Assert.IsType<ScnMesh>(ScnMesh.DecodeSkinned(body, 3, 1, 1));

        string[] lines = ObjWriter.DescribeSkin(mesh).Split('\n');

        Assert.Equal("vertex,joint0,weight0,joint1,weight1,joint2,weight2", lines[1]);
        Assert.Equal("0,7,1,,0,,0", lines[2].TrimEnd('\r'));
        Assert.Equal("1,7,0.5,9,0.5,,0", lines[3].TrimEnd('\r'));
    }

    /// <summary>A 20-byte skinned vertex.</summary>
    internal static byte[] Vertex((float X, float Y, float Z) position,
                                  (float X, float Y, float Z) normal = default,
                                  (float U, float V) uv = default,
                                  (int A, int B, int C) joints = default,
                                  (int A, int B, int C) weights = default)
    {
        if (joints == default)
            joints = (0, -1, -1);
        if (weights == default)
            weights = (128, 0, 0);

        return new BigEndianWriter()
            .U16(Fixed(uv.U)).U16(Fixed(uv.V))
            .U16(Fixed(position.X)).U16(Fixed(position.Y)).U16(Fixed(position.Z))
            .U8((int)Math.Round(normal.X * 64) & 0xFF)
            .U8((int)Math.Round(normal.Y * 64) & 0xFF)
            .U8((int)Math.Round(normal.Z * 64) & 0xFF)
            .U8(joints.A < 0 ? 0xFF : joints.A)
            .U8(joints.B < 0 ? 0xFF : joints.B)
            .U8(joints.C < 0 ? 0xFF : joints.C)
            .U8(weights.A).U8(weights.B).U8(weights.C)
            .U8(0)
            .ToArray();
    }

    private static int Fixed(float value) => (short)Math.Round(value * 256) & 0xFFFF;

    /// <summary>Strips, each a vertex count followed by its vertices.</summary>
    internal static byte[] Body(IReadOnlyList<byte[][]> strips)
    {
        var writer = new BigEndianWriter();
        foreach (byte[][] strip in strips)
        {
            writer.U16(strip.Length);
            foreach (byte[] vertex in strip)
                writer.Bytes(vertex);
        }

        return writer.ToArray();
    }

    /// <summary>A character model's shape record, whose fields sit at different offsets from a display list's.</summary>
    internal static byte[] SkinnedRecord(string name, string tag, int vertices, int triangles, int strips) =>
        new BigEndianWriter()
            .U32(1)                                   // the word that marks the skinned encoding
            .Field(name, 8).Field(tag, 4)
            .U32(0)
            .U16(vertices).U16(triangles).U16(strips)
            .U16(0).U16(0)
            .U16((int)(ScnMesh.SkinnedBodySize(vertices, strips) & 0xFFFF))
            .ToArray();
}

/// <summary>
/// The record length fields are 16-bit and the game does not clamp them, so a big enough shape
/// stores its length modulo 65,536.
/// </summary>
public class SixteenBitWrapTests
{
    [Fact]
    public void ADisplayListLongerThanSixtyFourKilobytesIsFoundAnyway()
    {
        // 1200 strips of four 13-byte vertices is 66,000 bytes, which wraps past 16 bits.
        byte[] list = LongList(strips: 1200, verticesPerStrip: 4);
        int padded = (list.Length + 31) / 32 * 32;
        Assert.True(padded > 0x10000, "the fixture has to be long enough to wrap");

        var table = new BigEndianWriter()
            .Bytes(ShapeRecord("big", "M0", padded, vertices: 4800, triangles: 2400))
            .Bytes(list).PadTo(SceneTable.RecordSize + padded).Zeros(SceneTable.RecordSize)
            .U32(0).Field("after", 8).Zeros(20);

        SceneTable scene = SceneTable.Parse(table.ToArray(), 0);
        List<SceneRecord> records = scene.Records.ToList();

        Assert.Equal(SceneRecordKind.Shape, records[0].Kind);
        Assert.Equal(padded + SceneTable.RecordSize, records[0].Geometry!.Length);
        Assert.Equal("after", records[1].Name);

        ScnMesh mesh = Assert.IsType<ScnMesh>(
            ScnMesh.DecodeDisplayList(records[0].Geometry, 4800, 2400));
        Assert.Equal(4800, mesh.Vertices.Count);
        Assert.Equal(2400, mesh.Triangles.Count);
    }

    [Fact]
    public void ASkinnedBodyLongerThanSixtyFourKilobytesIsFoundAnyway()
    {
        // 3300 vertices at 20 bytes is 66,000 bytes plus the strip headers, so this wraps too.
        var strips = new List<byte[][]>();
        for (int i = 0; i < 1100; i++)
            strips.Add([SkinnedMeshTests.Vertex((i, 0, 0)), SkinnedMeshTests.Vertex((i, 1, 0)), SkinnedMeshTests.Vertex((i, 0, 1))]);

        byte[] body = SkinnedMeshTests.Body(strips);
        Assert.True(body.Length > 0x10000, "the fixture has to be long enough to wrap");

        var table = new BigEndianWriter()
            .Bytes(SkinnedMeshTests.SkinnedRecord("big", "M0", vertices: 3300, triangles: 1100, strips: 1100))
            .Bytes(body);
        table.PadTo((table.Length + 31) / 32 * 32);
        table.U32(0).Field("after", 8).Zeros(20);

        SceneTable scene = SceneTable.Parse(table.ToArray(), 0);
        List<SceneRecord> records = scene.Records.ToList();

        Assert.Equal(SceneRecordKind.SkinnedShape, records[0].Kind);
        Assert.Equal(body.Length, records[0].Geometry!.Length);
        Assert.Equal("after", records[1].Name);
    }

    /// <summary>A display list of triangle strips with 13-byte vertices.</summary>
    private static byte[] LongList(int strips, int verticesPerStrip)
    {
        var writer = new BigEndianWriter();
        for (int i = 0; i < strips; i++)
        {
            writer.U8(0x98).U16(verticesPerStrip);
            for (int v = 0; v < verticesPerStrip; v++)
            {
                writer.U16(i).U16(v).U16(0)     // position
                      .U8(0).U8(64).U8(0)       // normal, unit +Y
                      .U16(0).U16(0);           // texture coordinate
            }
        }

        return writer.ToArray();
    }

    private static byte[] ShapeRecord(string name, string tag, int listBytes, int vertices, int triangles) =>
        new BigEndianWriter()
            .U32(0)
            .Field(name, 8).Field(tag, 4)
            .Zeros(6)
            .U16((listBytes + SceneTable.RecordSize) & 0xFFFF)
            .U16(0)
            .U16(listBytes & 0xFFFF)
            .U16(vertices).U16(triangles)
            .ToArray();
}
