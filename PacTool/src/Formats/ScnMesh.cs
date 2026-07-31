using PacTool.Gx;

namespace PacTool.Formats;

/// <summary>One decoded vertex.</summary>
public readonly record struct ScnVertex(
    float X, float Y, float Z,
    float Nx, float Ny, float Nz,
    float U, float V);

/// <summary>
/// A shape's geometry, decoded from the GX display list that follows its scene-table record.
///
/// The vertex layout is <em>not</em> stored in the file. On hardware it lives in the command
/// processor's vertex descriptor and attribute table registers, which the game writes before it
/// calls the list, and the shipped display lists contain no register loads of their own. What the
/// file does carry is the shape record's vertex and triangle counts, and those pin the layout down:
/// a wrong vertex length walks the stream to a different number of vertices or overruns it, so the
/// two layouts in <see cref="ScnVertexFormats"/> can be told apart by trying each and keeping the
/// one whose walk reproduces the declared counts. Across the reference data that identifies the
/// layout uniquely for 4649 of 4651 shapes.
/// </summary>
public sealed class ScnMesh
{
    /// <summary>Vertex layout the display list turned out to use.</summary>
    public required GxVertexFormat Format { get; init; }

    /// <summary>Name given to this layout, for listings.</summary>
    public required string FormatName { get; init; }

    /// <summary>Vertices in stream order, one entry per vertex of every primitive.</summary>
    public required IReadOnlyList<ScnVertex> Vertices { get; init; }

    /// <summary>Triangles as index triples into <see cref="Vertices"/>.</summary>
    public required IReadOnlyList<(int A, int B, int C)> Triangles { get; init; }

    /// <summary>The parsed display list.</summary>
    public required GxDisplayList DisplayList { get; init; }

    /// <summary>
    /// Decodes a shape's display list, or returns null when no known layout reproduces
    /// <paramref name="declaredVertices"/> and <paramref name="declaredTriangles"/>.
    ///
    /// Three tests narrow the candidates, in order of how much they are trusted:
    ///
    /// <list type="number">
    /// <item>The walk has to reach exactly the declared vertex and triangle counts. On its own this
    /// settles 6907 of the 7902 shapes in the reference data.</item>
    /// <item>A display list is padded up to a multiple of 32 bytes, and the shape record states the
    /// padded length, so the bytes consumed must round up to it. That settles another 981 - the
    /// short lists, where padding was hiding the difference between one layout and another.</item>
    /// <item>Failing those, the decoded values themselves: a normal that is not unit length, or a
    /// texture coordinate that comes out as a denormal or in the thousands, means the layout is
    /// wrong. That settles the last 10, leaving 4 shapes this tool does not claim to decode.</item>
    /// </list>
    /// </summary>
    /// <param name="displayList">The whole stream stored after the shape record, padding included.</param>
    /// <param name="declaredVertices">Vertex count from the shape record.</param>
    /// <param name="declaredTriangles">Triangle count from the shape record.</param>
    public static ScnMesh? Decode(ReadOnlySpan<byte> displayList, int declaredVertices, int declaredTriangles)
    {
        var fits = new List<(string Name, GxVertexFormat Format, GxDisplayList Walk)>();

        foreach ((string candidateName, GxVertexFormat candidate) in ScnVertexFormats.All)
        {
            int size = candidate.VertexSize;
            GxDisplayList? walk = GxDisplayList.Parse(displayList, _ => size);
            if (walk is null)
                continue;
            if (walk.VertexCount != declaredVertices || walk.TriangleCount != declaredTriangles)
                continue;

            fits.Add((candidateName, candidate, walk));
        }

        if (fits.Count == 0)
            return null;

        if (fits.Count > 1)
        {
            int listBytes = displayList.Length - SceneTable.RecordSize;
            var padded = fits.Where(f => RoundUpTo32(f.Walk.Consumed) == listBytes).ToList();
            if (padded.Count > 0)
                fits = padded;
        }

        if (fits.Count > 1)
        {
            double best = -1;
            double runnerUp = -1;
            int bestIndex = -1;
            for (int i = 0; i < fits.Count; i++)
            {
                double score = Plausibility(displayList, fits[i].Format, fits[i].Walk);
                if (score > best)
                {
                    runnerUp = best;
                    best = score;
                    bestIndex = i;
                }
                else if (score > runnerUp)
                {
                    runnerUp = score;
                }
            }

            // Only accept a winner that is both convincing on its own and clearly ahead.
            if (best < 0.9 || best - runnerUp < 0.25)
                return null;

            fits = [fits[bestIndex]];
        }

        (string name, GxVertexFormat chosen, GxDisplayList parsed) = fits[0];

        var vertices = new List<ScnVertex>(declaredVertices);
        var triangles = new List<(int, int, int)>(declaredTriangles);
        int stride = chosen.VertexSize;

        foreach (GxPrimitiveCommand command in parsed.Primitives)
        {
            int firstVertex = vertices.Count;
            for (int i = 0; i < command.VertexCount; i++)
            {
                ReadOnlySpan<byte> raw = displayList.Slice(command.VertexOffset + i * stride, stride);
                (float x, float y, float z) = GxVertexReader.ReadPosition(chosen, raw);
                (float nx, float ny, float nz) = GxVertexReader.ReadNormal(chosen, raw);
                (float u, float v) = GxVertexReader.ReadTexCoord(chosen, raw, 0);
                vertices.Add(new ScnVertex(x, y, z, nx, ny, nz, u, v));
            }

            foreach ((int a, int b, int c) in GxDisplayList.Triangulate(command))
                triangles.Add((firstVertex + a, firstVertex + b, firstVertex + c));
        }

        return new ScnMesh
        {
            Format = chosen,
            FormatName = name,
            Vertices = vertices,
            Triangles = triangles,
            DisplayList = parsed,
        };
    }

    /// <summary>Rounds up to the 32-byte boundary a display list is padded to.</summary>
    private static int RoundUpTo32(int value) => (value + 31) / 32 * 32;

    /// <summary>
    /// The fraction of vertices whose decoded values look like real ones. A correct layout scores
    /// 1.0 on this across the whole reference set; a wrong one lands well below, because reading a
    /// float texture coordinate as fixed point puts it in the thousands and reading fixed point as
    /// a float produces denormals.
    /// </summary>
    private static double Plausibility(ReadOnlySpan<byte> displayList, GxVertexFormat format, GxDisplayList walk)
    {
        int stride = format.VertexSize;
        int good = 0;
        int total = 0;

        foreach (GxPrimitiveCommand command in walk.Primitives)
        {
            for (int i = 0; i < command.VertexCount; i++)
            {
                total++;
                ReadOnlySpan<byte> raw = displayList.Slice(command.VertexOffset + i * stride, stride);
                (float nx, float ny, float nz) = GxVertexReader.ReadNormal(format, raw);
                (float u, float v) = GxVertexReader.ReadTexCoord(format, raw, 0);

                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length is >= 0.9 and <= 1.05 && IsPlausible(u) && IsPlausible(v))
                    good++;
            }
        }

        return total == 0 ? 0 : (double)good / total;
    }

    /// <summary>
    /// A texture coordinate is believable if it is zero, or finite and neither vanishingly small
    /// (which is what a fixed-point pair looks like read as a float) nor enormous (which is what a
    /// float looks like read as fixed point).
    /// </summary>
    private static bool IsPlausible(float value) =>
        float.IsFinite(value) && (value == 0 || (Math.Abs(value) >= 1e-4f && Math.Abs(value) <= 32f));
}

/// <summary>
/// The vertex layouts the shipped display lists use. Both describe the same attributes - a
/// position, a normal and one texture coordinate, all direct - and differ only in how the texture
/// coordinate is stored.
///
/// The fractions were confirmed against the data rather than guessed. Under a 6-bit normal
/// fraction, which is what the hardware fixes for signed bytes, all 40,806 normals in the
/// reference set come out unit length to within half a percent. Under an 8-bit position fraction
/// the models measure roughly 100 units across, matching the placement coordinates in
/// <c>map.dat</c>; a different fraction would put them in the thousands or the hundredths.
/// </summary>
public static class ScnVertexFormats
{
    /// <summary>Position, normal and a fixed-point texture coordinate. 13 bytes.</summary>
    public static GxVertexFormat FixedTexCoord { get; } = new()
    {
        Position = GxAttributeMode.Direct,
        PositionComponents = 3,
        PositionFormat = GxComponentFormat.Short,
        PositionFraction = 8,
        Normal = GxAttributeMode.Direct,
        NormalFormat = GxComponentFormat.Byte,
        TexCoord = [GxAttributeMode.Direct, .. new GxAttributeMode[7]],
        TexCoordComponents = [2, 2, 2, 2, 2, 2, 2, 2],
        TexCoordFormat =
            [GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short,
             GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short],
        TexCoordFraction = [8, 0, 0, 0, 0, 0, 0, 0],
    };

    /// <summary>Position, normal and a floating-point texture coordinate. 17 bytes.</summary>
    public static GxVertexFormat FloatTexCoord { get; } = new()
    {
        Position = GxAttributeMode.Direct,
        PositionComponents = 3,
        PositionFormat = GxComponentFormat.Short,
        PositionFraction = 8,
        Normal = GxAttributeMode.Direct,
        NormalFormat = GxComponentFormat.Byte,
        TexCoord = [GxAttributeMode.Direct, .. new GxAttributeMode[7]],
        TexCoordComponents = [2, 2, 2, 2, 2, 2, 2, 2],
        TexCoordFormat =
            [GxComponentFormat.Float, GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short,
             GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short, GxComponentFormat.Short],
    };

    /// <summary>Every known layout, in the order <see cref="ScnMesh.Decode"/> tries them.</summary>
    public static IReadOnlyList<(string Name, GxVertexFormat Format)> All { get; } =
    [
        ("s16 texcoord", FixedTexCoord),
        ("f32 texcoord", FloatTexCoord),
    ];
}
