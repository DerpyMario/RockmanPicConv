using System.Buffers;
using System.Text;
using System.Text.Json;
using PacTool.Extract;
using PacTool.Formats;

namespace PacTool;

internal static class Program
{
    private const string Usage = """
        pactool - unpacker, repacker and content converter for CAPR (.pac) archives
                  Mega Man Network Transmission / Rockman.EXE Transmission (GameCube)

        Usage:
          pactool list   <archive.pac> [--json]
          pactool unpack <archive.pac> [-o <dir>] [--decode] [--no-manifest] [--strict]
          pactool pack   <input> <archive.pac> [--align <n>] [--no-align]
          pactool verify <archive.pac> [<archive.pac> ...] [--strict]
          pactool decode <file> [<file> ...] [-o <dir>] [--mips] [--raw] [--flat]
          pactool info   <file> [<file> ...]

        pack accepts three kinds of <input>:
          pac.json    a manifest written by unpack. Rebuilds byte for byte: member order,
                      stored names and reserved header fields all come from the manifest.
          *.lst       a RockmanFilePack list: one path per line relative to the list, with
                      nested .lst files inlined recursively. The stored name is the base name.
          <dir>       every file directly inside the directory, ordinal sorted by name.

        decode and info look inside the payloads instead of treating them as opaque:
          .pac         every member, each into its own directory
          .pcp / .scn  Picture Pack textures to PNG; a .scn also yields its scene table
                       and its GX display list geometry as Wavefront OBJ
          .mpc         skeleton hierarchy as text, plus the packed mesh data
          map/bg/enemy.dat   stage object, background and spawn directories as text
          .bmd / .bdl  J3D models: section inventory, scene graph, and TEX1 textures to PNG
          .bti         a standalone GameCube texture to PNG
          .pic         Softimage PIC source art to PNG
        Yaz0-compressed input is decompressed first. Anything unrecognised is copied out as is.

        Options:
          -o <dir>     output directory (default: ./<input name without extension>)
          --decode     unpack also converts each member's contents, into <dir>/decoded/
          --mips       write every mip level, not only the base one
          --raw        keep the stored bytes too: each texture's, and each display list's
          --flat       decode writes straight into -o rather than one directory per input
          --align <n>  pad each payload up to a multiple of n bytes (default 32)
          --no-align   store payloads at their exact length
          --no-manifest  extract payloads only, skip pac.json and the .lst
          --strict     treat structural oddities as errors rather than warnings
          --json       machine-readable output for list

        Archive layout: a flat chain of 32-byte big-endian headers, each followed by its
        payload; there is no central directory and the chain ends at end of file.
          0x00 char[4] "CAPR"   0x04 u32 size   0x08 u32 rsv0
          0x0C u32 rsv1         0x10 char[16] name (NUL-padded)   0x20 payload
        """;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (PacFormatException ex)
        {
            Error(ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            Error(ex.Message);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Error(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var options = new Options();
        var positional = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-o" or "--output":
                    if (++i >= args.Length)
                        return UsageError("-o needs a directory.");
                    options.Output = args[i];
                    break;
                case "--align":
                    if (++i >= args.Length || !int.TryParse(args[i], out int alignment) || alignment < 1)
                        return UsageError("--align needs a positive integer.");
                    options.Alignment = alignment;
                    break;
                case "--no-align":
                    options.Alignment = 1;
                    break;
                case "--no-manifest":
                    options.NoManifest = true;
                    break;
                case "--strict":
                    options.Strict = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--decode" or "--convert":
                    options.Decode = true;
                    break;
                case "--mips":
                    options.AllMips = true;
                    break;
                case "--raw":
                    options.KeepRaw = true;
                    break;
                case "--flat":
                    options.Flat = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        return UsageError($"Unknown option '{arg}'.");
                    positional.Add(arg);
                    break;
            }
        }

        return args[0] switch
        {
            "list" => List(positional, options),
            "unpack" or "extract" => Unpack(positional, options),
            "pack" or "repack" => Pack(positional, options),
            "verify" or "test" => Verify(positional, options),
            "decode" or "convert" => Decode(positional, options),
            "info" or "describe" => Info(positional),
            _ => UsageError($"Unknown command '{args[0]}'."),
        };
    }

    private static int List(List<string> positional, Options options)
    {
        if (positional.Count != 1)
            return UsageError("list takes exactly one archive.");

        using var archive = PacArchive.Open(positional[0], options.Strict);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(BuildManifest(archive, null), JsonOptions));
            return 0;
        }

        Console.WriteLine($"{archive.SourceName}");
        Console.WriteLine($"  {archive.Length:N0} bytes, {archive.Members.Count} member(s)");
        Console.WriteLine();
        Console.WriteLine("    #  Offset      Size          Name");
        Console.WriteLine("  ---  ----------  ------------  ----------------");
        foreach (PacMember member in archive.Members)
            Console.WriteLine($"  {member.Index,3}  0x{member.DataOffset:X8}  {member.Size,12:N0}  {member.Name}");

        ReportWarnings(archive);
        return 0;
    }

    private static int Unpack(List<string> positional, Options options)
    {
        if (positional.Count != 1)
            return UsageError("unpack takes exactly one archive.");

        string archivePath = positional[0];
        using var archive = PacArchive.Open(archivePath, options.Strict);

        string stem = Path.GetFileNameWithoutExtension(archivePath);
        string outputDirectory = options.Output ?? (stem.Length > 0 ? stem : "unpacked");
        Directory.CreateDirectory(outputDirectory);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifest = BuildManifest(archive, archivePath);
        bool renamed = false;

        foreach (PacMember member in archive.Members)
        {
            string fileName = Deduplicate(SanitizeFileName(member.Name, member.Index), used);
            if (!fileName.Equals(member.Name, StringComparison.Ordinal))
                renamed = true;

            string destination = Path.Combine(outputDirectory, fileName);
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                archive.CopyMemberTo(member, output);
            }

            manifest.Members[member.Index].File = fileName;
            Console.WriteLine($"  {member.Size,12:N0}  {fileName}");
        }

        if (!options.NoManifest)
        {
            string manifestPath = Path.Combine(outputDirectory, "pac.json");
            manifest.Save(manifestPath);

            string listPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(archivePath) + ".lst");
            File.WriteAllText(listPath, string.Concat(manifest.Members.Select(m => m.File + "\r\n")), Encoding.ASCII);

            Console.WriteLine();
            Console.WriteLine($"Extracted {archive.Members.Count} member(s) to {Path.GetFullPath(outputDirectory)}");
            Console.WriteLine($"  pac.json  rebuild with: pactool pack \"{manifestPath}\" \"{Path.GetFileName(archivePath)}\"");
            Console.WriteLine($"  {Path.GetFileName(listPath),-9} RockmanFilePack-style list");

            if (renamed)
                Warn("some members were renamed to be valid, unique file names; " +
                     "pack from pac.json (not the .lst) to restore the original names.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"Extracted {archive.Members.Count} member(s) to {Path.GetFullPath(outputDirectory)}");
        }

        if (options.Decode)
        {
            // Converted output lives in its own subdirectory so that `pack` on the unpack
            // directory still sees exactly the members it extracted, and nothing else.
            string decodedDirectory = Path.Combine(outputDirectory, "decoded");
            Console.WriteLine();
            var result = new ExtractResult();
            foreach (PacMember member in archive.Members)
            {
                string memberDirectory = Path.Combine(decodedDirectory, ContentExtractor.SafeName(member.Name));
                result.Add(ContentExtractor.Extract(member.Name, archive.ReadMember(member),
                                                    memberDirectory, Options.ExtractOptions(options)));
            }

            ReportExtraction(result, decodedDirectory);
        }

        ReportWarnings(archive);
        return 0;
    }

    private static int Decode(List<string> positional, Options options)
    {
        if (positional.Count == 0)
            return UsageError("decode takes at least one file.");
        if (options.Flat && positional.Count > 1 && options.Output is null)
            return UsageError("--flat with several inputs needs an explicit -o directory.");

        var total = new ExtractResult();
        int failed = 0;

        foreach (string path in positional)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string outputDirectory = options.Output is null
                ? (stem.Length > 0 ? stem : "decoded")
                : options.Flat ? options.Output : Path.Combine(options.Output, stem);

            try
            {
                byte[] data = File.ReadAllBytes(path);
                total.Add(ContentExtractor.Extract(Path.GetFileName(path), data, outputDirectory,
                                                   Options.ExtractOptions(options)));
            }
            catch (PacFormatException ex)
            {
                failed++;
                Error($"{path}: {ex.Message}");
            }
        }

        Console.WriteLine();
        ReportExtraction(total, options.Output ?? ".");
        return failed == 0 ? 0 : 1;
    }

    private static int Info(List<string> positional)
    {
        if (positional.Count == 0)
            return UsageError("info takes at least one file.");

        int failed = 0;

        foreach (string path in positional)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                Console.WriteLine($"{path}  ({data.Length:N0} bytes)");
                Describe(Path.GetFileName(path), data, "  ");
            }
            catch (PacFormatException ex)
            {
                failed++;
                Error($"{path}: {ex.Message}");
            }

            Console.WriteLine();
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Prints what a blob is and what it holds, without writing anything.</summary>
    private static void Describe(string name, byte[] data, string indent)
    {
        ContentKind kind;
        try
        {
            kind = ContentSniffer.Identify(name, data, out bool compressed);
            if (compressed)
            {
                data = Yaz0.Decompress(data, name);
                Console.WriteLine($"{indent}Yaz0 compressed, {data.Length:N0} bytes decompressed");
            }
        }
        catch (PacFormatException ex)
        {
            Console.WriteLine($"{indent}{ex.Message}");
            return;
        }

        try
        {
            switch (kind)
            {
                case ContentKind.Archive:
                {
                    using var stream = new MemoryStream(data, writable: false);
                    using var archive = PacArchive.Open(stream, name);
                    Console.WriteLine($"{indent}CAPR archive, {archive.Members.Count} member(s)");
                    foreach (PacMember member in archive.Members)
                    {
                        Console.WriteLine($"{indent}  {member.Name,-18} {member.Size,10:N0} B");
                        Describe(member.Name, archive.ReadMember(member), indent + "    ");
                    }

                    return;
                }

                case ContentKind.PicturePack:
                {
                    PicturePack pack = PicturePack.Parse(data, name);
                    Console.WriteLine($"{indent}Picture Pack, {pack.Textures.Count} texture(s)" +
                                      (pack.Scene is null ? "" : $", scene table {pack.Scene.Raw.Length:N0} B"));
                    foreach (PicturePackTexture texture in pack.Textures)
                        Console.WriteLine($"{indent}  {texture.Name,-16} {texture.Describe()}");
                    foreach (string warning in pack.Warnings)
                        Console.WriteLine($"{indent}  note: {warning}");
                    return;
                }

                case ContentKind.MpcModel:
                {
                    MpcModel model = MpcModel.Parse(data, name);
                    int parts = model.Nodes.Count(n => n.Category == MpcNodeCategory.Part);
                    Console.WriteLine($"{indent}MPC model, root '{model.RootName}', {model.Nodes.Count} node(s) " +
                                      $"({parts} with a mesh), {model.MeshData.Length:N0} B mesh data");
                    return;
                }

                case ContentKind.DataDirectory:
                {
                    DataDirectory directory = DataDirectory.Parse(data, name);
                    int spawns = directory.Entries.Sum(e => e.Spawns.Count);
                    Console.WriteLine($"{indent}{directory.Kind} directory, {directory.Entries.Count} entr" +
                                      $"{(directory.Entries.Count == 1 ? "y" : "ies")}" +
                                      (spawns > 0 ? $", {spawns} spawn(s)" : "") +
                                      (directory.Trailing.Length > 0 ? $", {directory.Trailing.Length:N0} B level data" : ""));
                    return;
                }

                case ContentKind.J3dModel:
                {
                    J3dModel model = J3dModel.Parse(data, name);
                    Console.WriteLine($"{indent}J3D {model.Variant}, {model.Sections.Count} section(s): " +
                                      string.Join(", ", model.Sections.Select(s => s.Magic)));
                    Console.WriteLine($"{indent}  {model.JointNames.Count} joint(s), {model.MaterialNames.Count} material(s), " +
                                      $"{model.Textures.Count} texture(s)");
                    foreach (BtiTexture texture in model.Textures)
                        Console.WriteLine($"{indent}  {texture.Name,-16} {texture.Describe()}");
                    foreach (string warning in model.Warnings)
                        Console.WriteLine($"{indent}  note: {warning}");
                    return;
                }

                case ContentKind.BtiTexture:
                    Console.WriteLine($"{indent}BTI texture, {BtiTexture.Parse(data, 0, name).Describe()}");
                    return;

                case ContentKind.SoftimagePic:
                {
                    SoftimagePic pic = SoftimagePic.Parse(data, name);
                    Console.WriteLine($"{indent}{pic.Describe()}");
                    if (pic.Comment.Length > 0)
                        Console.WriteLine($"{indent}  comment: {pic.Comment}");
                    return;
                }

                default:
                    Console.WriteLine($"{indent}unrecognised, {data.Length:N0} bytes");
                    return;
            }
        }
        catch (PacFormatException ex)
        {
            Console.WriteLine($"{indent}{ContentSniffer.Describe(kind)}, but parsing failed: {ex.Message}");
        }
    }

    private static void ReportExtraction(ExtractResult result, string outputDirectory)
    {
        Console.WriteLine($"Wrote {result.FilesWritten:N0} file(s) to {Path.GetFullPath(outputDirectory)}");
        Console.WriteLine($"  {result.ImagesWritten:N0} image(s) and {result.MeshesWritten:N0} mesh(es) decoded, "
                          + $"{result.Unrecognised:N0} item(s) copied verbatim");
        if (result.Warnings.Count > 0)
            Console.WriteLine($"  {result.Warnings.Count:N0} note(s); see the lines above");
    }

    private static int Pack(List<string> positional, Options options)
    {
        if (positional.Count != 2)
            return UsageError("pack takes an input and an output archive.");

        string input = positional[0];
        string output = positional[1];
        int alignment = options.Alignment ?? PacFormat.DefaultAlignment;
        List<PacSource> sources;

        if (Directory.Exists(input))
        {
            sources = Directory.GetFiles(input)
                .Where(p => !Path.GetFileName(p).Equals("pac.json", StringComparison.OrdinalIgnoreCase))
                .Where(p => !Path.GetExtension(p).Equals(".lst", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Select(p => PacSource.FromFile(p))
                .ToList();
        }
        else if (Path.GetExtension(input).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            sources = PacManifest.Resolve(input, out int manifestAlignment);
            alignment = options.Alignment ?? manifestAlignment;
        }
        else if (Path.GetExtension(input).Equals(".lst", StringComparison.OrdinalIgnoreCase))
        {
            sources = ListFile.Resolve(input);
        }
        else
        {
            return UsageError($"'{input}' is not a directory, a .json manifest or a .lst list.");
        }

        if (sources.Count == 0)
            throw new PacFormatException($"{input}: nothing to pack.");

        PacBuilder.Build(output, sources, alignment);

        long total = new FileInfo(output).Length;
        foreach (PacSource source in sources)
        {
            long padded = PacBuilder.Align(source.Length, alignment);
            string note = padded != source.Length ? $"  (+{padded - source.Length} pad)" : "";
            Console.WriteLine($"  {padded,12:N0}  {source.Name}{note}");
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {Path.GetFullPath(output)}");
        Console.WriteLine($"  {total:N0} bytes, {sources.Count} member(s), alignment {alignment}");
        return 0;
    }

    private static int Verify(List<string> positional, Options options)
    {
        if (positional.Count == 0)
            return UsageError("verify takes at least one archive.");

        int failed = 0;
        foreach (string path in positional)
        {
            try
            {
                using var archive = PacArchive.Open(path, options.Strict);

                // Rebuild from the archive's own members and compare. This exercises the header
                // encoder, the name encoder and member ordering against known-good bytes.
                var sources = archive.Members
                    .Select(m => new PacSource
                    {
                        Name = m.Name,
                        Length = m.Size,
                        Origin = $"{path}#{m.Index}",
                        Reserved0 = m.Reserved0,
                        Reserved1 = m.Reserved1,
                        Open = () => new MemoryStream(archive.ReadMember(m), writable: false),
                    })
                    .ToList();

                using var rebuilt = new MemoryStream((int)archive.Length);
                PacBuilder.Build(rebuilt, sources, alignment: 1);

                string verdict = SameBytes(path, rebuilt) ? "ok" : "MISMATCH";
                if (verdict != "ok")
                    failed++;

                Console.WriteLine($"  {verdict,-8}  {archive.Members.Count,4} member(s)  {archive.Length,12:N0} bytes  {path}");
                foreach (string warning in archive.Warnings)
                    Console.WriteLine($"            note: {warning}");
            }
            catch (PacFormatException ex)
            {
                failed++;
                Console.WriteLine($"  FAILED    {path}");
                Console.WriteLine($"            {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{positional.Count - failed}/{positional.Count} archive(s) round-tripped byte for byte.");
        return failed == 0 ? 0 : 1;
    }

    private static bool SameBytes(string path, MemoryStream rebuilt)
    {
        using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (original.Length != rebuilt.Length)
            return false;

        rebuilt.Position = 0;
        byte[] a = new byte[1 << 16];
        byte[] b = new byte[1 << 16];
        int got;
        while ((got = original.Read(a, 0, a.Length)) > 0)
        {
            rebuilt.ReadExactly(b, 0, got);
            if (!a.AsSpan(0, got).SequenceEqual(b.AsSpan(0, got)))
                return false;
        }

        return true;
    }

    private static PacManifest BuildManifest(PacArchive archive, string? source) => new()
    {
        Source = source is null ? null : Path.GetFileName(source),
        Align = PacFormat.DefaultAlignment,
        Members = archive.Members.Select(m => new PacManifestEntry
        {
            Name = m.Name,
            File = m.Name,
            Size = m.Size,
            Reserved0 = m.Reserved0,
            Reserved1 = m.Reserved1,
        }).ToList(),
    };

    /// <summary>
    /// Turns a stored member name into a file name that is safe to create. Names come from the
    /// archive, so they are treated as untrusted: separators and traversal are stripped rather
    /// than allowed to escape the output directory.
    /// </summary>
    private static string SanitizeFileName(string name, int index)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(InvalidFileNameChars.Contains(c) ? '_' : c);

        string result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0 || result is "." or "..")
            return $"member_{index:D3}";

        if (ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(result)))
            result = "_" + result;

        return result;
    }

    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string Deduplicate(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName))
            return fileName;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem}~{n}{extension}";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    private static void ReportWarnings(PacArchive archive)
    {
        foreach (string warning in archive.Warnings)
            Warn(warning);
    }

    private static void Warn(string message) => Console.Error.WriteLine($"warning: {message}");

    private static void Error(string message) => Console.Error.WriteLine($"error: {message}");

    private static int UsageError(string message)
    {
        Error(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private sealed class Options
    {
        public string? Output { get; set; }
        public int? Alignment { get; set; }
        public bool NoManifest { get; set; }
        public bool Strict { get; set; }
        public bool Json { get; set; }
        public bool Decode { get; set; }
        public bool AllMips { get; set; }
        public bool KeepRaw { get; set; }
        public bool Flat { get; set; }

        public static ExtractOptions ExtractOptions(Options options) => new()
        {
            AllMips = options.AllMips,
            KeepRaw = options.KeepRaw,
            Log = Console.WriteLine,
        };
    }
}
