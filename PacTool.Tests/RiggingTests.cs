using PacTool.Formats;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The joint rest poses in a <c>.scn</c>: 104-byte records that, unlike everything else in the
/// scene table, are not aligned to 32.
/// </summary>
public class ScnNodeTests
{
    [Fact]
    public void ANodeRecordIsRecognisedAndRead()
    {
        byte[] table = Node("hand_l", id: 0x1234, translation: (1.5f, -2f, 3f),
                            rotation: [0, 0, 1, 0, 1, 0, -1, 0, 0], offset: (4f, 5f, 6f));

        Assert.True(ScnNode.LooksLikeNode(table, 0));
        ScnNode node = ScnNode.Parse(table, 0);

        Assert.Equal("hand_l", node.Name);
        Assert.Equal(0x1234, node.Id);
        Assert.Equal((1.5f, -2f, 3f), node.Translation);
        Assert.Equal((1f, 1f, 1f), node.Scale);
        Assert.Equal([0, 0, 1, 4f, 0, 1, 0, 5f, -1, 0, 0, 6f], node.InverseBind);
    }

    [Fact]
    public void ARecordSpansOneHundredAndFourBytes()
    {
        Assert.Equal(104, ScnNode.RecordSize);
        Assert.Equal(ScnNode.RecordSize, SceneTable.RecordSize + ScnNode.BodySize);
    }

    [Fact]
    public void SomethingWithoutAnOrthonormalRotationIsNotTakenForANode()
    {
        // The rows have to be unit length, which is what stops the walk from desynchronising.
        byte[] stretched = Node("bone", 1, default, [3, 0, 0, 0, 3, 0, 0, 0, 3], default);
        Assert.False(ScnNode.LooksLikeNode(stretched, 0));

        byte[] unnamed = Node("", 1, default, Identity, default);
        Assert.False(ScnNode.LooksLikeNode(unnamed, 0));

        // The low half of the leading word is always zero and the high half never is.
        byte[] node = Node("bone", 1, default, Identity, default);
        node[3] = 1;
        Assert.False(ScnNode.LooksLikeNode(node, 0));

        byte[] zeroId = Node("bone", 0, default, Identity, default);
        Assert.False(ScnNode.LooksLikeNode(zeroId, 0));
    }

    [Fact]
    public void TheRotationComesBackOutAsAQuaternion()
    {
        // A quarter turn about Z: the quaternion is (0, 0, sin 45, cos 45).
        byte[] table = Node("spin", 1, default, [0, -1, 0, 1, 0, 0, 0, 0, 1], default);

        (float x, float y, float z, float w) = ScnNode.Parse(table, 0).InverseBindRotation;

        Assert.Equal(0f, x, 5);
        Assert.Equal(0f, y, 5);
        Assert.Equal(MathF.Sqrt(0.5f), z, 5);
        Assert.Equal(MathF.Sqrt(0.5f), w, 5);
    }

    [Theory]
    // Shepperd's method branches on which diagonal term dominates, so every branch needs a case.
    [InlineData(0f, 0f, 1f)]        // 180 degrees about X
    [InlineData(0f, 1f, 0f)]        // about Y
    [InlineData(1f, 0f, 0f)]        // about Z
    public void EveryBranchOfTheQuaternionConversionRoundTrips(float ax, float ay, float az)
    {
        float[] m = Rotation(ax, ay, az, MathF.PI);
        (float x, float y, float z, float w) = ScnNode.ToQuaternion(
            [m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0]);

        // A half turn about a unit axis is (axis, 0), up to an overall sign.
        float length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        Assert.Equal(1f, length, 4);
        Assert.Equal(0f, w, 4);
        Assert.Equal(1f, MathF.Abs(x * ax + y * ay + z * az), 4);
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>The rotation matrix of an angle about a unit axis, row-major.</summary>
    private static float[] Rotation(float x, float y, float z, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle), t = 1 - c;
        return [
            t * x * x + c, t * x * y - s * z, t * x * z + s * y,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c,
        ];
    }

    /// <summary>A node record: the usual 32-byte header, then translation, scale and inverse bind.</summary>
    internal static byte[] Node(string name, int id, (float X, float Y, float Z) translation,
                                float[] rotation, (float X, float Y, float Z) offset)
    {
        var writer = new BigEndianWriter()
            .U32((uint)id << 16).Field(name, 8).Zeros(20)
            .F32(translation.X).F32(translation.Y).F32(translation.Z)
            .F32(1).F32(1).F32(1);

        for (int row = 0; row < 3; row++)
        {
            writer.F32(rotation[row * 3]).F32(rotation[row * 3 + 1]).F32(rotation[row * 3 + 2]);
            writer.F32(row == 0 ? offset.X : row == 1 ? offset.Y : offset.Z);
        }

        return writer.ToArray();
    }
}

/// <summary>
/// Joining the two halves of a character: the joint table and motions from the <c>.mpc</c>, the
/// geometry and rest poses from the <c>.scn</c>.
/// </summary>
public class RiggedModelTests
{
    [Fact]
    public void AJointsParentIsTheLastEntryAboveItInTheWalk()
    {
        MpcModel skeleton = Skeleton("chn0", [
            (0x01, "root"),         // depth 1 below the header record
            (0x03, "hip"),          // 2
            (0x04, "thigh"),        // 3
            (0x09, "knee"),         // 4
            (0x0A, "shin"),         // 5
            (0x03, "spine"),        // 2, which closes the leg
            (0x04, "chest"),        // 3
        ]);

        RiggedModel model = RiggedModel.Assemble("m01", EmptyScene(), skeleton, []);

        Assert.Equal(
            ["chn0", "root", "hip", "thigh", "knee", "shin", "spine", "chest"],
            model.Joints.Select(j => j.Name));
        Assert.Equal([-1, 0, 1, 2, 3, 4, 1, 6], model.Joints.Select(j => j.Parent));
        Assert.Equal([0, 1, 2, 3, 4, 5, 2, 3], model.Joints.Select(j => j.Depth));
    }

    [Fact]
    public void RestPosesAreMatchedByNameAndEachRecordIsUsedOnce()
    {
        // Mirrored limbs repeat a name, so the second joint called 'arm' takes the second record.
        SceneTable scene = Scene([
            ScnNodeTests.Node("chn0", 1, (0, 0, 0), Identity, default),
            ScnNodeTests.Node("arm", 2, (1, 0, 0), Identity, default),
            ScnNodeTests.Node("arm", 3, (-1, 0, 0), Identity, default),
        ]);
        MpcModel skeleton = Skeleton("chn0", [(0x01, "arm"), (0x03, "arm"), (0x04, "tail")]);

        RiggedModel model = RiggedModel.Assemble("m01", scene, skeleton, []);

        Assert.Equal(1f, model.Joints[1].Translation.X);
        Assert.Equal(-1f, model.Joints[2].Translation.X);
        Assert.True(model.Joints[0].HasRestPose);
        Assert.False(model.Joints[3].HasRestPose);
        Assert.Equal(3, model.PosedJointCount);
        Assert.Contains(model.Warnings, w => w.Contains("1 of 4 joint(s) have no rest pose"));
    }

    [Fact]
    public void TheListingShowsTheTreeAndWhichJointsArePosed()
    {
        MpcModel skeleton = Skeleton("chn0", [(0x01, "root")]);

        string listing = RiggedModel.Assemble("m01", EmptyScene(), skeleton, []).DescribeSkeleton();

        Assert.Contains("2 joint(s), 0 with a rest pose", listing);
        Assert.Contains("root", listing);
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    internal static SceneTable EmptyScene() => Scene([]);

    /// <summary>A scene table holding nothing but joint records.</summary>
    internal static SceneTable Scene(IReadOnlyList<byte[]> nodes)
    {
        var writer = new BigEndianWriter().U16(nodes.Count).Zeros(30);
        foreach (byte[] node in nodes)
            writer.Bytes(node);

        return SceneTable.Parse(writer.ToArray(), 0);
    }

    /// <summary>An MPC skeleton: the header's entry-shaped record, then the joint table.</summary>
    internal static MpcModel Skeleton(string root, IReadOnlyList<(int Type, string Name)> nodes)
    {
        var writer = new BigEndianWriter()
            .U32(nodes.Count + 1)
            .U32(0xFD00)
            .Zeros(3).Field(root, 8).Zeros(2)
            .PadTo(MpcModel.EntryTableOffset);

        foreach ((int type, string name) in nodes)
            writer.U16(0).U8(type).Field(name, 8).Zeros(5).U32(0).U16(0).U16(0);

        return MpcModel.Parse(writer.ToArray(), "test.mpc");
    }
}
