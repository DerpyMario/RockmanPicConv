using PacTool.Gx;

namespace PacTool.Formats;

/// <summary>One decoded vertex.</summary>
public readonly record struct ScnVertex(
    float X, float Y, float Z,
    float Nx, float Ny, float Nz,
    float U, float V);

/// <summary>
/// How one vertex of a character model is bound to the skeleton. Up to three joints, with weights
/// that sum to 1.
/// </summary>
public readonly record struct ScnSkinBinding(
    int Joint0, int Joint1, int Joint2,
    float Weight0, float Weight1, float Weight2)
{
    /// <summary>The bindings that are actually used, as (joint, weight) pairs.</summary>
    public IEnumerable<(int Joint, float Weight)> Influences
    {
        get
        {
            if (Joint0 >= 0) yield return (Joint0, Weight0);
            if (Joint1 >= 0) yield return (Joint1, Weight1);
            if (Joint2 >= 0) yield return (Joint2, Weight2);
        }
    }
}

/// <summary>
/// A shape's geometry. Two encodings occur, and a <c>.scn</c> may hold either:
///
/// <list type="bullet">
/// <item><b>A GX display list</b> — stage scenery, effects and props. See
/// <see cref="DecodeDisplayList"/>.</item>
/// <item><b>A skinned strip list</b> — every character and enemy model. Not a display list at all;
/// see <see cref="DecodeSkinned"/>.</item>
/// </list>
/// </summary>
public sealed class ScnMesh
{
    /// <summary>Vertices in stored order, one entry per drawn vertex.</summary>
    public required IReadOnlyList<ScnVertex> Vertices { get; init; }

    /// <summary>Triangles as index triples into <see cref="Vertices"/>.</summary>
    public required IReadOnlyList<(int A, int B, int C)> Triangles { get; init; }

    /// <summary>How the geometry was stored, for listings.</summary>
    public required string Description { get; init; }

    /// <summary>Skeleton bindings, one per vertex, on a character model. Null on scenery.</summary>
    public IReadOnlyList<ScnSkinBinding>? Skin { get; init; }

    /// <summary>Strips, quads or whatever the source used, for listings.</summary>
    public required int PrimitiveCount { get; init; }

    /// <summary>Vertex layout, when the source was a display list.</summary>
    public GxVertexFormat? Format { get; init; }

    /// <summary>The parsed display list, when the source was one.</summary>
    public GxDisplayList? DisplayList { get; init; }

    /// <summary>Joints this shape's vertices reference, ascending. Empty when it is not skinned.</summary>
    public IReadOnlyList<int> Joints => Skin is null
        ? []
        : Skin.SelectMany(s => s.Influences).Select(i => i.Joint).Distinct().Order().ToList();

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Display list geometry
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a shape stored as a GX display list, or returns null when no known vertex layout
    /// reproduces the declared counts.
    ///
    /// The layout is not in the file — on hardware it lives in the command processor's vertex
    /// descriptor and attribute table registers, which the game writes before calling the list, and
    /// the shipped lists carry no register loads of their own. Three tests narrow the candidates,
    /// in order of how much they are trusted:
    ///
    /// <list type="number">
    /// <item>The walk has to reach exactly the declared vertex and triangle counts.</item>
    /// <item>A display list is padded up to a multiple of 32 bytes and the record states the padded
    /// length, so the bytes consumed must round up to it.</item>
    /// <item>Failing those, the decoded values themselves: a normal that is not unit length, or a
    /// texture coordinate that comes out as a denormal or in the thousands, means the layout is
    /// wrong.</item>
    /// </list>
    /// </summary>
    /// <param name="stream">The whole stream stored after the shape record, padding included.</param>
    /// <param name="declaredVertices">Vertex count from the shape record.</param>
    /// <param name="declaredTriangles">Triangle count from the shape record.</param>
    public static ScnMesh? DecodeDisplayList(ReadOnlySpan<byte> stream, int declaredVertices, int declaredTriangles)
    {
        var fits = new List<(string Name, GxVertexFormat Format, GxDisplayList Walk)>();
        int listBytes = stream.Length - SceneTable.RecordSize;

        foreach ((string candidateName, GxVertexFormat candidate) in ScnVertexFormats.All)
        {
            int size = candidate.VertexSize;
            GxDisplayList? walk = GxDisplayList.Parse(stream, _ => size);
            if (walk is null || walk.VertexCount != declaredVertices || walk.TriangleCount != declaredTriangles)
                continue;

            fits.Add((candidateName, candidate, walk));
        }

        if (fits.Count == 0)
            return null;

        if (fits.Count > 1)
        {
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
                double score = Plausibility(stream, fits[i].Format, fits[i].Walk);
                if (score > best)
                {
                    (runnerUp, best, bestIndex) = (best, score, i);
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
                ReadOnlySpan<byte> raw = stream.Slice(command.VertexOffset + i * stride, stride);
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
            Vertices = vertices,
            Triangles = triangles,
            Description = $"GX display list, {name}, {stride} B/vertex",
            PrimitiveCount = parsed.Primitives.Count,
            Format = chosen,
            DisplayList = parsed,
        };
    }

    /// <summary>
    /// Works out how long a display list really is.
    ///
    /// The record's length fields are 16-bit and the game does not clamp them, so a shape with more
    /// than 65,536 bytes of geometry stores its length modulo 65,536 — <c>b0cdfblk.scn</c>'s
    /// <c>atgallM3</c> is 111,328 bytes and states 45,792. Candidate lengths are therefore the
    /// stated one plus any whole number of 64 KiB, and the right one is whichever walks to the
    /// declared counts and ends on the padding boundary it claims.
    /// </summary>
    /// <returns>The stream length including the trailing 32 bytes, or 0 when none fits.</returns>
    public static int ResolveStreamLength(ReadOnlySpan<byte> data, int at, int statedListBytes,
                                          int declaredVertices, int declaredTriangles)
    {
        for (int wrap = 0; ; wrap++)
        {
            long listBytes = statedListBytes + (long)wrap * 0x10000;
            long streamBytes = listBytes + SceneTable.RecordSize;
            if (listBytes <= 0 || at + streamBytes > data.Length)
                return 0;

            ReadOnlySpan<byte> stream = data.Slice(at, (int)streamBytes);
            foreach ((string _, GxVertexFormat candidate) in ScnVertexFormats.All)
            {
                int size = candidate.VertexSize;
                GxDisplayList? walk = GxDisplayList.Parse(stream, _ => size);
                if (walk is null || walk.VertexCount != declaredVertices || walk.TriangleCount != declaredTriangles)
                    continue;
                if (RoundUpTo32(walk.Consumed) == listBytes)
                    return (int)streamBytes;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Skinned strip geometry
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bytes one vertex of a character model occupies.</summary>
    public const int SkinnedVertexSize = 20;

    /// <summary>
    /// Decodes a shape stored as a skinned strip list — the form every character and enemy model
    /// uses. It is not a GX display list and contains no opcodes; the geometry is a plain run of
    /// triangle strips, each a vertex count followed by that many 20-byte vertices:
    ///
    /// <code>
    ///   0x00  s16     u          texture coordinate, 8-bit fraction
    ///   0x02  s16     v
    ///   0x04  s16     x          position, 8-bit fraction
    ///   0x06  s16     y
    ///   0x08  s16     z
    ///   0x0A  s8[3]   normal     6-bit fraction, as the hardware fixes for signed bytes
    ///   0x0D  u8[3]   joints     indices into the skeleton of the .mpc with the same stem,
    ///                            0xFF where unused
    ///   0x10  u8[3]   weights    in 1/128 units; the three sum to 128
    ///   0x13  u8      padding    zero throughout the reference data
    /// </code>
    ///
    /// Confirmed across 634 shapes and 522,224 vertices: every normal comes out unit length, every
    /// weight triple sums to 128 (or 127 or 126, where rounding lost a unit), and the byte at 0x13
    /// is always zero.
    /// </summary>
    public static ScnMesh? DecodeSkinned(ReadOnlySpan<byte> body, int declaredVertices,
                                         int declaredTriangles, int declaredStrips)
    {
        if (declaredVertices - 2 * declaredStrips != declaredTriangles)
            return null;

        var vertices = new List<ScnVertex>(declaredVertices);
        var skin = new List<ScnSkinBinding>(declaredVertices);
        var triangles = new List<(int, int, int)>(declaredTriangles);
        int at = 0;

        for (int strip = 0; strip < declaredStrips; strip++)
        {
            if (at + 2 > body.Length)
                return null;

            int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(body[at..]);
            at += 2;
            if (count < 3 || at + count * SkinnedVertexSize > body.Length)
                return null;

            int firstVertex = vertices.Count;
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> raw = body.Slice(at + i * SkinnedVertexSize, SkinnedVertexSize);
                vertices.Add(ReadSkinnedVertex(raw));
                skin.Add(ReadSkinBinding(raw));
            }

            at += count * SkinnedVertexSize;

            // Every strip alternates its winding, exactly as a GX triangle strip does.
            for (int i = 0; i + 2 < count; i++)
            {
                triangles.Add((i & 1) == 0
                    ? (firstVertex + i, firstVertex + i + 1, firstVertex + i + 2)
                    : (firstVertex + i + 1, firstVertex + i, firstVertex + i + 2));
            }
        }

        if (at != body.Length || vertices.Count != declaredVertices || triangles.Count != declaredTriangles)
            return null;

        return new ScnMesh
        {
            Vertices = vertices,
            Triangles = triangles,
            Skin = skin,
            Description = $"skinned strips, {SkinnedVertexSize} B/vertex",
            PrimitiveCount = declaredStrips,
        };
    }

    /// <summary>Bytes a skinned body occupies: a vertex count per strip, then the vertices.</summary>
    public static long SkinnedBodySize(int vertices, int strips) =>
        (long)vertices * SkinnedVertexSize + (long)strips * 2;

    private static ScnVertex ReadSkinnedVertex(ReadOnlySpan<byte> raw)
    {
        const float coordinate = 1f / 256f;    // 8-bit fraction
        const float normal = 1f / 64f;         // 6-bit fraction, fixed by the hardware for s8
        return new ScnVertex(
            ReadInt16(raw, 0x04) * coordinate, ReadInt16(raw, 0x06) * coordinate, ReadInt16(raw, 0x08) * coordinate,
            (sbyte)raw[0x0A] * normal, (sbyte)raw[0x0B] * normal, (sbyte)raw[0x0C] * normal,
            ReadInt16(raw, 0x00) * coordinate, ReadInt16(raw, 0x02) * coordinate);
    }

    private static ScnSkinBinding ReadSkinBinding(ReadOnlySpan<byte> raw)
    {
        const float weight = 1f / 128f;
        return new ScnSkinBinding(
            raw[0x0D] == 0xFF ? -1 : raw[0x0D],
            raw[0x0E] == 0xFF ? -1 : raw[0x0E],
            raw[0x0F] == 0xFF ? -1 : raw[0x0F],
            raw[0x10] * weight, raw[0x11] * weight, raw[0x12] * weight);
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int at) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data[at..]);

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

    /// <summary>Every known layout, in the order <see cref="ScnMesh.DecodeDisplayList"/> tries them.</summary>
    public static IReadOnlyList<(string Name, GxVertexFormat Format)> All { get; } =
    [
        ("s16 texcoord", FixedTexCoord),
        ("f32 texcoord", FloatTexCoord),
    ];
}
