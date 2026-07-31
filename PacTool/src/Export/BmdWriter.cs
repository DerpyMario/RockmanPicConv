using PacTool.Formats;
using PacTool.Gx;

namespace PacTool.Export;

/// <summary>
/// Writes a J3D model: <c>J3D1 bmd3</c>, or <c>bdl4</c> when asked, with the eight sections a
/// rigged and textured model needs - <c>INF1</c>, <c>VTX1</c>, <c>EVP1</c>, <c>DRW1</c>,
/// <c>JNT1</c>, <c>SHP1</c>, <c>MAT3</c> and <c>TEX1</c>, in that order, because readers rely on
/// <c>TEX1</c> being last. The <c>bdl4</c> variant normally also carries <c>MDL3</c>, a cache of
/// precompiled register writes; it is not written here, and readers rebuild that state from
/// <c>MAT3</c> anyway.
///
/// This is the reverse of <see cref="J3dModel"/>, and the two are checked against each other: a
/// model written here is read back by that class in the tests, which is the only end-to-end check
/// available here since no BMD ships with this game to compare against.
///
/// Geometry is emitted the way J3D stores it - shared vertex arrays plus display lists of indices,
/// rather than the flat per-vertex streams the source uses - so vertices are welded on the way out.
/// Skinning goes through the usual pair of tables: <c>EVP1</c> holds the weight sets and the inverse
/// bind matrices, <c>DRW1</c> says whether a matrix slot is a weight set or a plain joint, and each
/// corner of a triangle carries a matrix index.
/// </summary>
public static class BmdWriter
{
    /// <summary>Builds a <c>.bmd</c> (or <c>.bdl</c>) for a rigged character model.</summary>
    /// <param name="model">The assembled model.</param>
    /// <param name="textures">Textures to embed, in the order the materials reference them.</param>
    /// <param name="binaryDisplayLists">Write the <c>bdl4</c> variant tag rather than <c>bmd3</c>.</param>
    public static byte[] Build(RiggedModel model, IReadOnlyList<PicturePackTexture> textures,
                               bool binaryDisplayLists = false)
    {
        var geometry = BmdGeometry.Build(model);

        var sections = new List<byte[]>
        {
            Inf1(model, geometry),
            Vtx1(geometry),
            Evp1(model, geometry),
            Drw1(geometry),
            Jnt1(model),
            Shp1(geometry),
            Mat3(model, textures.Count),
            Tex1(textures),
        };

        var file = new J3dWriter();
        file.Ascii("J3D1").Ascii(binaryDisplayLists ? "bdl4" : "bmd3");
        file.U32(0).U32(sections.Count);
        file.Ascii("SVR3").Fill(12, 0xFF);
        foreach (byte[] section in sections)
            file.Bytes(section);

        file.PatchU32(8, file.Length);
        return file.ToArray();
    }

    /// <summary>
    /// The scene graph: 0x01 opens a level, 0x02 closes it, 0x10 names a joint, 0x11 a material,
    /// 0x12 a shape, and 0x00 ends the list.
    ///
    /// The graph has exactly one node at the top - readers take that node as the root and report an
    /// error otherwise - so the joint tree hangs under joint 0 and the shapes hang there too, each
    /// below the material that draws it.
    /// </summary>
    private static byte[] Inf1(RiggedModel model, BmdGeometry geometry)
    {
        var w = new J3dWriter();
        w.Ascii("INF1").U32(0);
        w.U16(0).U16(0xFFFF);                       // scene loading flags
        w.U32(geometry.PacketCount);
        w.U32(geometry.Vertices.Count);
        int offset = w.Length;
        w.U32(0);
        w.Align(32);
        w.PatchU32(offset, w.Length);

        var children = new List<List<int>>();
        for (int i = 0; i < model.Joints.Count; i++)
            children.Add([]);

        // Anything the skeleton left parentless other than joint 0 is attached to it, so the graph
        // keeps its single root whatever the joint table says.
        for (int i = 1; i < model.Joints.Count; i++)
        {
            int parent = model.Joints[i].Parent;
            children[parent >= 0 && parent < i ? parent : 0].Add(i);
        }

        if (model.Joints.Count > 0)
        {
            EmitJoint(w, 0, children, geometry, model.Joints.Count);
        }
        else
        {
            // No skeleton at all: the shapes still need a root to hang from.
            w.U16(0x11).U16(0);
            w.U16(0x01).U16(0);
            for (int i = 0; i < geometry.Shapes.Count; i++)
                w.U16(0x12).U16(i);

            w.U16(0x02).U16(0);
        }

        w.U16(0).U16(0);                            // terminator
        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>Writes one joint and, below it, its children - and at the root, the shapes.</summary>
    private static void EmitJoint(J3dWriter w, int joint, List<List<int>> children,
                                  BmdGeometry geometry, int jointCount)
    {
        w.U16(0x10).U16(joint);

        bool shapesHere = joint == 0 && geometry.Shapes.Count > 0;
        if (children[joint].Count == 0 && !shapesHere)
            return;

        w.U16(0x01).U16(0);
        foreach (int child in children[joint])
            EmitJoint(w, child, children, geometry, jointCount);

        for (int i = 0; i < geometry.Shapes.Count && shapesHere; i++)
        {
            w.U16(0x11).U16(i);
            w.U16(0x01).U16(0);
            w.U16(0x12).U16(i);
            w.U16(0x02).U16(0);
        }

        w.U16(0x02).U16(0);
    }

    /// <summary>
    /// The vertex arrays: position, normal and one texture coordinate, all as floats. The thirteen
    /// offsets are fixed slots - position, normal, normal-binormal-tangent, two colours, then eight
    /// texture coordinates - so texture coordinate zero is slot five, not slot four.
    /// </summary>
    private static byte[] Vtx1(BmdGeometry geometry)
    {
        const int PositionSlot = 0;
        const int NormalSlot = 1;
        const int TexCoord0Slot = 5;

        var w = new J3dWriter();
        w.Ascii("VTX1").U32(0);
        int formatOffset = w.Length;
        w.U32(0);
        int arrayOffsets = w.Length;
        for (int i = 0; i < 13; i++)
            w.U32(0);

        // The format list follows the 64-byte header directly, which is also where the offset
        // written here points.
        w.PatchU32(formatOffset, w.Length);

        // Attribute, component count, component type, fixed-point shift.
        w.U32(9).U32(1).U32(4).U8(0).Fill(3, 0xFF);     // position, XYZ, float
        w.U32(10).U32(0).U32(4).U8(0).Fill(3, 0xFF);    // normal, XYZ, float
        w.U32(13).U32(1).U32(4).U8(0).Fill(3, 0xFF);    // texcoord 0, ST, float
        w.U32(255).U32(1).U32(0).U8(0).Fill(3, 0xFF);   // terminator
        w.Align(32);

        w.PatchU32(arrayOffsets + PositionSlot * 4, w.Length);
        foreach (BmdVertex v in geometry.Vertices)
            w.F32(v.X).F32(v.Y).F32(v.Z);
        w.Align(32);

        w.PatchU32(arrayOffsets + NormalSlot * 4, w.Length);
        foreach (BmdVertex v in geometry.Vertices)
            w.F32(v.Nx).F32(v.Ny).F32(v.Nz);
        w.Align(32);

        w.PatchU32(arrayOffsets + TexCoord0Slot * 4, w.Length);
        foreach (BmdVertex v in geometry.Vertices)
            w.F32(v.U).F32(v.V);
        w.Align(32);

        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>
    /// The weight sets and the inverse bind matrices. Each set lists how many joints influence it,
    /// then their indices and weights.
    /// </summary>
    private static byte[] Evp1(RiggedModel model, BmdGeometry geometry)
    {
        var w = new J3dWriter();
        w.Ascii("EVP1").U32(0);
        w.U16(geometry.WeightSets.Count).U16(0xFFFF);
        int offsets = w.Length;
        w.U32(0).U32(0).U32(0).U32(0);
        w.Align(32);

        w.PatchU32(offsets, w.Length);                  // influence count per set
        foreach (int[][] set in geometry.WeightSets)
            w.U8(set.Length);
        w.Align(4);

        w.PatchU32(offsets + 4, w.Length);              // joint index per influence
        foreach (int[][] set in geometry.WeightSets)
        {
            foreach (int[] influence in set)
                w.U16(influence[0]);
        }

        w.Align(4);
        w.PatchU32(offsets + 8, w.Length);              // weight per influence
        foreach (int[][] set in geometry.WeightSets)
        {
            foreach (int[] influence in set)
                w.F32(influence[1] / 128f);
        }

        w.Align(32);
        w.PatchU32(offsets + 12, w.Length);             // inverse bind matrix per joint
        foreach (RiggedJoint joint in model.Joints)
        {
            float[] m = joint.InverseBind ?? Identity3X4;
            foreach (float value in m)
                w.F32(value);
        }

        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>The matrix table: for each slot, whether it is a weight set or a plain joint.</summary>
    private static byte[] Drw1(BmdGeometry geometry)
    {
        var w = new J3dWriter();
        w.Ascii("DRW1").U32(0);
        w.U16(geometry.MatrixTable.Count).U16(0xFFFF);
        int offsets = w.Length;
        w.U32(0).U32(0);
        w.Align(32);

        w.PatchU32(offsets, w.Length);
        foreach ((bool weighted, int _) in geometry.MatrixTable)
            w.U8(weighted ? 1 : 0);
        w.Align(2);

        w.PatchU32(offsets + 4, w.Length);
        foreach ((bool _, int index) in geometry.MatrixTable)
            w.U16(index);

        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>The joints: scale, rotation, translation and a bounding volume each.</summary>
    private static byte[] Jnt1(RiggedModel model)
    {
        var w = new J3dWriter();
        w.Ascii("JNT1").U32(0);
        w.U16(model.Joints.Count).U16(0xFFFF);
        int offsets = w.Length;
        w.U32(0).U32(0).U32(0);
        w.Align(32);

        w.PatchU32(offsets, w.Length);
        foreach (RiggedJoint joint in model.Joints)
        {
            w.U16(0).U16(0x00FF);                       // matrix type, then the usual padding
            w.F32(joint.Scale.X).F32(joint.Scale.Y).F32(joint.Scale.Z);
            w.S16(0).S16(0).S16(0).U16(0xFFFF);         // rotation, in 16-bit angles, plus padding
            w.F32(joint.Translation.X).F32(joint.Translation.Y).F32(joint.Translation.Z);
            w.F32(0);                                   // bounding sphere radius
            w.F32(0).F32(0).F32(0);                     // bounding box minimum
            w.F32(0).F32(0).F32(0);                     // bounding box maximum
        }

        w.Align(4);
        w.PatchU32(offsets + 4, w.Length);              // index table, identity here
        for (int i = 0; i < model.Joints.Count; i++)
            w.U16(i);

        w.Align(4);
        w.PatchU32(offsets + 8, w.Length);
        w.StringTable(Unique(model.Joints.Select(j => j.Name).ToList()));

        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>
    /// The shapes. Each is a batch descriptor naming its packets; each packet is a run of GX
    /// primitives plus the slice of the matrix table it loads.
    ///
    /// <code>
    ///   0x0C  u32  batch descriptors
    ///   0x10  u32  batch index table
    ///   0x14  u32  batch names, zero here
    ///   0x18  u32  vertex descriptor lists
    ///   0x1C  u32  matrix table
    ///   0x20  u32  primitive data
    ///   0x24  u32  matrix data, one record per packet
    ///   0x28  u32  packet locations, one record per packet
    /// </code>
    /// </summary>
    private static byte[] Shp1(BmdGeometry geometry)
    {
        var w = new J3dWriter();
        w.Ascii("SHP1").U32(0);
        w.U16(geometry.Shapes.Count).U16(0xFFFF);
        int offsets = w.Length;
        for (int i = 0; i < 8; i++)
            w.U32(0);

        w.Align(32);
        int batchTable = w.Length;
        w.PatchU32(offsets, batchTable);
        w.Fill(geometry.Shapes.Count * BatchSize);

        w.Align(4);
        w.PatchU32(offsets + 4, w.Length);              // index table
        for (int i = 0; i < geometry.Shapes.Count; i++)
            w.U16(i);

        w.Align(4);
        w.PatchU32(offsets + 8, 0);                     // no per-shape names

        w.Align(32);
        w.PatchU32(offsets + 12, w.Length);
        // One descriptor list, shared: a matrix index then indexed position, normal and texcoord.
        w.U32(0).U32(1);                                // PosMatIdx, direct 8-bit
        w.U32(9).U32(3);                                // position, index16
        w.U32(10).U32(3);                               // normal, index16
        w.U32(13).U32(3);                               // texcoord 0, index16
        w.U32(255).U32(0);                              // terminator
        w.Align(32);

        // The matrix table is the concatenation of every packet's slots; a matrix data record says
        // which slice of it a packet loads.
        w.PatchU32(offsets + 16, w.Length);
        var firstMatrix = new List<int>();
        int matrixCursor = 0;
        foreach (BmdShape shape in geometry.Shapes)
        {
            foreach (BmdPacket packet in shape.Packets)
            {
                firstMatrix.Add(matrixCursor);
                foreach (int slot in packet.MatrixSlots)
                    w.U16(slot);

                matrixCursor += packet.MatrixSlots.Count;
            }
        }

        w.Align(32);
        int primitives = w.Length;
        w.PatchU32(offsets + 20, primitives);
        var spans = new List<(int Offset, int Size)>();
        foreach (BmdShape shape in geometry.Shapes)
        {
            foreach (BmdPacket packet in shape.Packets)
            {
                int start = w.Length;
                WritePacket(w, packet);
                w.Align(32);
                spans.Add((start - primitives, w.Length - start));
            }
        }

        w.Align(32);
        w.PatchU32(offsets + 24, w.Length);             // matrix data, one record per packet
        for (int i = 0, packet = 0; i < geometry.Shapes.Count; i++)
        {
            foreach (BmdPacket p in geometry.Shapes[i].Packets)
            {
                w.U16(0xFFFF).U16(p.MatrixSlots.Count).U32(firstMatrix[packet]);
                packet++;
            }
        }

        w.Align(4);
        w.PatchU32(offsets + 28, w.Length);             // packet locations, one record per packet
        foreach ((int offset, int size) in spans)
            w.U32(size).U32(offset);

        // Fill the batch descriptors now that the packet numbering is settled.
        int firstPacket = 0;
        for (int i = 0; i < geometry.Shapes.Count; i++)
        {
            int at = batchTable + i * BatchSize;
            w.PatchU16(at, 0x00FF);                     // matrix type 0, then padding
            w.PatchU16(at + 2, geometry.Shapes[i].Packets.Count);
            w.PatchU16(at + 4, 0);                      // descriptor list offset, shared
            w.PatchU16(at + 6, firstPacket);            // first matrix data record
            w.PatchU16(at + 8, firstPacket);            // first packet location
            w.PatchU16(at + 10, 0xFFFF);
            for (int f = 0; f < 7; f++)
                w.PatchU32(at + 12 + f * 4, 0);         // radius and bounding box

            firstPacket += geometry.Shapes[i].Packets.Count;
        }

        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>Size of a SHP1 batch descriptor.</summary>
    private const int BatchSize = 0x28;

    /// <summary>
    /// Writes one packet's primitives. Triangles are batched into as few draw calls as the 16-bit
    /// vertex count allows, and the run is closed with the zero opcode a reader stops on.
    ///
    /// The matrix index is a hardware address rather than a slot number: the transform unit holds
    /// each matrix as three rows, so slot n is addressed as 3n.
    /// </summary>
    private static void WritePacket(J3dWriter w, BmdPacket packet)
    {
        const int MaxVertices = 0xFFFF / 3 * 3;

        for (int at = 0; at < packet.Triangles.Count;)
        {
            int count = Math.Min(packet.Triangles.Count - at, MaxVertices / 3);
            w.U8(0x90).U16(count * 3);                  // draw triangles, vertex attribute table 0
            for (int i = 0; i < count; i++)
            {
                foreach (BmdCorner corner in packet.Triangles[at + i])
                {
                    w.U8(packet.LocalMatrix(corner.Slot) * 3);
                    w.U16(corner.Vertex).U16(corner.Vertex).U16(corner.Vertex);
                }
            }

            at += count;
        }

        w.U8(0);                                        // end of the primitive run
    }

    /// <summary>
    /// The materials. J3D stores a material as a 332-byte record of indices into per-field tables,
    /// so writing one means writing every table it indexes as well; each is given a single entry
    /// that every material shares. The result is one flat-lit, texture-modulated material per shape.
    ///
    /// The thirty section offsets must ascend, because a reader sizes each table by the distance to
    /// the next one that is set. Tables the record never indexes are left at zero and their indices
    /// at 0xFFFF, which is how J3D spells "none".
    /// </summary>
    private static byte[] Mat3(RiggedModel model, int textureCount)
    {
        int materials = Math.Max(1, model.Meshes.Count);
        int textures = Math.Max(1, textureCount);

        var w = new J3dWriter();
        w.Ascii("MAT3").U32(0);
        w.U16(materials).U16(0xFFFF);
        int offsets = w.Length;
        for (int i = 0; i < 30; i++)
            w.U32(0);

        w.Align(32);
        Table(w, offsets, 0);                           // the material records
        for (int i = 0; i < materials; i++)
            MaterialRecord(w, textureCount > 0);

        w.Align(4);
        Table(w, offsets, 1);                           // material index table
        for (int i = 0; i < materials; i++)
            w.U16(i);

        w.Align(4);
        Table(w, offsets, 2);
        w.StringTable(Unique(Enumerable.Range(0, materials)
            .Select(i => i < model.Meshes.Count ? model.Meshes[i].Name : $"material{i}").ToList()));

        w.Align(4);
        Table(w, offsets, 3);                           // indirect texturing, one block per material
        for (int i = 0; i < materials; i++)
            IndirectTexturing(w);

        w.Align(4);
        Table(w, offsets, 4);
        w.U32(0);                                       // cull mode: none, since strip winding varies

        Table(w, offsets, 5);
        w.U8(0xFF).U8(0xFF).U8(0xFF).U8(0xFF);          // material colour: white

        Table(w, offsets, 6);
        w.U8(1);                                        // one colour channel

        w.Align(4);
        Table(w, offsets, 7);
        w.U8(0).U8(0).U8(0).U8(0).U8(2).U8(0).U8(0xFF).U8(0xFF);
        // lighting off, both sources the register, no lights, no attenuation

        Table(w, offsets, 8);
        w.U8(0xFF).U8(0xFF).U8(0xFF).U8(0xFF);          // ambient colour: white

        Table(w, offsets, 10);
        w.U8(1);                                        // one texture coordinate generator

        w.Align(4);
        Table(w, offsets, 11);
        w.U8(1).U8(4).U8(60).U8(0xFF);                  // 2x4 matrix from texcoord 0, identity matrix

        w.Align(4);
        Table(w, offsets, 13);
        TextureMatrix(w);

        Table(w, offsets, 15);                          // texture stage to TEX1 index
        for (int i = 0; i < textures; i++)
            w.U16(i);

        w.Align(4);
        Table(w, offsets, 16);
        w.U8(0).U8(0).U8(4).U8(0xFF);                   // texcoord 0, texmap 0, colour channel 0

        Table(w, offsets, 17);
        w.S16(0).S16(0).S16(0).S16(0);                  // TEV register colour

        Table(w, offsets, 18);
        w.U8(0xFF).U8(0xFF).U8(0xFF).U8(0xFF);          // constant colour: white

        Table(w, offsets, 19);
        w.U8(1);                                        // one TEV stage

        w.Align(4);
        Table(w, offsets, 20);
        // Pass the texture straight through: out = d, with d the texture colour and alpha.
        w.U8(0xFF);
        w.U8(15).U8(15).U8(15).U8(8);                   // colour in: zero, zero, zero, texture
        w.U8(0).U8(0).U8(0).U8(1).U8(0);                // add, no bias, unit scale, clamp, to prev
        w.U8(7).U8(7).U8(7).U8(4);                      // alpha in: zero, zero, zero, texture
        w.U8(0).U8(0).U8(0).U8(1).U8(0);
        w.U8(0xFF);

        Table(w, offsets, 21);
        w.U8(0).U8(0).U8(0xFF).U8(0xFF);                // no swapping

        Table(w, offsets, 22);                          // the four standard swap tables
        w.U8(0).U8(1).U8(2).U8(3);
        w.U8(0).U8(0).U8(0).U8(3);
        w.U8(1).U8(1).U8(1).U8(3);
        w.U8(2).U8(2).U8(2).U8(3);

        Table(w, offsets, 23);
        Fog(w);

        Table(w, offsets, 24);
        w.U8(7).U8(0).U8(0).U8(7).U8(0).Fill(3, 0xFF);  // alpha test: always passes

        Table(w, offsets, 25);
        w.U8(1).U8(4).U8(5).U8(3);                      // blend source alpha over one minus it

        Table(w, offsets, 26);
        w.U8(1).U8(3).U8(1).U8(0xFF);                   // depth test on, less or equal, writes on

        Table(w, offsets, 27);
        w.U8(0);                                        // depth test after texturing

        Table(w, offsets, 28);
        w.U8(0);                                        // no dithering

        w.Align(4);
        Table(w, offsets, 29);
        w.U8(0).Fill(3, 0xFF).F32(1).F32(1).F32(1);     // normal-binormal-tangent scale, unused

        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    /// <summary>Records where a MAT3 sub-table starts.</summary>
    private static void Table(J3dWriter w, int offsets, int index) =>
        w.PatchU32(offsets + index * 4, w.Length);

    /// <summary>
    /// One material record. Every index points at entry zero of its table, apart from the ones that
    /// name something this exporter does not use, which are 0xFFFF.
    /// </summary>
    private static void MaterialRecord(J3dWriter w, bool textured)
    {
        w.U8(1).U8(0).U8(0).U8(0);                      // draw order, cull, colour channels, texgens
        w.U8(0).U8(0).U8(0).U8(0);                      // TEV stages, depth compare location, depth, dither
        w.U16(0).U16(0xFFFF);                           // material colours
        w.U16(0).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF);   // colour channel controls
        w.U16(0).U16(0xFFFF);                           // ambient colours
        for (int i = 0; i < 8; i++)
            w.U16(0xFFFF);                              // lights

        w.U16(0);                                       // texture coordinate generator 0
        for (int i = 1; i < 8; i++)
            w.U16(0xFFFF);
        for (int i = 0; i < 8; i++)
            w.U16(0xFFFF);                              // post-transform generators

        for (int i = 0; i < 10; i++)
            w.U16(0xFFFF);                              // texture matrices, identity via the generator
        for (int i = 0; i < 20; i++)
            w.U16(0xFFFF);                              // post-transform matrices

        w.U16(textured ? 0 : 0xFFFF);                   // texture stage 0
        for (int i = 1; i < 8; i++)
            w.U16(0xFFFF);

        for (int i = 0; i < 4; i++)
            w.U16(0xFFFF);                              // constant colours
        for (int i = 0; i < 16; i++)
            w.U8(0x0C);                                 // constant colour selectors
        for (int i = 0; i < 16; i++)
            w.U8(0x1C);                                 // constant alpha selectors

        w.U16(0);                                       // TEV order 0
        for (int i = 1; i < 16; i++)
            w.U16(0xFFFF);
        for (int i = 0; i < 4; i++)
            w.U16(0xFFFF);                              // TEV register colours

        w.U16(0);                                       // TEV stage 0
        for (int i = 1; i < 16; i++)
            w.U16(0xFFFF);

        w.U16(0);                                       // TEV swap mode 0
        for (int i = 1; i < 16; i++)
            w.U16(0xFFFF);
        for (int i = 0; i < 4; i++)
            w.U16(i);                                   // the four swap tables

        for (int i = 0; i < 12; i++)
            w.U16(0xFFFF);                              // unidentified, 0xFFFF throughout

        w.U16(0).U16(0).U16(0).U16(0);                  // fog, alpha test, blend, NBT scale
    }

    /// <summary>
    /// An indirect texturing block. Indirect texturing is off here, so this is the neutral block
    /// readers expect: a half-scale matrix and disabled stages.
    /// </summary>
    private static void IndirectTexturing(J3dWriter w)
    {
        w.U16(0);
        for (int i = 0; i < 9; i++)
            w.U16(0xFFFF);

        for (int i = 0; i < 3; i++)
        {
            w.F32(0.5f).F32(0).F32(0).F32(0).F32(0.5f).F32(0);
            w.U8(1).U8(0xFF).U8(0xFF).U8(0xFF);
        }

        for (int i = 0; i < 4; i++)
            w.U32(0x0000FFFF);

        for (int i = 0; i < 16; i++)
            w.U16(0).U16(0).U16(0).U16(0).U16(0x00FF).U16(0xFFFF);
    }

    /// <summary>A texture matrix: an identity transform with no rotation, scaling or offset.</summary>
    private static void TextureMatrix(J3dWriter w)
    {
        w.U16(0x0100).U16(0xFFFF);
        w.F32(0.5f).F32(0.5f);                          // centre of the scale
        w.F32(0);                                       // rotation
        w.F32(1).F32(1);                                // scale
        w.U16(0).U16(0xFFFF);
        w.F32(0).F32(0);                                // translation
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        foreach (float value in identity)
            w.F32(value);
    }

    /// <summary>Fog, switched off.</summary>
    private static void Fog(J3dWriter w)
    {
        w.U8(0).U8(0).U16(0);                           // type none, disabled, centre
        w.F32(0).F32(0).F32(0).F32(0);                  // start, end, near and far planes
        w.U8(0xFF).U8(0xFF).U8(0xFF).U8(0xFF);          // colour
        for (int i = 0; i < 10; i++)
            w.U16(0xFFFF);                              // range adjustment table
    }

    /// <summary>The textures, as BTI headers plus their stored GX data.</summary>
    private static byte[] Tex1(IReadOnlyList<PicturePackTexture> textures)
    {
        var w = new J3dWriter();
        w.Ascii("TEX1").U32(0);
        w.U16(textures.Count).U16(0xFFFF);
        int offsets = w.Length;
        w.U32(0).U32(0);
        w.Align(32);

        int headers = w.Length;
        w.PatchU32(offsets, headers);
        w.Fill(textures.Count * BtiTexture.HeaderSize);
        w.Align(32);

        for (int i = 0; i < textures.Count; i++)
        {
            PicturePackTexture texture = textures[i];
            int at = headers + i * BtiTexture.HeaderSize;
            int image = w.Length;
            w.Bytes(texture.Data);
            w.Align(32);

            var header = new J3dWriter();
            header.U8((int)texture.Format).U8(0);
            header.U16(texture.Width).U16(texture.Height);
            header.U8((int)texture.WrapS).U8((int)texture.WrapT);
            header.U8(0).U8((int)GxTlutFormat.Rgb5A3).U16(0);
            header.U32(0).U32(0);
            header.U8(1).U8(1).S8(0).S8(texture.MipLevels - 1);
            header.U8(texture.MipLevels).U8(0).U16(0);
            header.U32(image - at);
            byte[] bytes = header.ToArray();
            for (int k = 0; k < bytes.Length; k += 4)
                w.PatchU32(at + k, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(k)));
        }

        w.PatchU32(offsets + 4, w.Length);
        w.StringTable(Unique(textures.Select(t => t.Name).ToList()));
        w.Align(32);
        w.PatchU32(4, w.Length);
        return w.ToArray();
    }

    private static readonly float[] Identity3X4 = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    /// <summary>
    /// J3D looks joints and textures up by name, so duplicates would make one of them
    /// unreachable. The skeletons here do repeat names for mirrored limbs.
    /// </summary>
    private static List<string> Unique(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        foreach (string name in names)
        {
            string candidate = name.Length > 0 ? name : "unnamed";
            string unique = candidate;
            for (int n = 2; !seen.Add(unique); n++)
                unique = $"{candidate}_{n}";

            result.Add(unique);
        }

        return result;
    }
}
