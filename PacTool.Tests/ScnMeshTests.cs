using PacTool.Extract;
using PacTool.Formats;
using PacTool.Gx;
using Xunit;

namespace PacTool.Tests;

public class ScnMeshTests
{
    [Fact]
    public void AShapeWithFixedPointTextureCoordinatesDecodes()
    {
        // A quad as a four-vertex triangle strip, one unit across, facing +Y.
        byte[] stream = Stream(FixedVertices(
            [(0, 0, 0), (1, 0, 0), (0, 0, 1), (1, 0, 1)],
            [(0, 0), (1, 0), (0, 1), (1, 1)]));

        ScnMesh mesh = Assert.IsType<ScnMesh>(ScnMesh.DecodeDisplayList(stream, declaredVertices: 4, declaredTriangles: 2));

        Assert.Contains("s16 texcoord", mesh.Description);
        Assert.Equal(13, mesh.Format!.VertexSize);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(2, mesh.Triangles.Count);

        Assert.Equal(new ScnVertex(0, 0, 0, 0, 1, 0, 0, 0), mesh.Vertices[0]);
        Assert.Equal(new ScnVertex(1, 0, 0, 0, 1, 0, 1, 0), mesh.Vertices[1]);
        Assert.Equal([(0, 1, 2), (2, 1, 3)], mesh.Triangles);
    }

    [Fact]
    public void AShapeWithFloatingPointTextureCoordinatesDecodes()
    {
        byte[] stream = Stream(FloatVertices(
            [(37, 14, -48), (35, 14, -48), (37, 14, -32), (35, 14, -32)],
            [(1f, 0f), (0f, 0f), (1f, 1f), (0f, 1f)]));

        ScnMesh mesh = Assert.IsType<ScnMesh>(ScnMesh.DecodeDisplayList(stream, declaredVertices: 4, declaredTriangles: 2));

        Assert.Contains("f32 texcoord", mesh.Description);
        Assert.Equal(17, mesh.Format!.VertexSize);
        Assert.Equal(new ScnVertex(37, 14, -48, 0, 1, 0, 1, 0), mesh.Vertices[0]);
        Assert.Equal(new ScnVertex(35, 14, -32, 0, 1, 0, 0, 1), mesh.Vertices[3]);
    }

    [Fact]
    public void AShapeWhoseCountsDoNotMatchIsRefusedRatherThanGuessedAt()
    {
        byte[] stream = Stream(FixedVertices(
            [(0, 0, 0), (1, 0, 0), (0, 0, 1), (1, 0, 1)],
            [(0, 0), (1, 0), (0, 1), (1, 1)]));

        Assert.Null(ScnMesh.DecodeDisplayList(stream, declaredVertices: 6, declaredTriangles: 2));
        Assert.Null(ScnMesh.DecodeDisplayList(stream, declaredVertices: 4, declaredTriangles: 4));
    }

    [Fact]
    public void ThePaddedLengthSeparatesLayoutsThatBothWalkCleanly()
    {
        // A four-vertex strip is 55 bytes as 13-byte vertices and 71 as 17-byte ones. Both fit
        // inside a 96-byte list, so only the length the record states tells them apart: 55 rounds
        // up to 64 and 71 to 96.
        byte[] vertices = FloatVertices(
            [(1, 2, 3), (4, 5, 6), (7, 8, 9), (10, 11, 12)],
            [(0.25f, 0.5f), (0.25f, 0.5f), (0.25f, 0.5f), (0.25f, 0.5f)]);

        byte[] padTo96 = Stream(vertices, listBytes: 96);
        ScnMesh wide = Assert.IsType<ScnMesh>(ScnMesh.DecodeDisplayList(padTo96, 4, 2));
        Assert.Equal(17, wide.Format!.VertexSize);

        // The same bytes in a list the record says is 64 long can only be the shorter layout.
        byte[] shorter = Stream(FixedVertices(
            [(1, 2, 3), (4, 5, 6), (7, 8, 9), (10, 11, 12)],
            [(0.25f, 0.5f), (0.25f, 0.5f), (0.25f, 0.5f), (0.25f, 0.5f)]), listBytes: 64);
        ScnMesh narrow = Assert.IsType<ScnMesh>(ScnMesh.DecodeDisplayList(shorter, 4, 2));
        Assert.Equal(13, narrow.Format!.VertexSize);
    }

    [Fact]
    public void SeveralPrimitivesAreConcatenatedWithTheirIndicesRebased()
    {
        byte[] first = FixedVertices([(0, 0, 0), (1, 0, 0), (0, 0, 1)], [(0, 0), (1, 0), (0, 1)]);
        byte[] second = FixedVertices([(5, 0, 0), (6, 0, 0), (5, 0, 1)], [(0, 0), (1, 0), (0, 1)]);
        byte[] stream = Stream([.. Primitive(first, 3), .. Primitive(second, 3)], listBytes: 96, wrapped: true);

        ScnMesh mesh = Assert.IsType<ScnMesh>(ScnMesh.DecodeDisplayList(stream, declaredVertices: 6, declaredTriangles: 2));

        Assert.Equal(6, mesh.Vertices.Count);
        Assert.Equal([(0, 1, 2), (3, 4, 5)], mesh.Triangles);
        Assert.Equal(5f, mesh.Vertices[3].X);
    }

    /// <summary>Encodes vertices in the 13-byte layout: short position, byte normal, short UV.</summary>
    private static byte[] FixedVertices((float X, float Y, float Z)[] positions, (float U, float V)[] uvs)
    {
        var writer = new BigEndianWriter();
        for (int i = 0; i < positions.Length; i++)
        {
            writer.U16((int)(short)Math.Round(positions[i].X * 256))
                  .U16((int)(short)Math.Round(positions[i].Y * 256))
                  .U16((int)(short)Math.Round(positions[i].Z * 256))
                  .U8(0).U8(64).U8(0)
                  .U16((int)(short)Math.Round(uvs[i].U * 256))
                  .U16((int)(short)Math.Round(uvs[i].V * 256));
        }

        return writer.ToArray();
    }

    /// <summary>Encodes vertices in the 17-byte layout: short position, byte normal, float UV.</summary>
    private static byte[] FloatVertices((float X, float Y, float Z)[] positions, (float U, float V)[] uvs)
    {
        var writer = new BigEndianWriter();
        for (int i = 0; i < positions.Length; i++)
        {
            writer.U16((int)(short)Math.Round(positions[i].X * 256))
                  .U16((int)(short)Math.Round(positions[i].Y * 256))
                  .U16((int)(short)Math.Round(positions[i].Z * 256))
                  .U8(0).U8(64).U8(0)
                  .F32(uvs[i].U).F32(uvs[i].V);
        }

        return writer.ToArray();
    }

    /// <summary>Wraps vertex bytes in a triangle strip command.</summary>
    private static byte[] Primitive(byte[] vertices, int count) =>
        new BigEndianWriter().U8(0x98).U16(count).Bytes(vertices).ToArray();

    /// <summary>
    /// Builds the stream a shape record is followed by: the display list padded up to
    /// <paramref name="listBytes"/>, then the extra 32 bytes the record's length field counts.
    /// </summary>
    private static byte[] Stream(byte[] payload, int listBytes = 0, bool wrapped = false)
    {
        byte[] list = wrapped ? payload : Primitive(payload, payload.Length / (payload.Length >= 17 * 4 ? 17 : 13));
        if (listBytes == 0)
            listBytes = (list.Length + 31) / 32 * 32;

        return new BigEndianWriter()
            .Bytes(list)
            .Zeros(listBytes - list.Length)
            .Zeros(SceneTable.RecordSize)
            .ToArray();
    }
}

public class ObjWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pactool-obj-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AMeshRoundTripsThroughObjWithOneBasedIndicesAndAFlippedTextureOrigin()
    {
        var mesh = new ScnMesh
        {
            Format = ScnVertexFormats.FixedTexCoord,
            Description = "GX display list, s16 texcoord, 13 B/vertex",
            PrimitiveCount = 1,
            Vertices =
            [
                new ScnVertex(1, 2, 3, 0, 1, 0, 0.25f, 0.75f),
                new ScnVertex(4, 5, 6, 0, 1, 0, 0.5f, 0.5f),
                new ScnVertex(7, 8, 9, 0, 1, 0, 1, 0),
            ],
            Triangles = [(0, 1, 2)],
            DisplayList = GxDisplayList.Parse(GxDisplayListTests.Strip(0, 13, 3), _ => 13)!,
        };

        string path = Path.Combine(_directory, "mesh.obj");
        ObjWriter.Save(mesh, "test shape", path);
        string[] lines = File.ReadAllLines(path);

        Assert.Contains("o test_shape", lines);
        Assert.Contains("v 1 2 3", lines);
        Assert.Contains("vn 0 1 0", lines);
        Assert.Contains("vt 0.25 0.25", lines);       // OBJ counts V from the bottom, GX from the top
        Assert.Contains("f 1/1/1 2/2/2 3/3/3", lines);
    }

    [Fact]
    public void SeveralMeshesInOneFileGetTheirIndicesOffset()
    {
        var mesh = new ScnMesh
        {
            Format = ScnVertexFormats.FixedTexCoord,
            Description = "GX display list, s16 texcoord, 13 B/vertex",
            PrimitiveCount = 1,
            Vertices = [new ScnVertex(0, 0, 0, 0, 1, 0, 0, 0), new ScnVertex(1, 0, 0, 0, 1, 0, 0, 0), new ScnVertex(0, 1, 0, 0, 1, 0, 0, 0)],
            Triangles = [(0, 1, 2)],
            DisplayList = GxDisplayList.Parse(GxDisplayListTests.Strip(0, 13, 3), _ => 13)!,
        };

        string path = Path.Combine(_directory, "all.obj");
        ObjWriter.SaveAll([("first", mesh), ("second", mesh)], path);
        string[] lines = File.ReadAllLines(path);

        Assert.Contains("f 1/1/1 2/2/2 3/3/3", lines);
        Assert.Contains("f 4/4/4 5/5/5 6/6/6", lines);
    }
}
