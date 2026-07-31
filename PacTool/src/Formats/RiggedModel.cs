using System.Text;

namespace PacTool.Formats;

/// <summary>One joint of an assembled skeleton.</summary>
public sealed class RiggedJoint
{
    /// <summary>Index in the skeleton, matching the joint indices in the skin weights.</summary>
    public required int Index { get; init; }

    /// <summary>Joint name.</summary>
    public required string Name { get; init; }

    /// <summary>Index of the parent joint, or -1 for the root.</summary>
    public required int Parent { get; init; }

    /// <summary>Depth in the tree, from the node type.</summary>
    public required int Depth { get; init; }

    /// <summary>Translation relative to the parent, from the rest pose.</summary>
    public (float X, float Y, float Z) Translation { get; set; }

    /// <summary>Scale, from the rest pose.</summary>
    public (float X, float Y, float Z) Scale { get; set; } = (1, 1, 1);

    /// <summary>The 3x4 inverse bind matrix, row-major, or null when no rest pose was found.</summary>
    public float[]? InverseBind { get; set; }

    /// <summary>True when a rest pose was found for this joint.</summary>
    public bool HasRestPose => InverseBind is not null;
}

/// <summary>
/// A character model with everything needed to pose it: the skeleton from the <c>.mpc</c>, the rest
/// pose from the <c>.scn</c>'s joint records, the skinned meshes from its shapes, and the motions
/// from the <c>.mpc</c> again.
///
/// The three files are joined by name. A <c>.pac</c> holds <c>m01xxxxx.scn</c> and
/// <c>m01xxxxx.mpc</c>; the skin weights index the <c>.mpc</c>'s joint table, and each joint's rest
/// transform is the <c>.scn</c> joint record with the same name.
/// </summary>
public sealed class RiggedModel
{
    /// <summary>Model name, normally the stem the two files share.</summary>
    public required string Name { get; init; }

    /// <summary>Joints in skeleton order.</summary>
    public required IReadOnlyList<RiggedJoint> Joints { get; init; }

    /// <summary>Skinned meshes, in scene order.</summary>
    public required IReadOnlyList<(string Name, ScnMesh Mesh)> Meshes { get; init; }

    /// <summary>Animations from the <c>.mpc</c>.</summary>
    public required IReadOnlyList<MpcMotion> Motions { get; init; }

    /// <summary>Texture names the scene references, in order.</summary>
    public required IReadOnlyList<string> TextureNames { get; init; }

    /// <summary>Non-fatal problems noticed while assembling.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Joints that got a rest pose.</summary>
    public int PosedJointCount => Joints.Count(j => j.HasRestPose);

    /// <summary>
    /// Assembles a model. <paramref name="scene"/> supplies the geometry and rest poses,
    /// <paramref name="skeleton"/> the joint table and the motions.
    /// </summary>
    public static RiggedModel Assemble(string name, SceneTable scene, MpcModel skeleton,
                                       IReadOnlyList<string> textureNames)
    {
        var warnings = new List<string>();
        var joints = BuildJoints(skeleton);

        // Rest poses are matched by name. A skeleton may repeat a name for a mirrored limb, so
        // each record is consumed once, in order, rather than by a dictionary lookup.
        var unused = scene.Nodes.ToList();
        foreach (RiggedJoint joint in joints)
        {
            int at = unused.FindIndex(n => n.Name == joint.Name);
            if (at < 0)
                continue;

            ScnNode node = unused[at];
            unused.RemoveAt(at);
            joint.Translation = node.Translation;
            joint.Scale = node.Scale;
            joint.InverseBind = node.InverseBind;
        }

        int missing = joints.Count(j => !j.HasRestPose);
        if (missing > 0)
            warnings.Add($"{missing} of {joints.Count} joint(s) have no rest pose in the scene table; they are exported at the origin.");

        var meshes = scene.Shapes
            .Where(s => s.Mesh is { Skin: not null })
            .Select(s => (s.FileStem, s.Mesh!))
            .ToList();

        foreach ((string meshName, ScnMesh mesh) in meshes)
        {
            int highest = mesh.Joints.Count > 0 ? mesh.Joints[^1] : -1;
            if (highest >= joints.Count)
                warnings.Add($"'{meshName}' references joint {highest} but the skeleton has {joints.Count}.");
        }

        return new RiggedModel
        {
            Name = name,
            Joints = joints,
            Meshes = meshes,
            Motions = skeleton.Motions,
            TextureNames = textureNames,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Turns the flat, depth-first joint table into a tree. The node type fixes each entry's depth,
    /// so a joint's parent is the last entry shallower than it.
    /// </summary>
    private static List<RiggedJoint> BuildJoints(MpcModel skeleton)
    {
        var joints = new List<RiggedJoint>(skeleton.Nodes.Count);
        var openAtDepth = new Dictionary<int, int>();

        foreach (MpcNode node in skeleton.Nodes)
        {
            int depth = node.Depth;
            int parent = -1;
            for (int above = depth - 1; above >= 0; above--)
            {
                if (openAtDepth.TryGetValue(above, out int candidate))
                {
                    parent = candidate;
                    break;
                }
            }

            joints.Add(new RiggedJoint
            {
                Index = joints.Count,
                Name = node.Name,
                Parent = parent,
                Depth = depth,
            });
            openAtDepth[depth] = joints.Count - 1;

            // Anything deeper than this joint now belongs to a closed branch.
            foreach (int deeper in openAtDepth.Keys.Where(k => k > depth).ToList())
                openAtDepth.Remove(deeper);
        }

        return joints;
    }

    /// <summary>Renders the skeleton as the <c>skeleton.txt</c> listing.</summary>
    public string DescribeSkeleton()
    {
        var text = new StringBuilder();
        text.AppendLine($"# skeleton of {Name}");
        text.AppendLine($"# {Joints.Count} joint(s), {PosedJointCount} with a rest pose, " +
                        $"{Meshes.Count} mesh(es), {Motions.Count} motion(s)");
        text.AppendLine("#");
        text.AppendLine("#  idx  parent  name             translation                    rest pose");
        text.AppendLine("# ----  ------  ---------------  -----------------------------  ---------");

        foreach (RiggedJoint joint in Joints)
        {
            string label = new string(' ', joint.Depth * 2) + joint.Name;
            string translation = $"({joint.Translation.X:0.###}, {joint.Translation.Y:0.###}, {joint.Translation.Z:0.###})";
            text.AppendLine($"  {joint.Index,4}  {joint.Parent,6}  {label,-15}  {translation,-29}  " +
                            (joint.HasRestPose ? "yes" : "no"));
        }

        return text.ToString();
    }
}
