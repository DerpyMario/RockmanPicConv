using System.Buffers;
using System.Text;
using System.Text.Json;
using PacTool.Cli;
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
          pactool unpack <input> [<input> ...] [-o <dir>] [--decode] [--no-manifest] [--strict]
          pactool pack   <input> <archive.pac> [--align <n>] [--no-align]
          pactool verify <input> [<input> ...] [--strict]
          pactool decode <input> [<input> ...] [-o <dir>] [--mips] [--raw] [--tpl] [--flat]
          pactool info   <input> [<input> ...]

        Every <input> may be a file, a directory or a wildcard such as "Stage*.pac". A directory
        contributes the files inside it that the command handles; add --recurse for its
        subdirectories too, or --all to take every file in it whatever its name.

        Drag and drop: dropping files or folders onto the executable runs them without a command.
        Archives are unpacked and converted, everything else is converted, each beside its input,
        and the window waits for a key at the end so the report can be read.

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
          .mpc         joint hierarchy and motion list as text, plus a .bck per animation
          map/bg/enemy.dat   stage object, background and spawn directories as text
          .bmd / .bdl  J3D models: section inventory, scene graph, and TEX1 textures to PNG
          .bti         a standalone GameCube texture to PNG
          .pic         Softimage PIC source art to PNG
        Yaz0-compressed input is decompressed first. Anything unrecognised is copied out as is.

        Options:
          -o <dir>     output directory (default: ./<input name without extension>)
          -r, --recurse  expand directories into their subdirectories as well
          --all        take every file in an expanded directory, not only the known kinds
          --decode     unpack also converts each member's contents, into <dir>/decoded/
          --mips       write every mip level, not only the base one
          --raw        keep the stored bytes too: each texture's, and each display list's
          --tpl        also write the textures as a .tpl texture bank
          --no-j3d     skip the .bmd and .bck export
          --bdl        write .bdl rather than .bmd
          --motion-csv also write each motion's frames as CSV
          --flat       decode writes straight into -o rather than one directory per input
          --align <n>  pad each payload up to a multiple of n bytes (default 32)
          --no-align   store payloads at their exact length
          --no-manifest  extract payloads only, skip pac.json and the .lst
          --strict     treat structural oddities as errors rather than warnings
          --json       machine-readable output for list
          --pause      wait for a key before exiting (automatic when dropped onto the executable)
          --no-pause   never wait, even then

        Archive layout: a flat chain of 32-byte big-endian headers, each followed by its
        payload; there is no central directory and the chain ends at end of file.
          0x00 char[4] "CAPR"   0x04 u32 size   0x08 u32 rsv0
          0x0C u32 rsv1         0x10 char[16] name (NUL-padded)   0x20 payload
        """;

    private static int Main(string[] args)
    {
        // Whether to wait at the end is decided before anything can fail, so a run that ends in an
        // error still leaves the message on screen rather than closing over it.
        bool dropped = args.Length > 0 && !IsCommand(args[0]) && !args[0].StartsWith('-');
        bool pause = dropped && ConsoleSession.OwnsConsole();

        try
        {
            return Run(args, ref pause);
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
        finally
        {
            if (pause)
                ConsoleSession.Pause();
        }
    }

    private static bool IsCommand(string arg) => arg is
        "list" or "unpack" or "extract" or "pack" or "repack" or "verify" or "test" or
        "decode" or "convert" or "info" or "describe";

    private static int Run(string[] args, ref bool pause)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        // A run with no command at all is a drag and drop: the paths are the whole argument list,
        // and what to do with each is decided from what it turns out to be.
        bool dropped = !IsCommand(args[0]) && !args[0].StartsWith('-');
        var options = new Options();
        var positional = new List<string>();

        for (int i = dropped ? 0 : 1; i < args.Length; i++)
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
                case "--no-j3d":
                    options.NoJ3d = true;
                    break;
                case "--bdl":
                    options.Bdl = true;
                    break;
                case "--motion-csv":
                    options.MotionTables = true;
                    break;
                case "--tpl":
                    options.Tpl = true;
                    break;
                case "-r" or "--recurse" or "--recursive":
                    options.Recurse = true;
                    break;
                case "--all":
                    options.All = true;
                    break;
                case "--pause":
                    options.Pause = true;
                    break;
                case "--no-pause":
                    options.Pause = false;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        return UsageError($"Unknown option '{arg}'.");
                    positional.Add(arg);
                    break;
            }
        }

        if (options.Pause is { } wanted)
            pause = wanted;

        if (dropped)
            return Dropped(positional, options);

        return args[0] switch
        {
            "list" => List(positional, options),
            "unpack" or "extract" => Unpack(positional, options),
            "pack" or "repack" => Pack(positional, options),
            "verify" or "test" => Verify(positional, options),
            "decode" or "convert" => Decode(positional, options),
            "info" or "describe" => Info(positional, options),
            _ => UsageError($"Unknown command '{args[0]}'."),
        };
    }

    /// <summary>
    /// Files and folders dropped onto the executable, with no command to say what to do with them.
    ///
    /// Each one gets whatever is most useful for what it is: an archive is unpacked and its members
    /// converted, anything else is converted. Output lands beside the input rather than in the
    /// working directory, because a process started from a file manager inherits a working
    /// directory that has nothing to do with where the files came from.
    /// </summary>
    private static int Dropped(List<string> positional, Options options)
    {
        PathExpander.Result inputs = PathExpander.Expand(
            positional, PathExpander.ContentExtensions, options.Recurse || positional.Any(Directory.Exists), options.All);

        foreach (string missing in inputs.Missing)
            Error($"{missing}: no such file or directory.");

        if (inputs.Files.Count == 0)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        Console.WriteLine($"pactool: {inputs.Files.Count} file(s) to convert");
        Console.WriteLine();

        var total = new ExtractResult();
        var claims = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;

        foreach (string path in inputs.Files)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string beside = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            string outputDirectory = ClaimDirectory(
                Path.Combine(options.Output ?? beside, stem.Length > 0 ? stem : "output"),
                Path.GetFileName(path), claims);

            try
            {
                if (IsArchive(path))
                {
                    var single = new Options
                    {
                        Output = outputDirectory,
                        Decode = true,
                        AllMips = options.AllMips,
                        KeepRaw = options.KeepRaw,
                        Tpl = options.Tpl,
                        NoJ3d = options.NoJ3d,
                        Bdl = options.Bdl,
                        MotionTables = options.MotionTables,
                        Strict = options.Strict,
                    };
                    if (UnpackOne(path, outputDirectory, single, total) != 0)
                        failed++;
                }
                else
                {
                    byte[] data = File.ReadAllBytes(path);
                    total.Add(ContentExtractor.Extract(Path.GetFileName(path), data, outputDirectory,
                                                       Options.ExtractOptions(options)));
                }
            }
            catch (Exception ex) when (ex is PacFormatException or IOException or UnauthorizedAccessException)
            {
                failed++;
                Error($"{path}: {ex.Message}");
            }
        }

        Console.WriteLine();
        ReportExtraction(total, options.Output ?? Path.GetDirectoryName(Path.GetFullPath(inputs.Files[0])) ?? ".");
        if (failed > 0)
            Error($"{failed} of {inputs.Files.Count} input(s) failed.");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>True when a file starts with the archive magic, whatever it is called.</summary>
    private static bool IsArchive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            return stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 &&
                   magic.SequenceEqual(PacFormat.Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        if (positional.Count == 0)
            return UsageError("unpack takes at least one archive.");

        PathExpander.Result inputs = PathExpander.Expand(positional, PathExpander.ArchiveExtensions,
                                                         options.Recurse, options.All);
        foreach (string missing in inputs.Missing)
            Error($"{missing}: no archive there.");

        if (inputs.Files.Count == 0)
            return UsageError("nothing to unpack.");

        // One archive keeps the old behaviour of writing straight into -o; several would collide
        // there, so each gets a directory of its own named after the archive.
        bool nested = inputs.Files.Count > 1;
        var total = new ExtractResult();
        var claims = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;

        foreach (string archivePath in inputs.Files)
        {
            string stem = Path.GetFileNameWithoutExtension(archivePath);
            string outputDirectory = options.Output is { } chosen
                ? (nested ? Path.Combine(chosen, stem.Length > 0 ? stem : "unpacked") : chosen)
                : (stem.Length > 0 ? stem : "unpacked");
            if (nested)
                outputDirectory = ClaimDirectory(outputDirectory, Path.GetFileName(archivePath), claims);

            try
            {
                if (nested)
                {
                    Console.WriteLine();
                    Console.WriteLine(archivePath);
                }

                failed += UnpackOne(archivePath, outputDirectory, options, total);
            }
            catch (Exception ex) when (ex is PacFormatException or IOException or UnauthorizedAccessException)
            {
                failed++;
                Error($"{archivePath}: {ex.Message}");
            }
        }

        if (nested)
        {
            Console.WriteLine();
            Console.WriteLine($"Unpacked {inputs.Files.Count - failed}/{inputs.Files.Count} archive(s).");
            if (options.Decode)
                ReportExtraction(total, options.Output ?? ".");
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Extracts one archive, and converts its members when asked to.</summary>
    private static int UnpackOne(string archivePath, string outputDirectory, Options options, ExtractResult total)
    {
        using var archive = PacArchive.Open(archivePath, options.Strict);
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
            ExtractOptions extractOptions = Options.ExtractOptions(options);

            // Members are kept as they are read so that a scene and the skeleton of the same stem
            // can be joined afterwards; neither half is a character model on its own.
            var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (PacMember member in archive.Members)
            {
                byte[] payload = archive.ReadMember(member);
                payloads[member.Name] = payload;
                string memberDirectory = Path.Combine(decodedDirectory, ContentExtractor.SafeName(member.Name));
                result.Add(ContentExtractor.Extract(member.Name, payload, memberDirectory, extractOptions));
            }

            if (extractOptions.ExportJ3d)
                ContentExtractor.ExportRiggedModels(payloads, decodedDirectory, extractOptions, result);

            ReportExtraction(result, decodedDirectory);
            total.Add(result);
        }

        ReportWarnings(archive);
        return 0;
    }

    private static int Decode(List<string> positional, Options options)
    {
        if (positional.Count == 0)
            return UsageError("decode takes at least one file.");

        PathExpander.Result inputs = PathExpander.Expand(positional, PathExpander.ContentExtensions,
                                                         options.Recurse, options.All);
        foreach (string missing in inputs.Missing)
            Error($"{missing}: no such file or directory.");

        if (inputs.Files.Count == 0)
            return UsageError("nothing to decode.");
        if (options.Flat && inputs.Files.Count > 1 && options.Output is null)
            return UsageError("--flat with several inputs needs an explicit -o directory.");

        var total = new ExtractResult();
        var claims = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;

        foreach (string path in inputs.Files)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string outputDirectory = options.Output is null
                ? (stem.Length > 0 ? stem : "decoded")
                : options.Flat ? options.Output : Path.Combine(options.Output, stem);
            if (!options.Flat)
                outputDirectory = ClaimDirectory(outputDirectory, Path.GetFileName(path), claims);

            try
            {
                byte[] data = File.ReadAllBytes(path);
                total.Add(ContentExtractor.Extract(Path.GetFileName(path), data, outputDirectory,
                                                   Options.ExtractOptions(options)));
            }
            catch (Exception ex) when (ex is PacFormatException or IOException or UnauthorizedAccessException)
            {
                failed++;
                Error($"{path}: {ex.Message}");
            }
        }

        Console.WriteLine();
        ReportExtraction(total, options.Output ?? ".");
        if (failed > 0)
            Error($"{failed} of {inputs.Files.Count} input(s) failed.");

        return failed == 0 ? 0 : 1;
    }

    private static int Info(List<string> positional, Options options)
    {
        if (positional.Count == 0)
            return UsageError("info takes at least one file.");

        PathExpander.Result inputs = PathExpander.Expand(positional, PathExpander.ContentExtensions,
                                                         options.Recurse, options.All);
        foreach (string missing in inputs.Missing)
            Error($"{missing}: no such file or directory.");

        int failed = 0;

        foreach (string path in inputs.Files)
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
        if (result.ModelsWritten > 0 || result.AnimationsWritten > 0)
        {
            Console.WriteLine($"  {result.ModelsWritten:N0} J3D model(s) and "
                              + $"{result.AnimationsWritten:N0} animation(s) written");
        }

        if (result.TexturePacksWritten > 0)
            Console.WriteLine($"  {result.TexturePacksWritten:N0} TPL texture bank(s) written");

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

        PathExpander.Result inputs = PathExpander.Expand(positional, PathExpander.ArchiveExtensions,
                                                         options.Recurse, options.All);
        foreach (string missing in inputs.Missing)
            Error($"{missing}: no archive there.");

        if (inputs.Files.Count == 0)
            return UsageError("nothing to verify.");

        int failed = 0;
        foreach (string path in inputs.Files)
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
        Console.WriteLine($"{inputs.Files.Count - failed}/{inputs.Files.Count} archive(s) round-tripped byte for byte.");
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

    /// <summary>
    /// Picks the output directory for one input, so that a batch cannot quietly write over itself.
    ///
    /// Inputs that share only a stem are meant to land together - a scene and the skeleton beside
    /// it are two halves of one character - so a directory is claimed per file name rather than per
    /// path. Two files that really do have the same name, in different folders, get a suffix.
    /// </summary>
    private static string ClaimDirectory(string directory, string fileName,
                                         Dictionary<string, HashSet<string>> claims)
    {
        string stem = directory;
        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? stem : $"{stem}~{n}";
            if (!claims.TryGetValue(candidate, out HashSet<string>? owners))
            {
                claims[candidate] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileName };
                return candidate;
            }

            if (owners.Contains(fileName))
                continue;

            owners.Add(fileName);
            return candidate;
        }
    }

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
        public bool NoJ3d { get; set; }
        public bool Bdl { get; set; }
        public bool MotionTables { get; set; }
        public bool Tpl { get; set; }
        public bool Recurse { get; set; }
        public bool All { get; set; }
        public bool? Pause { get; set; }

        public static ExtractOptions ExtractOptions(Options options) => new()
        {
            AllMips = options.AllMips,
            KeepRaw = options.KeepRaw,
            ExportJ3d = !options.NoJ3d,
            BinaryDisplayLists = options.Bdl,
            MotionTables = options.MotionTables,
            ExportTpl = options.Tpl,
            Log = Console.WriteLine,
        };
    }
}
