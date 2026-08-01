using System.Text;
using PacTool.Export;
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

    /// <summary>Write a .bmd and a .bck per motion when a scene and a skeleton pair up.</summary>
    public bool ExportJ3d { get; set; } = true;

    /// <summary>Write the .bdl variant tag rather than .bmd.</summary>
    public bool BinaryDisplayLists { get; set; }

    /// <summary>Also write each motion's frames as CSV.</summary>
    public bool MotionTables { get; set; }

    /// <summary>Also write each container's textures as a .tpl texture bank.</summary>
    public bool ExportTpl { get; set; }

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

    /// <summary>Meshes decoded to OBJ.</summary>
    public int MeshesWritten { get; set; }

    /// <summary>Rigged models written as J3D.</summary>
    public int ModelsWritten { get; set; }

    /// <summary>Animations written as BCK.</summary>
    public int AnimationsWritten { get; set; }

    /// <summary>Texture banks written as TPL.</summary>
    public int TexturePacksWritten { get; set; }

    /// <summary>Stage areas whose layout was written.</summary>
    public int AreasWritten { get; set; }

    /// <summary>Items whose format was not recognised and were copied verbatim.</summary>
    public int Unrecognised { get; set; }

    /// <summary>Non-fatal problems, each already reported through the log.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Adds another result's totals to this one.</summary>
    public void Add(ExtractResult other)
    {
        FilesWritten += other.FilesWritten;
        ImagesWritten += other.ImagesWritten;
        MeshesWritten += other.MeshesWritten;
        ModelsWritten += other.ModelsWritten;
        AnimationsWritten += other.AnimationsWritten;
        TexturePacksWritten += other.TexturePacksWritten;
        AreasWritten += other.AreasWritten;
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

        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (PacMember member in archive.Members)
        {
            byte[] payload = archive.ReadMember(member);
            payloads[member.Name] = payload;
            string stem = Deduplicate(SafeName(member.Name), used);
            result.Add(Extract(member.Name, payload, Path.Combine(outputDirectory, stem), options));
        }

        if (options.ExportJ3d)
            ExportRiggedModels(payloads, outputDirectory, options, result);

        ExportStageLayout(payloads, outputDirectory, options, result);
        return result;
    }

    /// <summary>
    /// Joins the three stage blocks into one listing. They are three views of the same stage split
    /// across three members - the foreground in <c>map.dat</c>, the background in <c>bg.dat</c>,
    /// the enemies in <c>enemy.dat</c> - and each keeps the same list of areas, so what is actually
    /// in an area only becomes readable once they are put side by side.
    /// </summary>
    public static void ExportStageLayout(IReadOnlyDictionary<string, byte[]> payloads, string outputDirectory,
                                         ExtractOptions options, ExtractResult result)
    {
        if (!payloads.TryGetValue("map.dat", out byte[]? mapData))
            return;

        try
        {
            DataDirectory map = DataDirectory.Parse(mapData, "map.dat");
            if (map.Level is not { } level)
                return;

            List<DataDirectoryEntry>? spawns = null;
            if (payloads.TryGetValue("enemy.dat", out byte[]? enemyData))
            {
                DataDirectory enemies = DataDirectory.Parse(enemyData, "enemy.dat");
                spawns = enemies.Entries.ToList();
                if (spawns.Count != level.Areas.Count)
                {
                    result.Warnings.Add($"enemy.dat lists {spawns.Count} area(s) but map.dat has {level.Areas.Count}.");
                    options.Log($"    note: enemy.dat lists {spawns.Count} area(s) but map.dat has {level.Areas.Count}");
                }
            }

            Directory.CreateDirectory(outputDirectory);
            WriteText(Path.Combine(outputDirectory, "stage.txt"),
                      level.Describe("the stage", map.Entries, spawns), result);

            options.Log($"  {"stage layout",-20} {level.Areas.Count} area(s), {level.PlacementCount:N0} placement(s)" +
                        (spawns is null ? "" : $", {spawns.Sum(e => e.Spawns.Count)} spawn(s)"));
        }
        catch (PacFormatException ex)
        {
            result.Warnings.Add($"stage layout: {ex.Message}");
            options.Log($"    note: stage layout: {ex.Message}");
        }
    }

    /// <summary>
    /// Joins each scene with the skeleton of the same stem and writes the pair out as J3D. A
    /// character model is split across two members - the geometry and rest poses in the
    /// <c>.scn</c>, the joints and animations in the <c>.mpc</c> - so neither is exportable alone.
    /// </summary>
    public static void ExportRiggedModels(IReadOnlyDictionary<string, byte[]> payloads, string outputDirectory,
                                          ExtractOptions options, ExtractResult result)
    {
        foreach ((string name, byte[] data) in payloads)
        {
            if (!name.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
                continue;

            string stem = Path.GetFileNameWithoutExtension(name);
            if (!payloads.TryGetValue(stem + ".mpc", out byte[]? skeletonData))
                continue;

            try
            {
                PicturePack pack = PicturePack.Parse(data, name);
                if (pack.Scene is not { } scene)
                    continue;

                foreach (SceneRecord shape in scene.Shapes)
                {
                    shape.Mesh ??= shape.Kind == SceneRecordKind.SkinnedShape
                        ? ScnMesh.DecodeSkinned(shape.Geometry!, shape.VertexCount, shape.TriangleCount, shape.PrimitiveCount)
                        : ScnMesh.DecodeDisplayList(shape.Geometry!, shape.VertexCount, shape.TriangleCount);
                }

                MpcModel skeleton = MpcModel.Parse(skeletonData, stem + ".mpc");
                RiggedModel model = RiggedModel.Assemble(stem, scene, skeleton,
                                                         pack.Textures.Select(t => t.Name).ToList());
                if (model.Meshes.Count == 0 || model.Joints.Count == 0)
                    continue;

                WriteRiggedModel(model, pack, Path.Combine(outputDirectory, SafeName(stem) + ".model"), options, result);
            }
            catch (PacFormatException ex)
            {
                result.Warnings.Add($"{stem}: {ex.Message}");
                options.Log($"    note: {stem}: {ex.Message}");
            }
        }
    }

    private static void WriteRiggedModel(RiggedModel model, PicturePack pack, string outputDirectory,
                                         ExtractOptions options, ExtractResult result)
    {
        Directory.CreateDirectory(outputDirectory);
        Report(model.Name, model.Warnings, options, result);

        options.Log($"  {model.Name + " (rigged)",-20} {model.Joints.Count} joint(s), {model.PosedJointCount} posed, " +
                    $"{model.Meshes.Count} mesh(es), {model.Motions.Count} motion(s)");

        WriteText(Path.Combine(outputDirectory, "skeleton.txt"), model.DescribeSkeleton(), result);

        string extension = options.BinaryDisplayLists ? ".bdl" : ".bmd";
        Write(Path.Combine(outputDirectory, model.Name + extension),
              BmdWriter.Build(model, pack.Textures, options.BinaryDisplayLists), result);
        result.ModelsWritten++;

        if (model.Motions.Count == 0)
            return;

        WriteText(Path.Combine(outputDirectory, "motions.txt"),
                  MpcMotion.Describe(model.Name, model.Motions), result);

        string animationDirectory = Path.Combine(outputDirectory, "animation");
        Directory.CreateDirectory(animationDirectory);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jointNames = model.Joints.Select(j => j.Name).ToList();

        foreach (MpcMotion motion in model.Motions)
        {
            string stem = Deduplicate(SafeName(motion.Name), used);
            Write(Path.Combine(animationDirectory, stem + ".bck"),
                  BckWriter.Build(motion, model.Joints.Count), result);
            result.AnimationsWritten++;

            if (options.MotionTables)
                WriteText(Path.Combine(animationDirectory, stem + ".csv"), motion.DescribeFrames(jointNames), result);
        }
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
        {
            WriteText(Path.Combine(textureDirectory, "textures.txt"), index.ToString(), result);
            WriteTpl(textureDirectory, Path.GetFileNameWithoutExtension(name), name,
                     pack.Textures.Select(TplTexture.From).ToList(), options, result);
        }

        if (pack.Scene is { } scene)
            ExtractScene(name, scene, outputDirectory, options, result);
    }

    private static void ExtractScene(string name, SceneTable scene, string outputDirectory,
                                     ExtractOptions options, ExtractResult result)
    {
        var shapes = scene.Shapes.ToList();
        foreach (SceneRecord shape in shapes)
        {
            shape.Mesh = shape.Kind == SceneRecordKind.SkinnedShape
                ? ScnMesh.DecodeSkinned(shape.Geometry!, shape.VertexCount, shape.TriangleCount, shape.PrimitiveCount)
                : ScnMesh.DecodeDisplayList(shape.Geometry!, shape.VertexCount, shape.TriangleCount);
        }

        WriteText(Path.Combine(outputDirectory, "scene.txt"), scene.Describe(name), result);
        Write(Path.Combine(outputDirectory, "scene.bin"), scene.Raw, result);

        int decoded = shapes.Count(s => s.Mesh is not null);
        if (shapes.Count > 0)
        {
            int skinned = shapes.Count(s => s.Mesh?.Skin is not null);
            options.Log($"    {shapes.Count} shape(s), {decoded} decoded to geometry" +
                        (skinned > 0 ? $" ({skinned} skinned)" : "") +
                        $", {shapes.Sum(s => s.Mesh?.Triangles.Count ?? 0):N0} triangle(s)");
        }

        foreach (SceneRecord shape in shapes.Where(s => s.Mesh is null))
        {
            string message = $"shape '{shape.FileStem}': its {shape.VertexCount} vertices and " +
                             $"{shape.TriangleCount} triangles do not decode under any known layout";
            result.Warnings.Add($"{name}: {message}");
            options.Log($"    note: {message}");
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (decoded > 0)
        {
            string geometryDirectory = Path.Combine(outputDirectory, "geometry");
            Directory.CreateDirectory(geometryDirectory);

            var all = new List<(string Name, ScnMesh Mesh)>();
            foreach (SceneRecord shape in shapes)
            {
                if (shape.Mesh is not { } mesh)
                    continue;

                string stem = Deduplicate(SafeName(shape.FileStem), used);
                ObjWriter.Save(mesh, shape.FileStem, Path.Combine(geometryDirectory, stem + ".obj"));
                result.FilesWritten++;
                all.Add((shape.FileStem, mesh));

                // Skin weights have nowhere to go in an OBJ, so they get their own table.
                if (mesh.Skin is not null)
                {
                    WriteText(Path.Combine(geometryDirectory, stem + ".skin.csv"),
                              ObjWriter.DescribeSkin(mesh), result);
                }
            }

            // One combined file as well, so the whole scene can be opened in a single step.
            ObjWriter.SaveAll(all, Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(name) + ".obj"));
            result.FilesWritten++;
            result.MeshesWritten += all.Count;
        }

        if (!options.KeepRaw)
            return;

        string listDirectory = Path.Combine(outputDirectory, "displaylists");
        var listNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SceneRecord shape in shapes)
            Write(Path.Combine(listDirectory, Deduplicate(SafeName(shape.FileStem), listNames) + ".bin"), shape.Geometry!, result);

        foreach (SceneRecord payload in scene.Records.Where(r => r.Kind == SceneRecordKind.Payload && r.Raw.Length > 0))
            Write(Path.Combine(listDirectory, $"offset_{payload.Offset:X6}.bin"), payload.Raw, result);
    }

    private static void ExtractMpc(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        MpcModel model = MpcModel.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        Report(name, model.Warnings, options, result);

        options.Log($"  {name,-20} MPC skeleton, {model.Nodes.Count} joint(s), root '{model.RootName}', " +
                    $"{model.Motions.Count} motion(s)");

        WriteText(Path.Combine(outputDirectory, "skeleton.txt"), model.DescribeSkeleton(name), result);
        Write(Path.Combine(outputDirectory, "skeleton.bin"), model.SkeletonData, result);

        if (options.KeepRaw || model.Motions.Count == 0)
            Write(Path.Combine(outputDirectory, "motiondata.bin"), model.MeshData, result);
        if (model.Motions.Count == 0)
            return;

        WriteText(Path.Combine(outputDirectory, "motions.txt"), MpcMotion.Describe(name, model.Motions), result);
        if (!options.ExportJ3d)
            return;

        // A skeleton on its own is enough for an animation, even without the scene that goes with
        // it, so these are written whether or not a paired .scn turned up.
        string animationDirectory = Path.Combine(outputDirectory, "animation");
        Directory.CreateDirectory(animationDirectory);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jointNames = model.Nodes.Select(n => n.Name).ToList();

        foreach (MpcMotion motion in model.Motions)
        {
            string stem = Deduplicate(SafeName(motion.Name), used);
            Write(Path.Combine(animationDirectory, stem + ".bck"),
                  BckWriter.Build(motion, model.Nodes.Count), result);
            result.AnimationsWritten++;
            if (options.MotionTables)
                WriteText(Path.Combine(animationDirectory, stem + ".csv"), motion.DescribeFrames(jointNames), result);
        }
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
                    (spawns > 0 ? $", {spawns} spawn(s)" : "") +
                    (directory.Level is { } head ? $", {head.Areas.Count} area(s), {head.PlacementCount:N0} placement(s)" : ""));

        WriteText(Path.Combine(outputDirectory, "directory.txt"), directory.Describe(name), result);

        if (directory.Level is not { } level)
        {
            if (options.KeepRaw && directory.Trailing.Length > 0)
                Write(Path.Combine(outputDirectory, "trailing.bin"), directory.Trailing, result);

            return;
        }

        WriteText(Path.Combine(outputDirectory, "areas.txt"), level.Describe(name, directory.Entries), result);
        WriteText(Path.Combine(outputDirectory, "placements.csv"),
                  level.DescribePlacements(directory.Entries), result);
        result.AreasWritten += level.Areas.Count;

        if (options.KeepRaw)
            Write(Path.Combine(outputDirectory, "leveldata.bin"), directory.Trailing, result);

        // The collision boxes are only worth drawing where there are any, which means map.dat.
        if (!directory.Entries.Any(e => e.Boxes.Count > 0))
            return;

        string layoutDirectory = Path.Combine(outputDirectory, "layout");
        Directory.CreateDirectory(layoutDirectory);
        foreach (StageArea area in level.Areas)
        {
            string path = Path.Combine(layoutDirectory, $"area{area.Index}.obj");
            if (StageLayoutWriter.Save(area, directory.Entries, path))
            {
                result.FilesWritten++;
                result.MeshesWritten++;
            }
        }
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

        WriteTpl(textureDirectory, Path.GetFileNameWithoutExtension(name), name,
                 model.Textures.Select((t, i) => TplTexture.From(t, t.Name.Length > 0 ? t.Name : $"texture_{i:D3}")).ToList(),
                 options, result);
    }

    private static void ExtractBti(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        BtiTexture texture = BtiTexture.Parse(data, 0, name);
        Directory.CreateDirectory(outputDirectory);
        options.Log($"  {name,-20} BTI, {texture.Describe()}");

        string stem = SafeName(Path.GetFileNameWithoutExtension(name));
        if (stem.Length == 0)
            stem = "texture";

        WriteMips(outputDirectory, stem, texture.MipCount, options, result,
                  level => texture.Decode(level), name, name);
        WriteTpl(outputDirectory, stem, name, [TplTexture.From(texture, stem)], options, result);
    }

    private static void ExtractPic(string name, byte[] data, string outputDirectory,
                                   ExtractOptions options, ExtractResult result)
    {
        SoftimagePic pic = SoftimagePic.Parse(data, name);
        Directory.CreateDirectory(outputDirectory);
        options.Log($"  {name,-20} {pic.Describe()}");

        string stem = SafeName(Path.GetFileNameWithoutExtension(name));
        if (stem.Length == 0)
            stem = "image";

        PngWriter.Save(pic.Image, Path.Combine(outputDirectory, stem + ".png"));
        result.FilesWritten++;
        result.ImagesWritten++;

        // Source art never was a GX texture, so this is the one place a TPL is encoded rather than
        // copied: RGBA8, which keeps every colour and every alpha value the PIC held.
        WriteTpl(outputDirectory, stem, name, [TplTexture.From(pic.Image, stem)], options, result);
    }

    /// <summary>
    /// Writes a container's textures as one TPL, the texture bank the GameCube SDK loads directly.
    /// A Picture Pack or a <c>TEX1</c> section is already a bank of GX textures, so the mip chains
    /// go across byte for byte and only the headers around them are new.
    /// </summary>
    private static void WriteTpl(string directory, string stem, string sourceName,
                                 IReadOnlyList<TplTexture> textures, ExtractOptions options, ExtractResult result)
    {
        if (!options.ExportTpl || textures.Count == 0)
            return;

        try
        {
            Write(Path.Combine(directory, stem + ".tpl"), TplWriter.Build(textures), result);
            WriteText(Path.Combine(directory, stem + ".tpl.txt"), TplWriter.Describe(sourceName, textures), result);
            result.TexturePacksWritten++;
        }
        catch (PacFormatException ex)
        {
            result.Warnings.Add($"{sourceName}: {ex.Message}");
            options.Log($"    note: {ex.Message}");
        }
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
