using System.Buffers.Binary;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// A character model: the skeleton hierarchy plus the packed mesh data its parts point into.
/// Stored as a standalone <c>.mpc</c> file or as the payload of a <c>CAPR</c> member named
/// <c>*.mpc</c>; the textures it uses live in the <c>.scn</c> of the same stem.
///
/// <code>
///   0x00  u32       entryCount
///   0x04  u32       unknown     0xFD00 in most models, 0xF84B in others
///   0x08  entry     the root chain, in the same 0x18 layout as the entries below but type 0
///   0x20  entry[entryCount]
///   ...             packed mesh data, addressed by the geometry offsets in the entries
/// </code>
///
/// An entry is 0x18 bytes:
///
/// <code>
///   0x00  u16       flags       0 for most nodes
///   0x02  u8        type        see MpcNodeType
///   0x03  char[8]   name
///   0x0B  u8[5]     reserved
///   0x10  s16       reserved
///   0x12  s16       reserved
///   0x14  u16       geometry    offset into the mesh data in 16-bit words; 0 means no mesh
///   0x16  s16       parameter   blend weight on effect nodes
/// </code>
///
/// Entries are a depth-first walk of the skeleton and the type encodes the depth, so the tree can
/// be rebuilt from the sequence alone. The mesh data itself is left as bytes: it is GX vertex data
/// whose attribute descriptors are not in this file.
/// </summary>
public sealed class MpcModel
{
    /// <summary>Offset of the first skeleton entry.</summary>
    public const int EntryTableOffset = 0x20;

    /// <summary>Size of one skeleton entry.</summary>
    public const int EntrySize = 0x18;

    /// <summary>Offset of the entry-shaped record in the header, which is joint 0.</summary>
    public const int RootRecordOffset = 0x08;

    /// <summary>Name of the root chain node, from the entry-shaped record in the header.</summary>
    public string RootName { get; }

    /// <summary>Header word at 0x04, whose meaning is not established.</summary>
    public uint Unknown { get; }

    /// <summary>Skeleton entries in stored (depth-first) order.</summary>
    public IReadOnlyList<MpcNode> Nodes { get; }

    /// <summary>Animations stored after the skeleton.</summary>
    public IReadOnlyList<MpcMotion> Motions { get; }

    /// <summary>The skeleton table, verbatim.</summary>
    public byte[] SkeletonData { get; }

    /// <summary>Everything after the skeleton: the motion directory and the motions themselves.</summary>
    public byte[] MeshData { get; }

    /// <summary>Non-fatal oddities noticed while parsing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    private MpcModel(string rootName, uint unknown, List<MpcNode> nodes, List<MpcMotion> motions,
                     byte[] skeleton, byte[] mesh, List<string> warnings)
    {
        RootName = rootName;
        Unknown = unknown;
        Nodes = nodes;
        Motions = motions;
        SkeletonData = skeleton;
        MeshData = mesh;
        Warnings = warnings;
    }

    /// <summary>True if <paramref name="data"/> plausibly starts with an MPC header.</summary>
    public static bool LooksLikeModel(ReadOnlySpan<byte> data)
    {
        if (data.Length < EntryTableOffset + EntrySize)
            return false;

        uint stored = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (stored < 2 || stored > 0x1000 || EntryTableOffset + (long)(stored - 1) * EntrySize > data.Length)
            return false;

        // The first entry is always the single root bone, type 1, with a name.
        return data[EntryTableOffset + 2] == (byte)MpcNodeType.RootBone &&
               Ascii.IsPrintableName(data.Slice(EntryTableOffset + 3, 8));
    }

    /// <summary>Parses an MPC model.</summary>
    public static MpcModel Parse(ReadOnlySpan<byte> data, string sourceName)
    {
        if (data.Length < EntryTableOffset)
            throw new PacFormatException($"{sourceName}: {data.Length} bytes is too short for an MPC header.");

        // The stored count includes the entry-shaped root chain record in the header, so the
        // table itself holds one fewer. Reading it as the table length runs a whole entry past the
        // end and into the motion directory.
        long count = Math.Max(0, (long)BinaryPrimitives.ReadUInt32BigEndian(data) - 1);
        uint unknown = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var warnings = new List<string>();

        long skeletonEnd = EntryTableOffset + count * EntrySize;
        if (count > 0x10000 || skeletonEnd > data.Length)
        {
            warnings.Add($"entry count {count} needs 0x{skeletonEnd:X} bytes but only 0x{data.Length:X} are present; truncating.");
            count = Math.Max(0, (data.Length - EntryTableOffset) / EntrySize);
            skeletonEnd = EntryTableOffset + count * EntrySize;
        }

        // Joint 0 is the entry-shaped record in the header, not the first table entry: the skin
        // weights in the paired .scn index it, and its name appears among that file's rest poses
        // in all 178 models that have both. Everything in the table hangs below it, so the table's
        // own depths shift down by one.
        var nodes = new List<MpcNode>((int)count + 1)
        {
            new()
            {
                Index = 0,
                Flags = BinaryPrimitives.ReadUInt16BigEndian(data[RootRecordOffset..]),
                Type = MpcNodeType.RootChain,
                Name = Ascii.Decode(data.Slice(RootRecordOffset + 3, 8)),
                GeometryWord = 0,
                Parameter = 0,
                DepthOverride = 0,
                Raw = data.Slice(RootRecordOffset, EntrySize).ToArray(),
            },
        };

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(EntryTableOffset + i * EntrySize, EntrySize);
            var node = new MpcNode
            {
                Index = nodes.Count,
                Flags = BinaryPrimitives.ReadUInt16BigEndian(entry),
                Type = (MpcNodeType)entry[2],
                Name = Ascii.Decode(entry[3..11]),
                GeometryWord = BinaryPrimitives.ReadUInt16BigEndian(entry[0x14..]),
                Parameter = BinaryPrimitives.ReadInt16BigEndian(entry[0x16..]),
                Raw = entry.ToArray(),
            };
            node.DepthOverride = node.TypeDepth + 1;
            nodes.Add(node);
        }

        // The motions animate the table joints; joint 0, the header chain, is driven by the
        // whole-model track instead.
        List<MpcMotion> motions = MpcMotion.ParseAll(data, (int)skeletonEnd, nodes.Count - 1, warnings);

        return new MpcModel(
            Ascii.Decode(data.Slice(0x0B, 8)),
            unknown,
            nodes,
            motions,
            data[EntryTableOffset..(int)skeletonEnd].ToArray(),
            data[(int)skeletonEnd..].ToArray(),
            warnings);
    }

    /// <summary>Renders the skeleton as the <c>skeleton.txt</c> listing.</summary>
    public string DescribeSkeleton(string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"# skeleton of {title}");
        text.AppendLine($"# root chain '{RootName}', {Nodes.Count} node(s), {MeshData.Length:N0} bytes of mesh data");
        text.AppendLine("#");
        text.AppendLine("# Indentation follows the node type, which encodes the depth of the depth-first walk.");
        text.AppendLine("# 'chain' nodes only connect; 'part' nodes reference a mesh; 'eff' nodes carry a blend weight.");
        text.AppendLine("#");
        text.AppendLine("#  idx  type          name            detail");
        text.AppendLine("# ----  ------------  --------------  ---------------------------------------");

        foreach (MpcNode node in Nodes)
        {
            string indent = new(' ', node.Depth * 2);
            string detail = node.Category switch
            {
                MpcNodeCategory.Effect => $"parameter={node.Parameter:+#;-#;0}",
                MpcNodeCategory.Chain => "connector, no mesh",
                _ when node.GeometryWord != 0 => $"mesh at word 0x{node.GeometryWord:X4} (byte 0x{node.GeometryWord * 2:X5})",
                _ => "",
            };
            if (node.Flags != 0)
                detail = detail.Length > 0 ? $"{detail}  flags=0x{node.Flags:X4}" : $"flags=0x{node.Flags:X4}";

            string label = $"{indent}'{node.Name}'";
            text.AppendLine($"  {node.Index,4}  {node.TypeName,-12}  {label,-16}  {detail}".TrimEnd());
        }

        return text.ToString();
    }
}

/// <summary>
/// Node types seen in the reference data. The value both names the node's role and fixes its depth
/// in the tree, so the hierarchy is recoverable from the flat sequence.
/// </summary>
public enum MpcNodeType
{
    /// <summary>The record in the header: the chain every other joint hangs below.</summary>
    RootChain = 0x00,

    /// <summary>The single root bone, always the first table entry.</summary>
    RootBone = 0x01,

    /// <summary>Effect node directly under the root.</summary>
    EffectTop = 0x02,

    /// <summary>Connector at depth 1.</summary>
    ChainA = 0x03,

    /// <summary>Mesh part at depth 2.</summary>
    PartA = 0x04,

    /// <summary>Effect node at depth 3.</summary>
    EffectA = 0x05,

    /// <summary>Connector at depth 2.</summary>
    ChainB = 0x06,

    /// <summary>Mesh part at depth 3.</summary>
    PartB = 0x07,

    /// <summary>Effect node at depth 4.</summary>
    EffectB = 0x08,

    /// <summary>Connector at depth 3.</summary>
    ChainC = 0x09,

    /// <summary>Mesh part at depth 4.</summary>
    PartC = 0x0A,

    /// <summary>Second mesh part at depth 4.</summary>
    PartC2 = 0x0B,

    /// <summary>Effect node at depth 5.</summary>
    EffectC = 0x0C,

    /// <summary>Connector at depth 4.</summary>
    ChainD = 0x0D,

    /// <summary>Mesh part at depth 5.</summary>
    PartD = 0x0E,

    /// <summary>Effect node at depth 6.</summary>
    EffectD = 0x0F,
}

/// <summary>Broad role of an <see cref="MpcNode"/>.</summary>
public enum MpcNodeCategory
{
    /// <summary>The root bone.</summary>
    Bone,

    /// <summary>A pure connector with no mesh of its own.</summary>
    Chain,

    /// <summary>A node that references mesh data.</summary>
    Part,

    /// <summary>A node carrying a blend or transform parameter.</summary>
    Effect,

    /// <summary>A type value not seen in the reference data.</summary>
    Unknown,
}

/// <summary>One skeleton entry of an <see cref="MpcModel"/>.</summary>
public sealed class MpcNode
{
    /// <summary>Position in the depth-first walk.</summary>
    public required int Index { get; init; }

    /// <summary>Field at 0x00, non-zero on a handful of nodes.</summary>
    public required ushort Flags { get; init; }

    /// <summary>Node type, which also fixes <see cref="Depth"/>.</summary>
    public required MpcNodeType Type { get; init; }

    /// <summary>Bone or part name.</summary>
    public required string Name { get; init; }

    /// <summary>Offset of this node's mesh in the mesh data, in 16-bit words. Zero means no mesh.</summary>
    public required ushort GeometryWord { get; init; }

    /// <summary>Blend weight on effect nodes.</summary>
    public required short Parameter { get; init; }

    /// <summary>The entry's bytes.</summary>
    public required byte[] Raw { get; init; }

    /// <summary>Role of this node.</summary>
    public MpcNodeCategory Category => Type switch
    {
        MpcNodeType.RootChain => MpcNodeCategory.Chain,
        MpcNodeType.RootBone => MpcNodeCategory.Bone,
        MpcNodeType.ChainA or MpcNodeType.ChainB or MpcNodeType.ChainC or MpcNodeType.ChainD => MpcNodeCategory.Chain,
        MpcNodeType.PartA or MpcNodeType.PartB or MpcNodeType.PartC or MpcNodeType.PartC2 or MpcNodeType.PartD => MpcNodeCategory.Part,
        MpcNodeType.EffectTop or MpcNodeType.EffectA or MpcNodeType.EffectB or MpcNodeType.EffectC or MpcNodeType.EffectD => MpcNodeCategory.Effect,
        _ => MpcNodeCategory.Unknown,
    };

    /// <summary>Depth in the skeleton tree. Set when the model is parsed.</summary>
    public int Depth => DepthOverride ?? TypeDepth;

    /// <summary>Depth assigned at parse time, which offsets the table below the root joint.</summary>
    public int? DepthOverride { get; set; }

    /// <summary>Depth the node type on its own implies.</summary>
    public int TypeDepth => Type switch
    {
        MpcNodeType.RootChain or MpcNodeType.RootBone => 0,
        MpcNodeType.EffectTop or MpcNodeType.ChainA => 1,
        MpcNodeType.PartA or MpcNodeType.ChainB => 2,
        MpcNodeType.EffectA or MpcNodeType.PartB or MpcNodeType.ChainC => 3,
        MpcNodeType.EffectB or MpcNodeType.PartC or MpcNodeType.PartC2 or MpcNodeType.ChainD => 4,
        MpcNodeType.EffectC or MpcNodeType.PartD => 5,
        MpcNodeType.EffectD => 6,
        _ => 2,
    };

    /// <summary>Display name of <see cref="Type"/>.</summary>
    public string TypeName => Enum.IsDefined(Type) ? Type.ToString() : $"type_0x{(int)Type:X2}";
}
