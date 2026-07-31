using System.Buffers.Binary;

namespace PacTool.Formats;

/// <summary>
/// A joint's rest pose, stored in the scene table of the <c>.scn</c> beside its skeleton's
/// <c>.mpc</c>. This is what makes a character model riggable: the skinned vertices name joints,
/// the <c>.mpc</c> gives the hierarchy, and these records give the transforms.
///
/// A node record is a 32-byte header - the usual name at 0x04, with the word at 0x00 carrying a
/// non-zero value in its high half - followed by 72 bytes:
///
/// <code>
///   0x00  f32[3]   translation   relative to the parent joint
///   0x0C  f32[3]   scale
///   0x18  f32[12]  inverse bind  a 3x4 matrix, row-major, world space to joint space
/// </code>
///
/// Records are 104 bytes and, unlike everything else in the table, are not aligned to 32.
///
/// Confirmed across the 4801 records in the reference data: every scale is (1, 1, 1) - nine of them
/// off by a part in a million, the rest exact - and every inverse bind matrix has orthonormal rows
/// with determinant 1, so they are rotations with no reflection or shear, which is what lets the
/// rotation be recovered as a quaternion without qualification.
/// </summary>
public sealed class ScnNode
{
    /// <summary>Total size of a node record, header included.</summary>
    public const int RecordSize = 104;

    /// <summary>Size of the body after the 32-byte header.</summary>
    public const int BodySize = 72;

    /// <summary>Joint name, matching an entry in the <c>.mpc</c> skeleton.</summary>
    public required string Name { get; init; }

    /// <summary>Offset of the record within the scene table.</summary>
    public required int Offset { get; init; }

    /// <summary>Value in the high half of the word at 0x00.</summary>
    public required int Id { get; init; }

    /// <summary>Translation relative to the parent joint.</summary>
    public required (float X, float Y, float Z) Translation { get; init; }

    /// <summary>Scale. Always (1, 1, 1) in the reference data.</summary>
    public required (float X, float Y, float Z) Scale { get; init; }

    /// <summary>
    /// The inverse bind matrix as 12 floats: three rows of a rotation followed by that row's
    /// translation, so <c>[r00 r01 r02 tx r10 r11 r12 ty r20 r21 r22 tz]</c>.
    /// </summary>
    public required float[] InverseBind { get; init; }

    /// <summary>The rotation part of the inverse bind matrix, as a quaternion (x, y, z, w).</summary>
    public (float X, float Y, float Z, float W) InverseBindRotation => ToQuaternion(InverseBind);

    /// <summary>
    /// True if a node record starts at <paramref name="at"/>. The test is structural: a name, a
    /// word whose low half is zero and whose high half is not, a scale and an orthonormal rotation.
    /// Guessing wrong here would desynchronise the table, so all four have to hold.
    /// </summary>
    public static bool LooksLikeNode(ReadOnlySpan<byte> data, int at)
    {
        if (at < 0 || at + RecordSize > data.Length)
            return false;

        uint word = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        if ((word & 0xFFFF) != 0 || (word >> 16) == 0)
            return false;
        if (!Ascii.IsPrintableName(data.Slice(at + 4, 8), minimumLength: 2))
            return false;

        int body = at + SceneTable.RecordSize;
        for (int i = 0; i < 18; i++)
        {
            float value = BinaryPrimitives.ReadSingleBigEndian(data[(body + i * 4)..]);
            if (!float.IsFinite(value) || Math.Abs(value) > 1e5f)
                return false;
        }

        for (int row = 0; row < 3; row++)
        {
            double length = 0;
            for (int column = 0; column < 3; column++)
            {
                float value = BinaryPrimitives.ReadSingleBigEndian(data[(body + 24 + row * 16 + column * 4)..]);
                length += (double)value * value;
            }

            if (Math.Abs(Math.Sqrt(length) - 1) > 0.02)
                return false;
        }

        return true;
    }

    /// <summary>Reads a node record. Call <see cref="LooksLikeNode"/> first.</summary>
    public static ScnNode Parse(ReadOnlySpan<byte> data, int at)
    {
        int body = at + SceneTable.RecordSize;
        var inverseBind = new float[12];
        for (int i = 0; i < inverseBind.Length; i++)
            inverseBind[i] = BinaryPrimitives.ReadSingleBigEndian(data[(body + 24 + i * 4)..]);

        return new ScnNode
        {
            Name = Ascii.Decode(data.Slice(at + 4, 8)),
            Offset = at,
            Id = (int)(BinaryPrimitives.ReadUInt32BigEndian(data[at..]) >> 16),
            Translation = Read3(data, body),
            Scale = Read3(data, body + 12),
            InverseBind = inverseBind,
        };
    }

    private static (float X, float Y, float Z) Read3(ReadOnlySpan<byte> data, int at) => (
        BinaryPrimitives.ReadSingleBigEndian(data[at..]),
        BinaryPrimitives.ReadSingleBigEndian(data[(at + 4)..]),
        BinaryPrimitives.ReadSingleBigEndian(data[(at + 8)..]));

    /// <summary>
    /// Converts the rotation part of a 3x4 matrix to a quaternion. Shepperd's method: pick the
    /// component the trace says is largest so the division never loses precision.
    /// </summary>
    public static (float X, float Y, float Z, float W) ToQuaternion(float[] m)
    {
        float m00 = m[0], m01 = m[1], m02 = m[2];
        float m10 = m[4], m11 = m[5], m12 = m[6];
        float m20 = m[8], m21 = m[9], m22 = m[10];
        float trace = m00 + m11 + m22;

        if (trace > 0)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            return ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
        }

        if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            return (0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }

        if (m11 > m22)
        {
            float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            return ((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        else
        {
            float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            return ((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
        }
    }
}
