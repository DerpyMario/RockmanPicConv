using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// The transform an animation track carries for one joint on one frame. Absent components are
/// left at their identity values, so a track that only rotates still reads cleanly.
/// </summary>
public readonly record struct MpcTransform(
    float ScaleX, float ScaleY, float ScaleZ,
    float RotationX, float RotationY, float RotationZ,
    float TranslationX, float TranslationY, float TranslationZ)
{
    /// <summary>No scale, no rotation, no translation.</summary>
    public static MpcTransform Identity { get; } = new(1, 1, 1, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// One named animation from a <c>.mpc</c>.
///
/// A motion is a 20-byte header followed by one uncompressed frame after another - there are no
/// keyframes and no interpolation, every frame states every joint's transform:
///
/// <code>
///   0x00  u16       frameCount
///   0x02  u16       tracks      which tracks each frame carries, see below
///   0x04  char[8]   name
///   0x0C  u8[8]     reserved
///   0x14  ...       frame[frameCount]
/// </code>
///
/// A frame is the whole-model tracks, a reserved pair, then the per-joint tracks, each track three
/// signed 16-bit values:
///
/// <code>
///   0x00  ...       model track[2 or 3]
///   ...   s16       reserved, zero throughout
///   ...             joint track[joints][1, 2 or 3]
/// </code>
///
/// Rotations are angles where 32768 is 180 degrees, which is the convention this hardware uses
/// throughout; translations are fixed point with an 8-bit fraction, matching the geometry.
///
/// The low nibble of <c>tracks</c> says how many of each are present. Bit 1 is always set and
/// gives one joint track and two model tracks; bit 2 adds a joint track and a model track; bit 3
/// adds another joint track; bit 0 adds another model track. Every combination that occurs in the
/// reference data reproduces the exact distance to the next motion in the file, for 1220 of the
/// 1239 motions.
/// </summary>
public sealed class MpcMotion
{
    /// <summary>Size of a motion header.</summary>
    public const int HeaderSize = 0x14;

    /// <summary>Bytes one track occupies: three signed 16-bit values.</summary>
    public const int TrackSize = 6;

    /// <summary>Motion name, normally the model stem plus a two-letter verb and a number.</summary>
    public required string Name { get; init; }

    /// <summary>Offset of the motion within the file.</summary>
    public required int Offset { get; init; }

    /// <summary>Number of frames. Every frame is stored in full.</summary>
    public required int FrameCount { get; init; }

    /// <summary>The raw track mask from the header.</summary>
    public required int TrackMask { get; init; }

    /// <summary>Tracks the whole-model block carries per frame: 2 or 3.</summary>
    public required int ModelTracks { get; init; }

    /// <summary>Tracks each joint carries per frame: 1, 2 or 3.</summary>
    public required int JointTracks { get; init; }

    /// <summary>Joints the motion animates, which is every joint of the skeleton.</summary>
    public required int JointCount { get; init; }

    /// <summary>The frame data, or empty when the motion did not fit in the file.</summary>
    public required byte[] Frames { get; init; }

    /// <summary>Bytes one frame occupies.</summary>
    public int FrameSize => 2 + (ModelTracks + JointTracks * JointCount) * TrackSize;

    /// <summary>True when the frame data is all there.</summary>
    public bool IsComplete => Frames.Length >= (long)FrameCount * FrameSize;

    /// <summary>
    /// Reads the whole-model transform on one frame. The model block sits at the front of the
    /// frame; the two reserved bytes come after it, before the joints.
    /// </summary>
    public MpcTransform ModelTransform(int frame) => ReadTransform(frame, 0, 0, ModelTracks);

    /// <summary>Reads one joint's transform on one frame.</summary>
    public MpcTransform JointTransform(int frame, int joint)
    {
        if ((uint)joint >= (uint)JointCount)
            throw new ArgumentOutOfRangeException(nameof(joint), $"This motion animates {JointCount} joint(s).");

        return ReadTransform(frame, ModelTracks * TrackSize + 2, joint * JointTracks, JointTracks);
    }

    /// <summary>
    /// Reads a run of tracks.
    ///
    /// The last track of a run is always the rotation. Two things say so: where a joint has only
    /// one track its values sweep past 32767 and wrap to negative, which only an angle does; and
    /// the last track of every model block in the reference data is a constant (16384, 0, 0),
    /// which as an angle is the quarter turn a model's base orientation needs and as anything else
    /// is meaningless.
    ///
    /// The first track of a run of more than one is the translation, from the root motion: a
    /// walking animation's first model track advances along one axis frame by frame, and a jump's
    /// rises and falls on another.
    ///
    /// A run of three has a middle track this tool does not identify. It is read out to the CSV
    /// listing but not used when building an animation.
    /// </summary>
    private MpcTransform ReadTransform(int frame, int blockOffset, int trackIndex, int trackCount)
    {
        if ((uint)frame >= (uint)FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), $"This motion has {FrameCount} frame(s).");

        int at = frame * FrameSize + blockOffset + trackIndex * TrackSize;
        if (at + trackCount * TrackSize > Frames.Length)
            return MpcTransform.Identity;

        MpcTransform result = MpcTransform.Identity;

        (float rx, float ry, float rz) = ReadTrack(at + (trackCount - 1) * TrackSize, 180f / 32768f);
        result = result with { RotationX = rx, RotationY = ry, RotationZ = rz };

        if (trackCount > 1)
        {
            (float tx, float ty, float tz) = ReadTrack(at, 1f / 256f);
            result = result with { TranslationX = tx, TranslationY = ty, TranslationZ = tz };
        }

        return result;
    }

    /// <summary>The unidentified middle track of a three-track run, or null when there is none.</summary>
    public (float X, float Y, float Z)? UnclassifiedModelTrack(int frame) =>
        ModelTracks < 3 ? null : ReadTrack(frame * FrameSize + TrackSize, 1f / 256f);

    private (float X, float Y, float Z) ReadTrack(int at, float scale) => (
        BinaryPrimitives.ReadInt16BigEndian(Frames.AsSpan(at)) * scale,
        BinaryPrimitives.ReadInt16BigEndian(Frames.AsSpan(at + 2)) * scale,
        BinaryPrimitives.ReadInt16BigEndian(Frames.AsSpan(at + 4)) * scale);

    /// <summary>
    /// Reads the motion directory that follows the joint table, and every motion it points at.
    /// The directory is 16-byte records - a file offset, an eight-character name and a reserved
    /// word - running until the offset of the first motion, which is where the directory ends.
    /// </summary>
    public static List<MpcMotion> ParseAll(ReadOnlySpan<byte> data, int directoryOffset, int joints,
                                           List<string> warnings)
    {
        var motions = new List<MpcMotion>();
        var entries = new List<(int Offset, string Name)>();
        int at = directoryOffset;

        while (at + 16 <= data.Length)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            if (!Ascii.IsPrintableName(data.Slice(at + 4, 8), minimumLength: 3))
                break;
            if (offset < directoryOffset || offset >= data.Length)
                break;
            if (entries.Count > 0 && offset <= entries[^1].Offset)
                break;

            entries.Add((offset, Ascii.Decode(data.Slice(at + 4, 8))));
            at += 16;

            // The first entry points at the byte just past the directory, so once the walk reaches
            // that offset the directory is over.
            if (at >= entries[0].Offset)
                break;
        }

        if (entries.Count == 0 || entries[0].Offset != at)
            return motions;

        for (int i = 0; i < entries.Count; i++)
        {
            (int offset, string name) = entries[i];
            if (offset + HeaderSize > data.Length)
            {
                warnings.Add($"motion '{name}' starts at 0x{offset:X}, past the end of the file.");
                continue;
            }

            int frameCount = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int mask = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if ((mask & 0x02) == 0)
            {
                warnings.Add($"motion '{name}' has track mask 0x{mask:X4}, which this tool does not recognise.");
                continue;
            }

            int modelTracks = (mask & 0x01) != 0 || (mask & 0x04) != 0 ? 3 : 2;
            int jointTracks = 1 + ((mask & 0x04) != 0 ? 1 : 0) + ((mask & 0x08) != 0 ? 1 : 0);
            long frameSize = 2 + (modelTracks + (long)jointTracks * joints) * TrackSize;
            long wanted = frameSize * frameCount;
            long limit = (i + 1 < entries.Count ? entries[i + 1].Offset : data.Length) - offset - HeaderSize;

            if (wanted > limit)
            {
                warnings.Add($"motion '{name}' needs {wanted:N0} bytes of frames but only {limit:N0} are available " +
                             "before the next motion; skipped.");
                continue;
            }

            motions.Add(new MpcMotion
            {
                Name = Ascii.Decode(data.Slice(offset + 4, 8)) is { Length: > 0 } stored ? stored : name,
                Offset = offset,
                FrameCount = frameCount,
                TrackMask = mask,
                ModelTracks = modelTracks,
                JointTracks = jointTracks,
                JointCount = joints,
                Frames = data.Slice(offset + HeaderSize, (int)wanted).ToArray(),
            });
        }

        return motions;
    }

    /// <summary>Renders the motion list as the <c>motions.txt</c> listing.</summary>
    public static string Describe(string title, IReadOnlyList<MpcMotion> motions)
    {
        var text = new StringBuilder();
        text.AppendLine($"# motions of {title}  ({motions.Count})");
        text.AppendLine("#");
        text.AppendLine("# Every frame is stored in full; there are no keyframes to interpolate between.");
        text.AppendLine("# Rotations are angles, translations are in model units.");
        text.AppendLine("#");
        text.AppendLine("#  name      frames  mask    model tracks  joint tracks  frame bytes  offset");
        text.AppendLine("# --------  -------  ------  ------------  ------------  -----------  --------");

        foreach (MpcMotion motion in motions)
        {
            text.AppendLine($"  {motion.Name,-8}  {motion.FrameCount,7}  0x{motion.TrackMask:X4}  " +
                            $"{Names(motion.ModelTracks),-12}  {Names(motion.JointTracks),-12}  " +
                            $"{motion.FrameSize,11:N0}  0x{motion.Offset:X6}");
        }

        return text.ToString();
    }

    private static string Names(int tracks) => tracks switch
    {
        1 => "rot",
        2 => "trans+rot",
        _ => "trans+?+rot",
    };

    /// <summary>Renders one motion's frames as CSV, one row per frame and joint.</summary>
    public string DescribeFrames(IReadOnlyList<string> jointNames)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {Name}: {FrameCount} frame(s), {JointCount} joint(s)");
        text.AppendLine("frame,joint,name,scaleX,scaleY,scaleZ,rotX,rotY,rotZ,transX,transY,transZ");

        for (int frame = 0; frame < FrameCount; frame++)
        {
            Row(text, frame, -1, "<model>", ModelTransform(frame));
            for (int joint = 0; joint < JointCount; joint++)
            {
                string name = joint < jointNames.Count ? jointNames[joint] : $"joint{joint}";
                Row(text, frame, joint, name, JointTransform(frame, joint));
            }
        }

        return text.ToString();
    }

    private static void Row(StringBuilder text, int frame, int joint, string name, MpcTransform t) =>
        text.AppendLine($"{frame},{(joint < 0 ? "" : joint.ToString(CultureInfo.InvariantCulture))},{name}," +
                        $"{F(t.ScaleX)},{F(t.ScaleY)},{F(t.ScaleZ)}," +
                        $"{F(t.RotationX)},{F(t.RotationY)},{F(t.RotationZ)}," +
                        $"{F(t.TranslationX)},{F(t.TranslationY)},{F(t.TranslationZ)}");

    private static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
