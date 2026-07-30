using System.Text;
using PacTool.Formats;
using PacTool.Imaging;

namespace PacTool.Extract;

/// <summary>Controls how much <see cref="ContentExtractor"/> writes.</summary>
public sealed class ExtractOptions
{
    /// <summary>Write every mip level rather than only the base one.</summary>
    public bool AllMips { get; set; }

    /// <summary>Also write each texture's stored bytes next to its PNG.</summary>
    public bool KeepRaw { get; set; }

    /// <summary>Receives one line per decoded item.</summary>
    public Action<string> Log { get; set; } = _ => { };
}

/// <summary>What a call to <see cref="ContentExtractor"/> produced.</summary>
public sealed class ExtractResult
{
    /// <summary>Files written.</summary>
    public int FilesWritten { get; set; }

    /// <summary>Images decoded to PNG.</summary>
    public int ImagesWritten { get; set; }

    /// <summary>Items whose format was not recognised and were copied verbatim.</summary>
    public int Unrecognised { get; set; }

    /// <summary>Non-fatal problems, each already reported through the log.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Adds another result's totals to this one.</summary>
    public void Add(ExtractResult other)
    {
        FilesWritten += other.FilesWritten;
        ImagesWritten += other.ImagesWritten;
        Unrecognised += other.Unrecognised;
        Warnings.AddRange(other.Warnings);
    }
}

/// <summary>
/// Turns the game's own formats into things a person can open: textures become PNG, tables become
/// text, and anything left over is written out verbatim so nothing is silently dropped.
///
/// Output goes into one directory per item, named after the item, so a stage archive lands as
/// <c>b0bdfblk.scn/</c>, <c>l0blight.pcp/</c>, <c>map.dat/</c> and so on beside each other.
/// </summary>
public static class ContentExtractor
{
    /// <summary>Decodes one blob into <paramref name="outputDirectory"/>.</summary>
    public static ExtractResult Extract(string name, byte[] data, string outputDirectory, ExtractOptions options)
    {
        var result = new ExtractResult();
        ContentKind kind;
        bool compressed;

        try
        {
            kind = ContentSniffer.Identify(name, data, out compressed);
            if (compressed)
            {
                data = Yaz0.Decompress(data, name);
                options.Log($"  {name}: Yaz0, {data.Length:N0} bytes decompressed");
            }
        }
        catch (PacFormatException ex)
        {
            result.Warnings.Add($"{name}: {ex.Message}");
            options.Log($"  {name}: {ex.Message}");
            kind = ContentKind.Unknown;
        }

        try
        {
            switch (kind)
            {
                case ContentKind.Archive:
                    ExtractArchive(name, data, outputDirectory, options, result);
                    return result;
                case ContentKind.PicturePack:
                    ExtractPicturePack(name, data, outputDirectory, options, result);
                    return result;
                case ContentKind.MpcModel:
                    ExtractMpc(name, data, outputDirectory, options, result);
                    return result;
                case ContentKind.DataDirectory:
                    ExtractDirectory(name, data, outputDirectory, options, result);
                    return result;
                case ContentKind.J3dModel:
                    ExtractJ3d(name, data, outputDirectory, options, result);
                    return result;
                case ContentKind.BtiTexture:
                    ExtractBti(name, data, outputDirectory, options, result);
                    return result;
                case ContentKind.SoftimagePic:
                    ExtractPic(name, data, outputDirectory, options, result);
                    return result;
            }
        }
        catch (PacFormatException ex)
        {
            // A blob that sniffed as a known format but failed to parse is still worth keeping.
            result.Warnings.Add($"{name}: {ex.Message}");
            options.Log($"  {name}: {ex.Message}; extracted verbatim instead");
        }

        Directory.CreateDirectory(outputDirectory);
        Write(Path.Combine(outputDirectory, SafeName(name)), data, result);
        result.Unrecognised++;
        options.Log($"  {name,-20} {ContentSniffer.Describe(kind)}, {data.Length:N0} bytes");
        return result;
    }

    /// <summary>Decodes every member of a <c>CAPR</c> archive.</summary>
    public static ExtractResult ExtractArchive(string name, byte[] data, string outputDirectory,
                                               ExtractOptions options, ExtractResult? into = null)
    {
        var result = into ?? new ExtractResult();
        using var stream = new MemoryStream(data, writable: false);
        using var archive = PacArchive.Open(stream, name);

        options.Log($"{name}: CAPR archive, {archive.Members.Count} member(s)");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PacMember member in archive.Members)
        {
            string stem = Deduplicate(SafeName(member.Name), used);
            result.Add(Extract(member.Name, archive.ReadMember(member),
                               Path.Combine(outputDirectory, stem), options));
        }

        return result;
    }

    private static void ExtractPicturePack(string name, byte[] data, string outputDirectory,
                                           ExtractOptions options, ExtractResult result)
    {
        PicturePack pack = PicturePack.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        Report(name, pack.Warnings, options, result);

        options.Log($"  {name,-20} Picture Pack, {pack.Textures.Count} texture(s)" +
                    (pack.Scene is null ? "" : $" + {pack.Scene.Raw.Length:N0} B scene table"));

        string textureDirectory = Path.Combine(outputDirectory, "textures");
        if (pack.Textures.Count > 0)
            Directory.CreateDirectory(textureDirectory);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = new StringBuilder();
        index.AppendLine($"# textures of {name}");
        index.AppendLine("#");
        index.AppendLine("#  idx  name              format  size       mips  wrap           stored");
        index.AppendLine("# ----  ----------------  ------  ---------  ----  -------------  ---------");

        foreach (PicturePackTexture texture in pack.Textures)
        {
            string stem = Deduplicate(SafeName(texture.Name.Length > 0 ? texture.Name : $"texture_{texture.Index:D3}"), used);
            index.AppendLine($"  {texture.Index,4}  {texture.Name,-16}  {texture.Describe()}");

            if (options.KeepRaw)
                Write(Path.Combine(textureDirectory, stem + ".bin"), texture.Data, result);

            WriteMips(textureDirectory, stem, texture.MipLevels, options, result,
                      level => texture.Decode(level), name, texture.Name);
        }

        if (pack.Textures.Count > 0)
            WriteText(Path.Combine(textureDirectory, "textures.txt"), index.ToString(), result);

        if (pack.Scene is { } scene)
            ExtractScene(name, scene, outputDirectory, result);
    }

    private static void ExtractScene(string name, SceneTable scene, string outputDirectory, ExtractResult result)
    {
        WriteText(Path.Combine(outputDirectory, "scene.txt"), scene.Describe(name), result);
        Write(Path.Combine(outputDirectory, "scene.bin"), scene.Raw, result);

        var payloads = scene.Records.Where(r => r.Kind == SceneRecordKind.Payload && r.Raw.Length > 0).ToList();
        if (payloads.Count == 0)
            return;

        string commandDirectory = Path.Combine(outputDirectory, "displaylists");
        Directory.CreateDirectory(commandDirectory);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SceneRecord payload in payloads)
        {
            string stem = payload.Owner is null
                ? $"offset_{payload.Offset:X6}"
                : SafeName(payload.Owner.FileStem);
            Write(Path.Combine(commandDirectory, Deduplicate(stem, used) + ".bin"), payload.Raw, result);
        }
    }

    private static void ExtractMpc(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        MpcModel model = MpcModel.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        Report(name, model.Warnings, options, result);

        options.Log($"  {name,-20} MPC model, {model.Nodes.Count} node(s), root '{model.RootName}', " +
                    $"{model.MeshData.Length:N0} B mesh data");

        WriteText(Path.Combine(outputDirectory, "skeleton.txt"), model.DescribeSkeleton(name), result);
        Write(Path.Combine(outputDirectory, "skeleton.bin"), model.SkeletonData, result);
        Write(Path.Combine(outputDirectory, "mesh.bin"), model.MeshData, result);
    }

    private static void ExtractDirectory(string name, byte[] data, string outputDirectory,
                                         ExtractOptions options, ExtractResult result)
    {
        DataDirectory directory = DataDirectory.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        Report(name, directory.Warnings, options, result);

        int spawns = directory.Entries.Sum(e => e.Spawns.Count);
        options.Log($"  {name,-20} {directory.Kind} directory, {directory.Entries.Count} entr" +
                    $"{(directory.Entries.Count == 1 ? "y" : "ies")}" +
                    (spawns > 0 ? $", {spawns} spawn(s)" : ""));

        WriteText(Path.Combine(outputDirectory, "directory.txt"), directory.Describe(name), result);
        if (directory.Trailing.Length > 0)
            Write(Path.Combine(outputDirectory, "leveldata.bin"), directory.Trailing, result);
    }

    private static void ExtractJ3d(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        J3dModel model = J3dModel.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        Report(name, model.Warnings, options, result);

        options.Log($"  {name,-20} J3D {model.Variant}, {model.Sections.Count} section(s), " +
                    $"{model.Textures.Count} texture(s), {model.JointNames.Count} joint(s)");

        WriteText(Path.Combine(outputDirectory, "model.txt"), model.Describe(name), result);

        string sectionDirectory = Path.Combine(outputDirectory, "sections");
        Directory.CreateDirectory(sectionDirectory);
        foreach (J3dSection section in model.Sections)
            Write(Path.Combine(sectionDirectory, $"{section.Index:D2}_{SafeName(section.Magic)}.bin"), section.Data, result);

        if (model.Textures.Count == 0)
            return;

        string textureDirectory = Path.Combine(outputDirectory, "textures");
        Directory.CreateDirectory(textureDirectory);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < model.Textures.Count; i++)
        {
            BtiTexture texture = model.Textures[i];
            string stem = Deduplicate(SafeName(texture.Name.Length > 0 ? texture.Name : $"texture_{i:D3}"), used);

            if (options.KeepRaw)
                Write(Path.Combine(textureDirectory, stem + ".bti.bin"), texture.Data, result);

            WriteMips(textureDirectory, stem, texture.MipCount, options, result,
                      level => texture.Decode(level), name, texture.Name);
        }
    }

    private static void ExtractBti(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        BtiTexture texture = BtiTexture.Parse(data, 0, name);
        Directory.CreateDirectory(outputDirectory);
        options.Log($"  {name,-20} BTI, {texture.Describe()}");

        string stem = SafeName(Path.GetFileNameWithoutExtension(name));
        WriteMips(outputDirectory, stem.Length > 0 ? stem : "texture", texture.MipCount, options, result,
                  level => texture.Decode(level), name, name);
    }

    private static void ExtractPic(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        SoftimagePic pic = SoftimagePic.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        options.Log($"  {name,-20} {pic.Describe()}");

        string stem = SafeName(Path.GetFileNameWithoutExtension(name));
        PngWriter.Save(pic.Image, Path.Combine(outputDirectory, (stem.Length > 0 ? stem : "image") + ".png"));
        result.FilesWritten++;
        result.ImagesWritten++;
    }

    /// <summary>
    /// Writes the base mip level as <c>&lt;stem&gt;.png</c>, and the rest as <c>&lt;stem&gt;.mip1.png</c>
    /// and so on when asked for. A level that fails to decode is reported and skipped rather than
    /// aborting the whole texture.
    /// </summary>
    private static void WriteMips(string directory, string stem, int levels, ExtractOptions options,
                                  ExtractResult result, Func<int, Rgba32Image> decode,
                                  string sourceName, string textureName)
    {
        int wanted = options.AllMips ? levels : 1;
        for (int level = 0; level < wanted; level++)
        {
            try
            {
                Rgba32Image image = decode(level);
                string suffix = level == 0 ? "" : $".mip{level}";
                PngWriter.Save(image, Path.Combine(directory, $"{stem}{suffix}.png"));
                result.FilesWritten++;
                result.ImagesWritten++;
            }
            catch (Exception ex) when (ex is PacFormatException or ArgumentOutOfRangeException)
            {
                string message = $"{sourceName}: texture '{textureName}' mip {level}: {ex.Message}";
                result.Warnings.Add(message);
                options.Log($"    {message}");
            }
        }
    }

    private static void Report(string name, IReadOnlyList<string> warnings, ExtractOptions options, ExtractResult result)
    {
        foreach (string warning in warnings)
        {
            result.Warnings.Add($"{name}: {warning}");
            options.Log($"    note: {warning}");
        }
    }

    private static void Write(string path, byte[] data, ExtractResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllBytes(path, data);
        result.FilesWritten++;
    }

    private static void WriteText(string path, string text, ExtractResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, text, Utf8NoBom);
        result.FilesWritten++;
    }

    /// <summary>The listings are plain text, so they get no byte order mark.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Turns a name from the data into one that is safe to create. Names are untrusted, so
    /// separators and traversal are replaced rather than allowed to escape the output directory.
    /// </summary>
    internal static string SafeName(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (char c in name)
            text.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);

        string result = text.ToString().Trim().TrimEnd('.');
        return result.Length == 0 || result is "." or ".." ? "_" : result;
    }

    private static string Deduplicate(string stem, HashSet<string> used)
    {
        if (used.Add(stem))
            return stem;

        for (int n = 2; ; n++)
        {
            string candidate = $"{stem}~{n}";
            if (used.Add(candidate))
                return candidate;
        }
    }
}
