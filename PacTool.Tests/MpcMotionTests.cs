using PacTool.Formats;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The motion container that follows the joint table in a <c>.mpc</c>: a directory of named
/// offsets, then one uncompressed frame after another.
/// </summary>
public class MpcMotionTests
{
    [Fact]
    public void TheDirectoryIsWalkedUntilItReachesTheFirstMotion()
    {
        byte[] data = File(joints: 2, [
            ("walk", frames: 3, mask: 0x02),
            ("jump", frames: 2, mask: 0x02),
        ]);

        var warnings = new List<string>();
        List<MpcMotion> motions = MpcMotion.ParseAll(data, 0, joints: 2, warnings);

        Assert.Empty(warnings);
        Assert.Equal(["walk", "jump"], motions.Select(m => m.Name));
        Assert.Equal([3, 2], motions.Select(m => m.FrameCount));
    }

    [Theory]
    // Bit 1 is always set: two model tracks and one joint track. Bit 2 adds one of each, bit 3
    // another joint track, and bit 0 another model track.
    [InlineData(0x02, 2, 1)]
    [InlineData(0x03, 3, 1)]
    [InlineData(0x06, 3, 2)]
    [InlineData(0x0A, 2, 2)]
    [InlineData(0x0E, 3, 3)]
    public void TheTrackMaskFixesTheFrameStride(int mask, int modelTracks, int jointTracks)
    {
        byte[] data = File(joints: 4, [("test", frames: 1, mask)]);

        MpcMotion motion = Assert.Single(MpcMotion.ParseAll(data, 0, joints: 4, []));

        Assert.Equal(modelTracks, motion.ModelTracks);
        Assert.Equal(jointTracks, motion.JointTracks);
        Assert.Equal(2 + (modelTracks + jointTracks * 4) * MpcMotion.TrackSize, motion.FrameSize);
        Assert.True(motion.IsComplete);
    }

    [Fact]
    public void TheModelBlockLeadsTheFrameAndTheJointsFollowTheReservedPair()
    {
        // Two model tracks, a reserved pair, then two joints of two tracks each.
        var frame = new BigEndianWriter()
            .U16(256).U16(512).U16(768)                     // model translation, in 1/256ths
            .U16(16384).U16(0).U16(0)                       // model rotation, a quarter turn
            .U16(0)                                         // the reserved pair
            .U16(256).U16(0).U16(0).U16(8192).U16(0).U16(0) // joint 0
            .U16(0).U16(512).U16(0).U16(0).U16(4096).U16(0) // joint 1
            .ToArray();

        byte[] data = File(joints: 2, [("test", frames: 1, mask: 0x0A)], [frame]);
        MpcMotion motion = Assert.Single(MpcMotion.ParseAll(data, 0, joints: 2, []));

        MpcTransform model = motion.ModelTransform(0);
        Assert.Equal((1f, 2f, 3f), (model.TranslationX, model.TranslationY, model.TranslationZ));
        Assert.Equal(90f, model.RotationX, 3);

        MpcTransform first = motion.JointTransform(0, 0);
        Assert.Equal(1f, first.TranslationX, 3);
        Assert.Equal(45f, first.RotationX, 3);

        MpcTransform second = motion.JointTransform(0, 1);
        Assert.Equal(2f, second.TranslationY, 3);
        Assert.Equal(22.5f, second.RotationY, 3);
    }

    [Fact]
    public void ASingleTrackIsTheRotation()
    {
        // A joint with one track sweeps past a half turn and wraps, which only an angle does.
        var frame = new BigEndianWriter()
            .U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .U16(0)
            .U16(0xC000).U16(0).U16(0)
            .ToArray();

        byte[] data = File(joints: 1, [("test", frames: 1, mask: 0x02)], [frame]);
        MpcMotion motion = Assert.Single(MpcMotion.ParseAll(data, 0, joints: 1, []));

        MpcTransform joint = motion.JointTransform(0, 0);
        Assert.Equal(-90f, joint.RotationX, 3);
        Assert.Equal(0f, joint.TranslationX);
    }

    [Fact]
    public void AMotionThatDoesNotFitBeforeTheNextOneIsSkippedAndReported()
    {
        byte[] data = File(joints: 2, [("big", frames: 1, mask: 0x02), ("next", frames: 1, mask: 0x02)]);

        // Claim far more frames than the space before the next motion can hold.
        data[32] = 0x03;
        data[33] = 0x84;

        var warnings = new List<string>();
        List<MpcMotion> motions = MpcMotion.ParseAll(data, 0, joints: 2, warnings);

        Assert.Equal(["next"], motions.Select(m => m.Name));
        Assert.Contains(warnings, w => w.Contains("'big'") && w.Contains("skipped"));
    }

    [Fact]
    public void SomethingThatIsNotADirectoryYieldsNoMotions()
    {
        Assert.Empty(MpcMotion.ParseAll(new byte[64], 0, joints: 1, []));
    }

    [Fact]
    public void TheListingNamesEachTrackRun()
    {
        byte[] data = File(joints: 1, [("only", frames: 2, mask: 0x06)]);

        string listing = MpcMotion.Describe("test.mpc", MpcMotion.ParseAll(data, 0, joints: 1, []));

        Assert.Contains("only", listing);
        Assert.Contains("trans+?+rot", listing);
        Assert.Contains("trans+rot", listing);
    }

    /// <summary>
    /// A motion directory - sixteen bytes an entry - followed by the motions themselves. Frames
    /// default to zeros, which is a valid animation that simply never moves.
    /// </summary>
    private static byte[] File(int joints,
                               IReadOnlyList<(string name, int frames, int mask)> motions,
                               IReadOnlyList<byte[]>? frames = null)
    {
        int directory = motions.Count * 16;
        var offsets = new List<int>();
        var bodies = new List<byte[]>();
        int at = directory;

        for (int i = 0; i < motions.Count; i++)
        {
            (string name, int count, int mask) = motions[i];
            int modelTracks = (mask & 0x01) != 0 || (mask & 0x04) != 0 ? 3 : 2;
            int jointTracks = 1 + ((mask & 0x04) != 0 ? 1 : 0) + ((mask & 0x08) != 0 ? 1 : 0);
            int frameSize = 2 + (modelTracks + jointTracks * joints) * MpcMotion.TrackSize;

            var body = new BigEndianWriter()
                .U16(count).U16(mask).Field(name, 8).Zeros(8);
            for (int f = 0; f < count; f++)
            {
                byte[]? supplied = frames is not null && f < frames.Count ? frames[f] : null;
                if (supplied is null)
                    body.Zeros(frameSize);
                else
                    body.Bytes(supplied).Zeros(frameSize - supplied.Length);
            }

            offsets.Add(at);
            byte[] bytes = body.ToArray();
            bodies.Add(bytes);
            at += bytes.Length;
        }

        var writer = new BigEndianWriter();
        for (int i = 0; i < motions.Count; i++)
            writer.U32(offsets[i]).Field(motions[i].name, 8).U32(0);

        foreach (byte[] body in bodies)
            writer.Bytes(body);

        return writer.ToArray();
    }
}
