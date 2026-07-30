using System.Buffers.Binary;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// A J3D model - the GameCube model format Nintendo's middleware produced, stored as <c>.bmd</c>
/// (<c>bmd3</c>) or <c>.bdl</c> (<c>bdl4</c>). A BDL is a BMD with the draw calls pre-compiled into
/// an extra <c>MDL3</c> section; everything else is identical, so both parse here.
///
/// <code>
///   0x00  char[4]  "J3D1"
///   0x04  char[4]  "bmd3" or "bdl4"
///   0x08  u32      fileSize
///   0x0C  u32      sectionCount
///   0x10  char[4]  subversion, normally "SVR3"
///   0x14  u8[12]   padding, 0xFF
///   0x20  ...      sections, each a four-character magic and a u32 size covering both
/// </code>
///
/// The sections are <c>INF1</c> scene graph, <c>VTX1</c> vertex arrays, <c>EVP1</c> envelopes,
/// <c>DRW1</c> draw matrices, <c>JNT1</c> joints, <c>SHP1</c> shapes, <c>MAT3</c> materials,
/// <c>MDL3</c> compiled draw calls and <c>TEX1</c> textures.
///
/// This reader inventories every section, extracts them verbatim, and decodes the ones whose
/// contents are self-describing: <c>TEX1</c> to PNG through the shared GX decoder,
/// <c>INF1</c> to a hierarchy listing, and the names <c>JNT1</c>, <c>MAT3</c> and <c>SHP1</c>
/// carry. Geometry is not reconstructed - that needs the vertex attribute descriptors and the
/// display lists read together, which is a larger job than texture extraction and one that no
/// sample in this repository would exercise.
/// </summary>
public sealed class J3dModel
{
    /// <summary>Size of the file header before the first section.</summary>
    public const int HeaderSize = 0x20;

    /// <summary><c>bmd3</c> or <c>bdl4</c>.</summary>
    public string Variant { get; }

    /// <summary>Subversion tag at 0x10, normally <c>SVR3</c>.</summary>
    public string Subversion { get; }

    /// <summary>File size the header claims.</summary>
    public uint DeclaredSize { get; }

    /// <summary>Sections in stored order.</summary>
    public IReadOnlyList<J3dSection> Sections { get; }

    /// <summary>Textures from the <c>TEX1</c> section, in stored order.</summary>
    public IReadOnlyList<BtiTexture> Textures { get; }

    /// <summary>Joint names from <c>JNT1</c>.</summary>
    public IReadOnlyList<string> JointNames { get; }

    /// <summary>Material names from <c>MAT3</c>.</summary>
    public IReadOnlyList<string> MaterialNames { get; }

    /// <summary>The scene graph from <c>INF1</c>.</summary>
    public IReadOnlyList<J3dHierarchyNode> Hierarchy { get; }

    /// <summary>Non-fatal oddities noticed while parsing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    private J3dModel(string variant, string subversion, uint declaredSize, List<J3dSection> sections,
                     List<BtiTexture> textures, List<string> jointNames, List<string> materialNames,
                     List<J3dHierarchyNode> hierarchy, List<string> warnings)
    {
        Variant = variant;
        Subversion = subversion;
        DeclaredSize = declaredSize;
        Sections = sections;
        Textures = textures;
        JointNames = jointNames;
        MaterialNames = materialNames;
        Hierarchy = hierarchy;
        Warnings = warnings;
    }

    /// <summary>True if <paramref name="data"/> starts with a J3D header this reader understands.</summary>
    public static bool LooksLikeModel(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return false;
        if (!data[..4].SequenceEqual("J3D1"u8))
            return false;

        return data[4..8].SequenceEqual("bmd3"u8) || data[4..8].SequenceEqual("bdl4"u8);
    }

    /// <summary>Parses a BMD or BDL.</summary>
    public static J3dModel Parse(ReadOnlySpan<byte> data, string sourceName)
    {
        if (!LooksLikeModel(data))
        {
            string found = data.Length >= 8 ? Ascii.Decode(data[..8]) : "(too short)";
            throw new PacFormatException($"{sourceName}: expected a 'J3D1bmd3' or 'J3D1bdl4' header, found '{found}'.");
        }

        string variant = Ascii.Decode(data[4..8]);
        uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        long sectionCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]);
        var warnings = new List<string>();

        if (declaredSize != data.Length)
            warnings.Add($"the header states {declaredSize:N0} bytes but the file is {data.Length:N0}.");

        var sections = new List<J3dSection>();
        int offset = HeaderSize;
        for (int i = 0; i < sectionCount && offset + 8 <= data.Length; i++)
        {
            string magic = Ascii.Decode(data.Slice(offset, 4));
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            if (size < 8 || offset + size > data.Length)
            {
                warnings.Add($"section {i} ('{magic}') at 0x{offset:X} claims {size:N0} bytes, which does not fit; stopping.");
                break;
            }

            sections.Add(new J3dSection
            {
                Index = i,
                Magic = magic,
                Offset = offset,
                Data = data.Slice(offset, (int)size).ToArray(),
            });
            offset += (int)size;
        }

        if (sections.Count < sectionCount)
            warnings.Add($"the header announces {sectionCount} section(s) but only {sections.Count} could be read.");

        var textures = new List<BtiTexture>();
        var jointNames = new List<string>();
        var materialNames = new List<string>();
        var hierarchy = new List<J3dHierarchyNode>();

        foreach (J3dSection section in sections)
        {
            try
            {
                switch (section.Magic)
                {
                    case "TEX1":
                        textures.AddRange(ParseTex1(section, sourceName, warnings));
                        break;
                    case "JNT1":
                        jointNames.AddRange(ReadNameTable(section, 0x0C, sourceName, warnings));
                        break;
                    case "MAT3":
                        materialNames.AddRange(ReadNameTable(section, 0x14, sourceName, warnings));
                        break;
                    case "INF1":
                        hierarchy.AddRange(ParseInf1(section));
                        break;
                }
            }
            catch (PacFormatException ex)
            {
                warnings.Add($"section '{section.Magic}': {ex.Message}");
            }
        }

        return new J3dModel(variant, Ascii.Decode(data.Slice(0x10, 4)), declaredSize,
                            sections, textures, jointNames, materialNames, hierarchy, warnings);
    }

    /// <summary>
    /// Reads the texture bank.
    ///
    /// <code>
    ///   0x08  u16  textureCount
    ///   0x0A  u16  padding
    ///   0x0C  u32  headerOffset   relative to the section
    ///   0x10  u32  nameTableOffset
    /// </code>
    ///
    /// Each of the <c>textureCount</c> headers is a plain <see cref="BtiTexture"/> whose own image
    /// and palette offsets are relative to itself, so several headers may share one image.
    /// </summary>
    private static List<BtiTexture> ParseTex1(J3dSection section, string sourceName, List<string> warnings)
    {
        ReadOnlySpan<byte> data = section.Data;
        if (data.Length < 0x14)
            throw new PacFormatException($"TEX1 is {data.Length} bytes, too short for its header.");

        int count = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int headerOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]);
        int nameOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
        var names = ReadStringTable(data, nameOffset);

        var textures = new List<BtiTexture>(count);
        for (int i = 0; i < count; i++)
        {
            int at = headerOffset + i * BtiTexture.HeaderSize;
            try
            {
                BtiTexture texture = BtiTexture.Parse(data, at, $"{sourceName}#TEX1[{i}]");
                texture.Name = i < names.Count ? names[i] : $"texture_{i:D2}";
                textures.Add(texture);
            }
            catch (PacFormatException ex)
            {
                warnings.Add($"TEX1 texture {i}: {ex.Message}");
            }
        }

        return textures;
    }

    /// <summary>
    /// Reads the scene graph.
    ///
    /// <code>
    ///   0x18  u32  hierarchyOffset  relative to the section
    ///   ...        u16 type, u16 index pairs; type 0 ends the list
    /// </code>
    ///
    /// Type 1 opens a level, 2 closes it, and 0x10, 0x11 and 0x12 attach a joint, a material and a
    /// shape respectively.
    /// </summary>
    private static List<J3dHierarchyNode> ParseInf1(J3dSection section)
    {
        ReadOnlySpan<byte> data = section.Data;
        if (data.Length < 0x1C)
            throw new PacFormatException($"INF1 is {data.Length} bytes, too short for its header.");

        int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x18..]);
        var nodes = new List<J3dHierarchyNode>();
        int depth = 0;

        while (offset + 4 <= data.Length)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int index = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4;

            if (type == 0)
                break;
            if (type == 1)
            {
                depth++;
                continue;
            }

            if (type == 2)
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            nodes.Add(new J3dHierarchyNode { Depth = depth, Type = type, Index = index });
        }

        return nodes;
    }

    /// <summary>Reads the name table a section points at from <paramref name="pointerOffset"/>.</summary>
    private static List<string> ReadNameTable(J3dSection section, int pointerOffset, string sourceName, List<string> warnings)
    {
        ReadOnlySpan<byte> data = section.Data;
        if (data.Length < pointerOffset + 4)
        {
            warnings.Add($"{sourceName}: {section.Magic} is too short to hold a name table pointer.");
            return [];
        }

        return ReadStringTable(data, (int)BinaryPrimitives.ReadUInt32BigEndian(data[pointerOffset..]));
    }

    /// <summary>
    /// Reads a J3D string table: a count, then one <c>u16</c> hash plus <c>u16</c> offset per
    /// string, then the NUL-terminated strings themselves. Offsets are relative to the table.
    /// </summary>
    private static List<string> ReadStringTable(ReadOnlySpan<byte> data, int tableOffset)
    {
        var names = new List<string>();
        if (tableOffset <= 0 || tableOffset + 4 > data.Length)
            return names;

        int count = BinaryPrimitives.ReadUInt16BigEndian(data[tableOffset..]);
        for (int i = 0; i < count; i++)
        {
            int entry = tableOffset + 4 + i * 4;
            if (entry + 4 > data.Length)
                break;

            int at = tableOffset + BinaryPrimitives.ReadUInt16BigEndian(data[(entry + 2)..]);
            if (at < 0 || at >= data.Length)
            {
                names.Add("");
                continue;
            }

            int end = data[at..].IndexOf((byte)0);
            names.Add(Ascii.Decode(data.Slice(at, end < 0 ? data.Length - at : end)));
        }

        return names;
    }

    /// <summary>Renders the model as the <c>model.txt</c> listing.</summary>
    public string Describe(string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {title}");
        text.AppendLine($"# J3D1 {Variant} ({(Variant == "bdl4" ? "BDL" : "BMD")}), subversion {Subversion}, " +
                        $"{DeclaredSize:N0} bytes, {Sections.Count} section(s)");
        text.AppendLine("#");
        text.AppendLine("# section  offset    size          contents");
        text.AppendLine("# -------  --------  ------------  --------------------------------------------");

        foreach (J3dSection section in Sections)
        {
            text.AppendLine($"  {section.Magic,-7}  0x{section.Offset:X6}  {section.Data.Length,12:N0}  " +
                            SectionSummary(section));
        }

        if (Hierarchy.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## scene graph (INF1)");
            foreach (J3dHierarchyNode node in Hierarchy)
            {
                string name = node.Type switch
                {
                    0x10 when node.Index < JointNames.Count => $" '{JointNames[node.Index]}'",
                    0x11 when node.Index < MaterialNames.Count => $" '{MaterialNames[node.Index]}'",
                    _ => "",
                };
                text.AppendLine($"  {new string(' ', node.Depth * 2)}{node.TypeName} {node.Index}{name}");
            }
        }

        if (JointNames.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## joints (JNT1), {JointNames.Count}");
            for (int i = 0; i < JointNames.Count; i++)
                text.AppendLine($"  [{i:D3}] '{JointNames[i]}'");
        }

        if (MaterialNames.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## materials (MAT3), {MaterialNames.Count}");
            for (int i = 0; i < MaterialNames.Count; i++)
                text.AppendLine($"  [{i:D3}] '{MaterialNames[i]}'");
        }

        if (Textures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## textures (TEX1), {Textures.Count}");
            for (int i = 0; i < Textures.Count; i++)
                text.AppendLine($"  [{i:D3}] {Textures[i].Name,-20} {Textures[i].Describe()}");
        }

        return text.ToString();
    }

    private string SectionSummary(J3dSection section) => section.Magic switch
    {
        "INF1" => $"scene graph, {Hierarchy.Count} node(s)",
        "VTX1" => "vertex arrays",
        "EVP1" => "skinning envelopes",
        "DRW1" => "draw matrix table",
        "JNT1" => $"{JointNames.Count} joint(s)",
        "SHP1" => $"{ShapeCount(section)} shape(s)",
        "MAT3" => $"{MaterialNames.Count} material(s)",
        "MDL3" => "pre-compiled draw calls",
        "TEX1" => $"{Textures.Count} texture(s)",
        _ => "unrecognised section, extracted verbatim",
    };

    /// <summary>Shape count, from the <c>u16</c> at 0x08 that every SHP1 section carries.</summary>
    private static int ShapeCount(J3dSection section) =>
        section.Data.Length >= 10 ? BinaryPrimitives.ReadUInt16BigEndian(section.Data.AsSpan(8)) : 0;
}

/// <summary>One top-level section of a <see cref="J3dModel"/>.</summary>
public sealed class J3dSection
{
    /// <summary>Position in the file, starting at zero.</summary>
    public required int Index { get; init; }

    /// <summary>The four-character magic, such as <c>TEX1</c>.</summary>
    public required string Magic { get; init; }

    /// <summary>Offset of the section within the file.</summary>
    public required int Offset { get; init; }

    /// <summary>The section's bytes, magic and size word included.</summary>
    public required byte[] Data { get; init; }
}

/// <summary>One node of a J3D scene graph.</summary>
public sealed class J3dHierarchyNode
{
    /// <summary>Nesting depth, from the open and close markers around it.</summary>
    public required int Depth { get; init; }

    /// <summary>Node type: 0x10 joint, 0x11 material, 0x12 shape.</summary>
    public required int Type { get; init; }

    /// <summary>Index into the section the type names.</summary>
    public required int Index { get; init; }

    /// <summary>Display name of <see cref="Type"/>.</summary>
    public string TypeName => Type switch
    {
        0x10 => "joint",
        0x11 => "material",
        0x12 => "shape",
        _ => $"type_0x{Type:X2}",
    };
}
