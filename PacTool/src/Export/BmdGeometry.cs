using PacTool.Formats;

namespace PacTool.Export;

/// <summary>A welded vertex, as the shared arrays store it.</summary>
public readonly record struct BmdVertex(
    float X, float Y, float Z,
    float Nx, float Ny, float Nz,
    float U, float V);

/// <summary>
/// One corner of a triangle: an index into the shared vertex arrays and the draw table slot the
/// vertex is posed by. The slot is per corner rather than per vertex because J3D indexes the
/// position, normal and texture coordinate separately from the matrix.
/// </summary>
public readonly record struct BmdCorner(int Vertex, int Slot);

/// <summary>
/// A packet: triangles plus the draw table slots they use. The hardware holds ten posing matrices
/// at a time, so a shape that needs more than ten is split into several of these.
/// </summary>
public sealed class BmdPacket
{
    /// <summary>Draw table slots this packet loads, at most <see cref="MaxMatrices"/> of them.</summary>
    public required IReadOnlyList<int> MatrixSlots { get; init; }

    /// <summary>Triangles, as corner triples.</summary>
    public required IReadOnlyList<BmdCorner[]> Triangles { get; init; }

    /// <summary>Position of a draw table slot within this packet's matrix table.</summary>
    public int LocalMatrix(int slot)
    {
        for (int i = 0; i < MatrixSlots.Count; i++)
        {
            if (MatrixSlots[i] == slot)
                return i;
        }

        return 0;
    }

    /// <summary>Posing matrices the hardware holds at once, and so the most a packet may load.</summary>
    public const int MaxMatrices = 10;
}

/// <summary>One shape: the packets that draw it.</summary>
public sealed class BmdShape
{
    /// <summary>Shape name, taken from the source mesh.</summary>
    public required string Name { get; init; }

    /// <summary>Packets, in draw order.</summary>
    public required IReadOnlyList<BmdPacket> Packets { get; init; }

    /// <summary>Triangles across every packet.</summary>
    public int TriangleCount => Packets.Sum(p => p.Triangles.Count);
}

/// <summary>
/// Turns the game's flat per-vertex streams into the shared arrays and index lists J3D wants.
///
/// Three things change on the way across. Vertices are welded, because the source repeats a vertex
/// for every strip that touches it while J3D indexes into one array. Skin weights become a table:
/// each distinct set of joint influences is interned once, so a corner carries a single matrix index
/// rather than three joints and three weights. And the triangles are split into packets, because a
/// packet may only name ten posing matrices - the number the transform unit holds - while a whole
/// character limb routinely draws with more.
/// </summary>
public sealed class BmdGeometry
{
    /// <summary>The welded vertex arrays.</summary>
    public required IReadOnlyList<BmdVertex> Vertices { get; init; }

    /// <summary>Shapes, one per source mesh.</summary>
    public required IReadOnlyList<BmdShape> Shapes { get; init; }

    /// <summary>
    /// Distinct weight sets, each a list of (joint, weight in 1/128 units) pairs. A vertex bound to
    /// a single joint does not get one of these; it goes straight into the draw table.
    /// </summary>
    public required IReadOnlyList<int[][]> WeightSets { get; init; }

    /// <summary>
    /// The draw table: for each slot, whether it indexes a weight set or a joint directly. This is
    /// the indirection that lets a rigidly bound vertex skip the weighting maths.
    /// </summary>
    public required IReadOnlyList<(bool Weighted, int Index)> MatrixTable { get; init; }

    /// <summary>Packets across every shape, which is how many matrix data records are needed.</summary>
    public int PacketCount => Shapes.Sum(s => s.Packets.Count);

    /// <summary>Builds the arrays for a model.</summary>
    public static BmdGeometry Build(RiggedModel model)
    {
        var vertices = new List<BmdVertex>();
        var vertexIndex = new Dictionary<BmdVertex, int>();
        var weightSets = new List<int[][]>();
        var weightSetIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var matrixTable = new List<(bool, int)>();
        var matrixIndex = new Dictionary<(bool, int), int>();
        var shapes = new List<BmdShape>();

        foreach ((string name, ScnMesh mesh) in model.Meshes)
        {
            var slots = new int[mesh.Vertices.Count];
            var welded = new int[mesh.Vertices.Count];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                BmdVertex vertex = Weld(mesh.Vertices[i]);
                if (!vertexIndex.TryGetValue(vertex, out int at))
                {
                    at = vertices.Count;
                    vertices.Add(vertex);
                    vertexIndex[vertex] = at;
                }

                welded[i] = at;
                slots[i] = MatrixSlot(mesh, i, weightSets, weightSetIndex, matrixTable, matrixIndex);
            }

            var triangles = new List<BmdCorner[]>(mesh.Triangles.Count);
            foreach ((int a, int b, int c) in mesh.Triangles)
            {
                triangles.Add([
                    new BmdCorner(welded[a], slots[a]),
                    new BmdCorner(welded[b], slots[b]),
                    new BmdCorner(welded[c], slots[c]),
                ]);
            }

            shapes.Add(new BmdShape { Name = name, Packets = Packetise(triangles) });
        }

        if (matrixTable.Count == 0)
            matrixTable.Add((false, 0));

        return new BmdGeometry
        {
            Vertices = vertices,
            Shapes = shapes,
            WeightSets = weightSets,
            MatrixTable = matrixTable,
        };
    }

    /// <summary>
    /// Splits triangles into packets that each name at most ten matrices. Triangles are taken in
    /// order and a packet is closed as soon as the next one would not fit, which keeps neighbouring
    /// triangles - and so the joints they share - together.
    /// </summary>
    private static List<BmdPacket> Packetise(List<BmdCorner[]> triangles)
    {
        var packets = new List<BmdPacket>();
        var current = new List<BmdCorner[]>();
        var slots = new List<int>();
        var present = new HashSet<int>();

        foreach (BmdCorner[] triangle in triangles)
        {
            int added = triangle.Select(c => c.Slot).Distinct().Count(s => !present.Contains(s));
            if (added > 0 && present.Count + added > BmdPacket.MaxMatrices && current.Count > 0)
            {
                packets.Add(new BmdPacket { MatrixSlots = slots, Triangles = current });
                current = [];
                slots = [];
                present = [];
            }

            foreach (BmdCorner corner in triangle)
            {
                if (present.Add(corner.Slot))
                    slots.Add(corner.Slot);
            }

            current.Add(triangle);
        }

        // A shape always gets at least one packet, so an empty mesh still has somewhere to hang its
        // matrix data and packet location records.
        if (current.Count > 0 || packets.Count == 0)
            packets.Add(new BmdPacket { MatrixSlots = slots.Count > 0 ? slots : [0], Triangles = current });

        return packets;
    }

    private static BmdVertex Weld(ScnVertex v) => new(v.X, v.Y, v.Z, v.Nx, v.Ny, v.Nz, v.U, v.V);

    /// <summary>
    /// Finds or creates the draw table slot for a vertex. A single full-weight influence becomes a
    /// direct joint reference; anything else becomes a weight set.
    /// </summary>
    private static int MatrixSlot(ScnMesh mesh, int vertex,
                                  List<int[][]> weightSets, Dictionary<string, int> weightSetIndex,
                                  List<(bool, int)> matrixTable, Dictionary<(bool, int), int> matrixIndex)
    {
        ScnSkinBinding binding = mesh.Skin is { } skin && vertex < skin.Count
            ? skin[vertex]
            : new ScnSkinBinding(0, -1, -1, 1, 0, 0);

        var influences = binding.Influences
            .Where(i => i.Weight > 0)
            .Select(i => new[] { i.Joint, (int)MathF.Round(i.Weight * 128f) })
            .ToArray();

        if (influences.Length == 0)
            influences = [[Math.Max(0, binding.Joint0), 128]];

        (bool Weighted, int Index) key;
        if (influences.Length == 1)
        {
            key = (false, influences[0][0]);
        }
        else
        {
            string signature = string.Join(",", influences.Select(i => $"{i[0]}:{i[1]}"));
            if (!weightSetIndex.TryGetValue(signature, out int set))
            {
                set = weightSets.Count;
                weightSets.Add(influences);
                weightSetIndex[signature] = set;
            }

            key = (true, set);
        }

        if (!matrixIndex.TryGetValue(key, out int slot))
        {
            slot = matrixTable.Count;
            matrixTable.Add(key);
            matrixIndex[key] = slot;
        }

        return slot;
    }
}
