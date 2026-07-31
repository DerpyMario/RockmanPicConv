using System.Buffers.Binary;
using PacTool.Export;
using PacTool.Formats;
using PacTool.Gx;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The BMD writer. The model it produces is read back through <see cref="J3dModel"/>, which is the
/// only end-to-end check available: no BMD ships with this game to compare against.
/// </summary>
public class BmdWriterTests
{
    [Fact]
    public void AWrittenModelReadsBackAsAJ3dModel()
    {
        byte[] bytes = BmdWriter.Build(Model(), [Texture("skin")]);

        Assert.True(J3dModel.LooksLikeModel(bytes));
        J3dModel model = J3dModel.Parse(bytes, "test.bmd");

        Assert.Equal("bmd3", model.Variant);
        Assert.Empty(model.Warnings);
        Assert.Equal(["INF1", "VTX1", "EVP1", "DRW1", "JNT1", "SHP1", "MAT3", "TEX1"],
                     model.Sections.Select(s => s.Magic));
        Assert.Equal(["root", "arm", "hand"], model.JointNames);
        Assert.Equal(["mesh"], model.MaterialNames);
        Assert.Equal(["skin"], model.Textures.Select(t => t.Name));
    }

    [Fact]
    public void TheBdlVariantIsTaggedDifferentlyAndIsOtherwiseTheSame()
    {
        byte[] bmd = BmdWriter.Build(Model(), []);
        byte[] bdl = BmdWriter.Build(Model(), [], binaryDisplayLists: true);

        Assert.Equal("bdl4", J3dModel.Parse(bdl, "test.bdl").Variant);
        Assert.Equal(bmd.Length, bdl.Length);
        Assert.Equal(bmd[8..], bdl[8..]);
    }

    [Fact]
    public void VerticesAreWeldedAcrossTheMeshesThatShareThem()
    {
        // The same three positions twice: the source repeats them, the shared array should not.
        var mesh = Mesh(3, [(0, 1, 2)]);
        RiggedModel model = Model([("a", mesh), ("b", mesh)]);

        BmdGeometry geometry = BmdGeometry.Build(model);

        Assert.Equal(3, geometry.Vertices.Count);
        Assert.Equal(2, geometry.Shapes.Count);
        Assert.Equal(1, geometry.Shapes[0].TriangleCount);
        Assert.Equal(1, geometry.Shapes[1].TriangleCount);
    }

    [Fact]
    public void ASingleFullWeightInfluenceSkipsTheWeightTable()
    {
        ScnMesh rigid = Mesh(3, [(0, 1, 2)], [
            new ScnSkinBinding(1, -1, -1, 1f, 0f, 0f),
            new ScnSkinBinding(1, -1, -1, 1f, 0f, 0f),
            new ScnSkinBinding(2, 1, -1, 0.5f, 0.5f, 0f),
        ]);

        BmdGeometry geometry = BmdGeometry.Build(Model([("mesh", rigid)]));

        // One weight set for the blended vertex; the rigid ones are plain joint references.
        int[][] set = Assert.Single(geometry.WeightSets);
        Assert.Equal([[2, 64], [1, 64]], set);
        Assert.Equal([(false, 1), (true, 0)], geometry.MatrixTable);
    }

    [Fact]
    public void AShapeNeedingMoreThanTenMatricesIsSplitIntoPackets()
    {
        // Twelve triangles, each bound to a joint of its own: more than one packet can carry.
        var bindings = new List<ScnSkinBinding>();
        var triangles = new List<(int, int, int)>();
        for (int i = 0; i < 12; i++)
        {
            bindings.Add(new ScnSkinBinding(i, -1, -1, 1f, 0f, 0f));
            bindings.Add(new ScnSkinBinding(i, -1, -1, 1f, 0f, 0f));
            bindings.Add(new ScnSkinBinding(i, -1, -1, 1f, 0f, 0f));
            triangles.Add((i * 3, i * 3 + 1, i * 3 + 2));
        }

        ScnMesh mesh = Mesh(36, triangles, bindings);
        BmdShape shape = Assert.Single(BmdGeometry.Build(Model([("mesh", mesh)], joints: 12)).Shapes);

        Assert.Equal(2, shape.Packets.Count);
        Assert.All(shape.Packets, p => Assert.InRange(p.MatrixSlots.Count, 1, BmdPacket.MaxMatrices));
        Assert.Equal(12, shape.TriangleCount);
    }

    [Fact]
    public void TheSceneGraphHasASingleRootWithTheShapesBelowIt()
    {
        byte[] bytes = BmdWriter.Build(Model(), []);
        (int at, int _) = Section(bytes, "INF1");

        int nodes = at + (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 0x14));
        int depth = 0;
        int top = 0;
        var kinds = new List<int>();
        for (int i = 0; ; i++)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(nodes + i * 4));
            if (type == 0)
                break;
            if (type == 1)
                depth++;
            else if (type == 2)
                depth--;
            else
            {
                if (depth == 0)
                    top++;
                kinds.Add(type);
            }
        }

        Assert.Equal(0, depth);
        Assert.Equal(1, top);
        Assert.Equal(0x10, kinds[0]);                       // the root joint
        Assert.Equal(3, kinds.Count(k => k == 0x10));       // every joint is placed
        Assert.Equal(1, kinds.Count(k => k == 0x12));       // and the one shape is drawn
    }

    [Fact]
    public void JointNamesAreMadeUniqueBecauseMirroredLimbsRepeatThem()
    {
        RiggedModel model = Model(joints: 3, names: ["root", "arm", "arm"]);

        Assert.Equal(["root", "arm", "arm_2"], J3dModel.Parse(BmdWriter.Build(model, []), "t.bmd").JointNames);
    }

    /// <summary>Finds a section by magic.</summary>
    private static (int At, int Size) Section(byte[] bytes, string magic)
    {
        for (int at = 0x20; at < bytes.Length;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4));
            if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == magic)
                return (at, size);

            at += size;
        }

        throw new InvalidOperationException($"no {magic} section");
    }

    internal static ScnMesh Mesh(int vertices, IReadOnlyList<(int A, int B, int C)> triangles,
                                 IReadOnlyList<ScnSkinBinding>? skin = null) => new()
    {
        Vertices = Enumerable.Range(0, vertices)
            .Select(i => new ScnVertex(i, i * 2, i * 3, 0, 1, 0, i / 16f, 0))
            .ToList(),
        Triangles = triangles,
        Skin = skin ?? Enumerable.Range(0, vertices)
            .Select(_ => new ScnSkinBinding(0, -1, -1, 1f, 0f, 0f)).ToList(),
        Description = "test",
        PrimitiveCount = 1,
    };

    internal static RiggedModel Model(IReadOnlyList<(string Name, ScnMesh Mesh)>? meshes = null,
                                      int joints = 3, IReadOnlyList<string>? names = null)
    {
        names ??= ["root", "arm", "hand"];
        return new RiggedModel
        {
            Name = "m01",
            Joints = Enumerable.Range(0, joints).Select(i => new RiggedJoint
            {
                Index = i,
                Name = i < names.Count ? names[i] : $"joint{i}",
                Parent = i - 1,
                Depth = i,
                Translation = (i, 0, 0),
            }).ToList(),
            Meshes = meshes ?? [("mesh", Mesh(3, [(0, 1, 2)]))],
            Motions = [],
            TextureNames = [],
        };
    }

    internal static PicturePackTexture Texture(string name) => new()
    {
        Index = 0,
        Name = name,
        Format = GxTextureFormat.Rgb565,
        Width = 8,
        Height = 8,
        WrapS = 1,
        WrapT = 1,
        MinFilter = 1,
        MagFilter = 1,
        MinLod = 0,
        MaxLod = 0,
        MipLevels = 1,
        HeaderOffset = 0,
        Data = new byte[GxTextureFormats.MipChainSize(GxTextureFormat.Rgb565, 8, 8, 1)],
    };
}

/// <summary>
/// The BCK writer. Every source frame is stored in full, so the interesting parts are the pools:
/// a component that never changes has to collapse to a single key, or a 60-frame animation of a
/// 37-joint skeleton would carry six thousand pointless keys.
/// </summary>
public class BckWriterTests
{
    [Fact]
    public void TheHeaderDescribesTheAnimationAndItsPools()
    {
        byte[] bytes = BckWriter.Build(Motion(frames: 4, joints: 2), jointCount: 3);

        Assert.Equal("J3D1", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("bck1", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
        Assert.Equal(bytes.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));

        Ank1 ank = Ank1.Parse(bytes);
        Assert.Equal("ANK1", ank.Magic);
        Assert.Equal(BckWriter.LoopRepeat, ank.LoopMode);
        Assert.Equal(0, ank.AngleShift);
        Assert.Equal(3, ank.Duration);                      // four frames span three intervals
        Assert.Equal(3, ank.JointCount);
    }

    [Fact]
    public void TheTrackListIsPaddedToTheSkeletonRatherThanTheMotion()
    {
        // A reader refuses an animation whose joint count differs from the model's.
        Ank1 ank = Ank1.Parse(BckWriter.Build(Motion(frames: 2, joints: 1), jointCount: 8));

        Assert.Equal(8, ank.JointCount);
        Assert.All(Enumerable.Range(0, 8), j => ank.Component(j, 0, 0));
    }

    [Fact]
    public void AComponentThatNeverChangesCollapsesToOneKey()
    {
        // Nothing in this motion moves, so every component is a constant.
        Ank1 ank = Ank1.Parse(BckWriter.Build(Motion(frames: 30, joints: 4), jointCount: 5));

        for (int joint = 0; joint < 5; joint++)
        {
            for (int component = 0; component < 3; component++)
            {
                for (int track = 0; track < 3; track++)
                    Assert.Equal(1, ank.Component(joint, component, track).Count);
            }
        }

        // One scale, one angle and one translation value between them, reused everywhere.
        Assert.Equal(1, ank.ScaleCount);
        Assert.Equal(1, ank.RotationCount);
        Assert.Equal(1, ank.TranslationCount);
    }

    [Fact]
    public void AMovingComponentGetsOneKeyPerFrame()
    {
        // Joint 0 slides along X: three model translation values, so three keys and no more.
        var frames = new List<byte[]>();
        for (int i = 0; i < 3; i++)
        {
            frames.Add(new BigEndianWriter()
                .U16(256 * (i + 1)).U16(0).U16(0)           // model translation
                .U16(0).U16(0).U16(0)                       // model rotation
                .U16(0)                                     // reserved
                .U16(0).U16(0).U16(0)                       // the single joint's rotation
                .ToArray());
        }

        Ank1 ank = Ank1.Parse(BckWriter.Build(Motion(3, 1, frames), jointCount: 2));

        // Skeleton joint 0 takes the whole-model track, so that is where the movement lands.
        (int count, int index) = ank.Component(0, 0, 2);
        Assert.Equal(3, count);
        Assert.Equal([0f, 1f, 0f, 1f, 2f, 0f, 2f, 3f, 0f], ank.Translations(index, count));

        // Its Y and Z stay put, and so does everything about joint 1.
        Assert.Equal(1, ank.Component(0, 1, 2).Count);
        Assert.Equal(1, ank.Component(1, 0, 2).Count);
    }

    [Fact]
    public void AnglesAreWrittenAsSixteenBitTurnsWithNoScaling()
    {
        var frames = new List<byte[]>();
        foreach (int angle in new[] { 0, 0x4000, 0x8000 })
        {
            frames.Add(new BigEndianWriter()
                .U16(0).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
                .U16(angle).U16(0).U16(0)
                .ToArray());
        }

        Ank1 ank = Ank1.Parse(BckWriter.Build(Motion(3, 1, frames), jointCount: 2));

        // Joint 1 is the motion's joint 0. A shift of zero means the stored angles are the
        // source's own, so a quarter turn stays 0x4000 and a half turn wraps to -0x8000.
        (int count, int index) = ank.Component(1, 0, 1);
        Assert.Equal(3, count);
        Assert.Equal(0, ank.AngleShift);
        Assert.Equal([0, 0, 0, 1, 0x4000, 0, 2, -0x8000, 0], ank.Rotations(index, count));
    }

    /// <summary>A motion whose frames are all zeros unless supplied.</summary>
    internal static MpcMotion Motion(int frames, int joints, IReadOnlyList<byte[]>? content = null)
    {
        const int ModelTracks = 2, JointTracks = 1;
        int frameSize = 2 + (ModelTracks + JointTracks * joints) * MpcMotion.TrackSize;
        var writer = new BigEndianWriter();
        for (int i = 0; i < frames; i++)
        {
            byte[]? supplied = content is not null && i < content.Count ? content[i] : null;
            if (supplied is null)
                writer.Zeros(frameSize);
            else
                writer.Bytes(supplied).Zeros(frameSize - supplied.Length);
        }

        return new MpcMotion
        {
            Name = "test",
            Offset = 0,
            FrameCount = frames,
            TrackMask = 0x02,
            ModelTracks = ModelTracks,
            JointTracks = JointTracks,
            JointCount = joints,
            Frames = writer.ToArray(),
        };
    }
}

/// <summary>
/// A minimal reader for the animations the writer produces, kept separate from the production code
/// so the tests are not checking the writer against itself.
/// </summary>
internal sealed class Ank1
{
    private readonly byte[] _data;
    private readonly int _at;

    private Ank1(byte[] data, int at)
    {
        _data = data;
        _at = at;
    }

    public static Ank1 Parse(byte[] data) => new(data, 0x20);

    public string Magic => System.Text.Encoding.ASCII.GetString(_data, _at, 4);
    public int LoopMode => _data[_at + 8];
    public int AngleShift => _data[_at + 9];
    public int Duration => U16(_at + 10);
    public int JointCount => U16(_at + 12);
    public int ScaleCount => U16(_at + 14);
    public int RotationCount => U16(_at + 16);
    public int TranslationCount => U16(_at + 18);

    private int Joints => _at + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_at + 0x14));
    private int Scales => _at + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_at + 0x18));
    private int RotationPool => _at + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_at + 0x1C));
    private int TranslationPool => _at + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_at + 0x20));

    /// <summary>One index record: a joint's component, for scale (0), rotation (1) or translation (2).</summary>
    public (int Count, int Index) Component(int joint, int component, int track)
    {
        int at = Joints + joint * 54 + component * 18 + track * 6;
        Assert.InRange(at + 6, 0, _data.Length);
        Assert.Equal(0, U16(at + 4));                       // tangent mode
        return (U16(at), U16(at + 2));
    }

    /// <summary>Reads a translation track as time, value and tangent triples.</summary>
    public float[] Translations(int index, int count)
    {
        var values = new float[count * 3];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(TranslationPool + (index + i) * 4));

        return values;
    }

    /// <summary>Reads a rotation track as time, angle and tangent triples.</summary>
    public int[] Rotations(int index, int count)
    {
        var values = new int[count * 3];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(RotationPool + (index + i) * 2));

        return values;
    }

    private int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
}
