using PacTool.Formats;

namespace PacTool.Export;

/// <summary>
/// Writes a J3D skeletal animation: <c>J3D1 bck1</c> with a single <c>ANK1</c> section.
///
/// <code>
///   ANK1 header
///   0x00  char[4]  "ANK1"
///   0x04  u32      sectionSize
///   0x08  u8       loopMode
///   0x09  u8       angleShift    rotations are scaled by 2^shift * 180 / 32768 degrees
///   0x0A  u16      duration      in frames
///   0x0C  u16      jointCount
///   0x0E  u16      scaleCount
///   0x10  u16      rotationCount
///   0x12  u16      translationCount
///   0x14  u32      jointsOffset      all four relative to the section start
///   0x18  u32      scalesOffset
///   0x1C  u32      rotationsOffset
///   0x20  u32      translationsOffset
/// </code>
///
/// Each joint is three components - x, y, z - and each component is three index records for scale,
/// rotation and translation in that order. An index record is a count, an index into the shared
/// pool, and a tangent mode; a count of 1 means a constant, and anything more means the pool holds
/// <c>count</c> triples of time, value and tangent.
///
/// The source animations store every frame in full, so this writes one key per frame with a zero
/// tangent, which is what a reader interpolates linearly through. A component that never changes
/// collapses to a single constant key, which is most of them.
/// </summary>
public static class BckWriter
{
    /// <summary>Play the animation once and stop.</summary>
    public const int LoopOnce = 0;

    /// <summary>Play the animation on a loop.</summary>
    public const int LoopRepeat = 2;

    /// <summary>
    /// Builds a <c>.bck</c> for one motion against a skeleton of <paramref name="jointCount"/>
    /// joints. The motion's tracks cover the skeleton's table joints, so joint 0 - the chain the
    /// rest of the skeleton hangs below - takes the whole-model track instead.
    /// </summary>
    public static byte[] Build(MpcMotion motion, int jointCount, int loopMode = LoopRepeat)
    {
        // A reader rejects an animation whose joint count does not match the model's, so the track
        // list is padded or trimmed to the skeleton rather than to whatever the motion carries.
        var scales = new List<float>();
        var rotations = new List<short>();
        var translations = new List<float>();
        var joints = new List<int[]>(jointCount);

        for (int joint = 0; joint < jointCount; joint++)
        {
            var indices = new int[18];      // three components, each scale/rotation/translation
            for (int component = 0; component < 3; component++)
            {
                (float[] scale, float[] rotation, float[] translation) = Sample(motion, joint, component);
                Write(scales, scale, indices, component * 6);
                WriteAngles(rotations, rotation, indices, component * 6 + 2);
                Write(translations, translation, indices, component * 6 + 4);
            }

            joints.Add(indices);
        }

        var section = new J3dWriter();
        section.Ascii("ANK1").U32(0);
        section.U8(loopMode).U8(AngleShift);
        section.U16(Math.Max(0, motion.FrameCount - 1));
        section.U16(jointCount);
        section.U16(scales.Count).U16(rotations.Count).U16(translations.Count);
        int offsets = section.Length;
        section.U32(0).U32(0).U32(0).U32(0);
        section.Align(4);

        section.PatchU32(offsets, section.Length);
        foreach (int[] indices in joints)
        {
            for (int i = 0; i < indices.Length; i += 2)
                section.U16(indices[i]).U16(indices[i + 1]).U16(0);
        }

        section.Align(4);
        section.PatchU32(offsets + 4, section.Length);
        foreach (float value in scales)
            section.F32(value);

        section.Align(4);
        section.PatchU32(offsets + 8, section.Length);
        foreach (short value in rotations)
            section.S16(value);

        section.Align(4);
        section.PatchU32(offsets + 12, section.Length);
        foreach (float value in translations)
            section.F32(value);

        section.Align(32);
        section.PatchU32(4, section.Length);

        byte[] body = section.ToArray();
        var file = new J3dWriter();
        file.Ascii("J3D1").Ascii("bck1").U32(0).U32(1);
        file.Ascii("SVR1").Fill(12, 0xFF);
        file.Bytes(body);
        file.PatchU32(8, file.Length);
        return file.ToArray();
    }

    /// <summary>
    /// Rotations are stored as 16-bit angles scaled by 2^shift. A shift of 0 covers a full turn in
    /// one revolution of the short, which is exactly what the source angles already are, so no
    /// range is lost and no scaling is needed.
    /// </summary>
    private const int AngleShift = 0;

    /// <summary>Pulls one component of one joint's transform out of every frame.</summary>
    private static (float[] Scale, float[] Rotation, float[] Translation) Sample(
        MpcMotion motion, int joint, int component)
    {
        int frames = Math.Max(1, motion.FrameCount);
        var scale = new float[frames];
        var rotation = new float[frames];
        var translation = new float[frames];

        for (int frame = 0; frame < frames; frame++)
        {
            MpcTransform t = frame >= motion.FrameCount ? MpcTransform.Identity
                : joint == 0 ? motion.ModelTransform(frame)
                : joint - 1 < motion.JointCount ? motion.JointTransform(frame, joint - 1)
                : MpcTransform.Identity;

            (scale[frame], rotation[frame], translation[frame]) = component switch
            {
                0 => (t.ScaleX, t.RotationX, t.TranslationX),
                1 => (t.ScaleY, t.RotationY, t.TranslationY),
                _ => (t.ScaleZ, t.RotationZ, t.TranslationZ),
            };
        }

        return (scale, rotation, translation);
    }

    /// <summary>Appends a float track to the pool and records its count and index.</summary>
    private static void Write(List<float> pool, float[] values, int[] indices, int slot)
    {
        if (values.All(v => v.Equals(values[0])))
        {
            indices[slot] = 1;
            indices[slot + 1] = Intern(pool, values[0]);
            return;
        }

        indices[slot] = values.Length;
        indices[slot + 1] = pool.Count;
        for (int i = 0; i < values.Length; i++)
        {
            pool.Add(i);            // time
            pool.Add(values[i]);    // value
            pool.Add(0);            // tangent
        }
    }

    /// <summary>
    /// Appends an angle track. Angles go into a separate 16-bit pool, so the time and tangent
    /// entries are 16-bit too - a frame number, not a fixed-point value.
    /// </summary>
    private static void WriteAngles(List<short> pool, float[] degrees, int[] indices, int slot)
    {
        var raw = new short[degrees.Length];
        for (int i = 0; i < degrees.Length; i++)
            raw[i] = ToAngle(degrees[i]);

        if (raw.All(v => v == raw[0]))
        {
            indices[slot] = 1;
            indices[slot + 1] = Intern(pool, raw[0]);
            return;
        }

        indices[slot] = raw.Length;
        indices[slot + 1] = pool.Count;
        for (int i = 0; i < raw.Length; i++)
        {
            pool.Add((short)i);
            pool.Add(raw[i]);
            pool.Add(0);
        }
    }

    /// <summary>Degrees back to the 16-bit angle a J3D reader expects, wrapping rather than clamping.</summary>
    private static short ToAngle(float degrees)
    {
        double turns = degrees / 360.0;
        double units = (turns - Math.Floor(turns + 0.5)) * 65536.0;
        return (short)Math.Round(units);
    }

    /// <summary>Reuses an existing pool entry for a constant, which keeps the pools small.</summary>
    private static int Intern<T>(List<T> pool, T value) where T : IEquatable<T>
    {
        int at = pool.IndexOf(value);
        if (at >= 0)
            return at;

        pool.Add(value);
        return pool.Count - 1;
    }
}
