using System.Text;

namespace PacTool.Formats;

/// <summary>Helpers for the fixed-width, NUL-padded ASCII name fields these formats are full of.</summary>
public static class Ascii
{
    /// <summary>
    /// Reads a name field: everything up to the first NUL, with bytes outside printable ASCII
    /// replaced by '?'. A field that is entirely full is not NUL-terminated, so the whole span
    /// is the name.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
            end = field.Length;

        var text = new StringBuilder(end);
        for (int i = 0; i < end; i++)
            text.Append(field[i] is >= 0x20 and <= 0x7E ? (char)field[i] : '?');

        return text.ToString();
    }

    /// <summary>
    /// True if the field holds at least <paramref name="minimumLength"/> characters, nothing but
    /// printable ASCII, and only NUL padding after the terminator.
    /// </summary>
    public static bool IsPrintableName(ReadOnlySpan<byte> field, int minimumLength = 1)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
            end = field.Length;
        if (end < minimumLength)
            return false;

        for (int i = 0; i < end; i++)
        {
            if (field[i] is < 0x20 or > 0x7E)
                return false;
        }

        // Trailing bytes after the terminator must be padding, otherwise this is not a name field.
        for (int i = end + 1; i < field.Length; i++)
        {
            if (field[i] != 0)
                return false;
        }

        return true;
    }

    /// <summary>True if every byte in <paramref name="data"/> is zero.</summary>
    public static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    /// <summary>Renders bytes as space-separated hex, for the diagnostic dumps.</summary>
    public static string Hex(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(data.Length * 3);
        foreach (byte b in data)
        {
            if (text.Length > 0)
                text.Append(' ');
            text.Append(b.ToString("x2"));
        }

        return text.ToString();
    }
}
