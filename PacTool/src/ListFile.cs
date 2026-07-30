namespace PacTool;

/// <summary>
/// Parses <c>.lst</c> files in the format the original <c>RockmanFilePack</c> tool consumed:
/// one path per line, relative to the directory holding the list. A line naming another
/// <c>.lst</c> is inlined recursively rather than nested, which is how <c>StageData/Fixed.pac</c>
/// ends up with 89 members from a 17-line list.
///
/// Blank lines and lines starting with <c>#</c> are skipped. Retail lists use neither, so this
/// is an addition for hand-written lists.
/// </summary>
public static class ListFile
{
    /// <summary>Reads a list file and resolves it to a flat, ordered set of members.</summary>
    public static List<PacSource> Resolve(string listPath)
    {
        var sources = new List<PacSource>();
        var active = new List<string>();
        ResolveInto(Path.GetFullPath(listPath), sources, active);
        return sources;
    }

    private static void ResolveInto(string listPath, List<PacSource> sources, List<string> active)
    {
        if (active.Any(p => string.Equals(p, listPath, StringComparison.OrdinalIgnoreCase)))
            throw new PacFormatException(
                $"List files include each other in a cycle: {string.Join(" -> ", active.Append(listPath).Select(Path.GetFileName))}");

        if (!File.Exists(listPath))
            throw new PacFormatException($"List file not found: {listPath}");

        active.Add(listPath);
        string baseDirectory = Path.GetDirectoryName(listPath) ?? ".";
        string[] lines = File.ReadAllLines(listPath);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Retail lists were authored on Windows and use backslashes.
            string relative = line.Replace('\\', Path.DirectorySeparatorChar)
                                  .Replace('/', Path.DirectorySeparatorChar);
            string resolved = Path.GetFullPath(Path.Combine(baseDirectory, relative));

            if (Path.GetExtension(resolved).Equals(".lst", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(resolved))
                    throw new PacFormatException(
                        $"{listPath}({i + 1}): nested list '{line}' not found at {resolved}");
                ResolveInto(resolved, sources, active);
                continue;
            }

            if (!File.Exists(resolved))
                throw new PacFormatException($"{listPath}({i + 1}): '{line}' not found at {resolved}");

            sources.Add(PacSource.FromFile(resolved));
        }

        active.RemoveAt(active.Count - 1);
    }
}
