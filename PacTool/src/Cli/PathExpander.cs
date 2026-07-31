namespace PacTool.Cli;

/// <summary>
/// Turns whatever the command line gave us into a list of files to work on.
///
/// Three things have to be handled that a bare <c>File.ReadAllBytes</c> does not. A directory
/// stands for the files inside it, because dragging a folder onto the tool or pointing it at an
/// extracted disc should just work. A pattern like <c>Stage*.pac</c> has to be expanded here,
/// because the Windows shell does not do it and passes the pattern through unchanged. And an
/// expansion is filtered by extension, because a game directory holds executables and scripts that
/// nobody meant to convert - while a file named outright is always taken, whatever it is called.
/// </summary>
public static class PathExpander
{
    /// <summary>Extensions <c>decode</c> and <c>info</c> pick up when expanding a directory.</summary>
    public static readonly string[] ContentExtensions =
        [".pac", ".pcp", ".scn", ".mpc", ".bmd", ".bdl", ".bti", ".pic", ".dat", ".tpl"];

    /// <summary>Extensions the archive commands pick up when expanding a directory.</summary>
    public static readonly string[] ArchiveExtensions = [".pac"];

    /// <summary>What <see cref="Expand"/> found, and what it could not.</summary>
    public sealed class Result
    {
        /// <summary>Files to work on, in the order the inputs named them.</summary>
        public List<string> Files { get; } = [];

        /// <summary>Inputs that matched nothing at all.</summary>
        public List<string> Missing { get; } = [];

        /// <summary>Directories that were expanded, for reporting.</summary>
        public int Directories { get; set; }
    }

    /// <summary>
    /// Expands <paramref name="inputs"/>. Directories contribute the files inside them whose
    /// extension is in <paramref name="extensions"/> - or every file when <paramref name="all"/> is
    /// set - going into subdirectories only when <paramref name="recurse"/> is set.
    /// </summary>
    public static Result Expand(IEnumerable<string> inputs, IReadOnlyList<string> extensions,
                                bool recurse = false, bool all = false)
    {
        var result = new Result();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string input in inputs)
        {
            int before = result.Files.Count;

            if (Directory.Exists(input))
            {
                result.Directories++;
                AddDirectory(input, extensions, recurse, all, result, seen);
            }
            else if (File.Exists(input))
            {
                Add(input, result, seen);
            }
            else if (HasWildcard(input))
            {
                AddPattern(input, result, seen);
            }

            if (result.Files.Count == before)
                result.Missing.Add(input);
        }

        return result;
    }

    private static void AddDirectory(string directory, IReadOnlyList<string> extensions, bool recurse,
                                     bool all, Result result, HashSet<string> seen)
    {
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", option);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (all || Matches(file, extensions))
                Add(file, result, seen);
        }
    }

    private static void AddPattern(string pattern, Result result, HashSet<string> seen)
    {
        string directory = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : ".";
        string leaf = Path.GetFileName(pattern);
        if (!Directory.Exists(directory) || HasWildcard(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, leaf)
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            Add(file, result, seen);
        }
    }

    private static void Add(string file, Result result, HashSet<string> seen)
    {
        // The same file can arrive twice - named outright and again through its directory - and
        // converting it twice would only overwrite the first pass with identical output.
        string key = TryFullPath(file);
        if (seen.Add(key))
            result.Files.Add(file);
    }

    private static bool Matches(string file, IReadOnlyList<string> extensions)
    {
        string extension = Path.GetExtension(file);
        foreach (string candidate in extensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasWildcard(string path) => path.Contains('*') || path.Contains('?');

    private static string TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
