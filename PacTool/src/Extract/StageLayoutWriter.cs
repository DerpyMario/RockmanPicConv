using System.Globalization;
using System.Text;
using PacTool.Formats;

namespace PacTool.Extract;

/// <summary>
/// Draws a stage area as Wavefront OBJ: every collision box, placed where the layout puts it.
///
/// This is the check on the whole chain as much as it is an output. The directory gives each asset
/// a box in its own space and the layout gives each placement a position; putting the two together
/// should produce a stage that tiles - blocks meeting edge to edge on the ten-unit lattice the
/// data is built on - and it does. Nothing else would.
///
/// The result is flat, because the collision data is: <c>map.dat</c> keeps every placement at z = 0
/// and the boxes are two-dimensional. Each box becomes one quad, and each placement's boxes are
/// grouped under the asset's name so a viewer can pick them apart.
/// </summary>
public static class StageLayoutWriter
{
    /// <summary>Writes one area's collision boxes. Returns false when the area has none to draw.</summary>
    public static bool Save(StageArea area, IReadOnlyList<DataDirectoryEntry> entries, string path)
    {
        string? text = Build(area, entries);
        if (text is null)
            return false;

        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }

    /// <summary>Builds the OBJ text, or null when nothing in the area has a box.</summary>
    public static string? Build(StageArea area, IReadOnlyList<DataDirectoryEntry> entries)
    {
        var text = new StringBuilder();
        var faces = new StringBuilder();
        int vertices = 0;
        string? group = null;

        foreach (StagePlacement placement in area.Placements)
        {
            if (placement.Entry >= entries.Count)
                continue;

            DataDirectoryEntry entry = entries[placement.Entry];
            foreach (DataDirectoryBox box in entry.Boxes)
            {
                if (!IsDrawable(box, placement))
                    continue;

                string name = $"{Safe(entry.Name)}_kind{box.Kind}";
                if (name != group)
                {
                    faces.AppendLine($"g {name}");
                    group = name;
                }

                float left = placement.X + box.Left;
                float right = placement.X + box.Right;
                float bottom = placement.Y + box.Bottom;
                float top = placement.Y + box.Top;

                text.AppendLine($"v {F(left)} {F(bottom)} {F(placement.Z)}");
                text.AppendLine($"v {F(right)} {F(bottom)} {F(placement.Z)}");
                text.AppendLine($"v {F(right)} {F(top)} {F(placement.Z)}");
                text.AppendLine($"v {F(left)} {F(top)} {F(placement.Z)}");
                faces.AppendLine($"f {vertices + 1} {vertices + 2} {vertices + 3} {vertices + 4}");
                vertices += 4;
            }
        }

        if (vertices == 0)
            return null;

        var output = new StringBuilder();
        output.AppendLine($"# area {area.Index}: {vertices / 4} collision box(es) from " +
                          $"{area.Placements.Count} placement(s)");
        output.AppendLine("# Boxes come from the directory and positions from the layout; both are as stored.");
        output.Append(text);
        output.Append(faces);
        return output.ToString();
    }

    /// <summary>
    /// A box is only drawn when both it and the placement are finite and sanely sized. An area
    /// whose records were left uninitialised would otherwise contribute quads a million units
    /// across and swamp everything real.
    /// </summary>
    private static bool IsDrawable(DataDirectoryBox box, StagePlacement placement) =>
        float.IsFinite(box.Width) && float.IsFinite(box.Height) &&
        float.IsFinite(placement.X) && float.IsFinite(placement.Y) && float.IsFinite(placement.Z) &&
        box.Width is > 0 and < 10_000 && box.Height is > 0 and < 10_000 &&
        Math.Abs(placement.X) < 1e6f && Math.Abs(placement.Y) < 1e6f && Math.Abs(placement.Z) < 1e6f;

    private static string Safe(string name)
    {
        if (name.Length == 0)
            return "unnamed";

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');

        return sb.ToString();
    }

    private static string F(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}
