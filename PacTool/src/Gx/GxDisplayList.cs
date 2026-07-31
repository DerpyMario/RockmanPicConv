using System.Buffers.Binary;

namespace PacTool.Gx;

/// <summary>The eight shapes a GX primitive command can draw.</summary>
public enum GxPrimitive
{
    /// <summary>Independent quads, four vertices each.</summary>
    Quads = 0,

    /// <summary>A second quad encoding that behaves identically.</summary>
    Quads2 = 1,

    /// <summary>Independent triangles, three vertices each.</summary>
    Triangles = 2,

    /// <summary>A triangle strip: every vertex after the second closes another triangle.</summary>
    TriangleStrip = 3,

    /// <summary>A triangle fan around the first vertex.</summary>
    TriangleFan = 4,

    /// <summary>Independent lines.</summary>
    Lines = 5,

    /// <summary>A connected line strip.</summary>
    LineStrip = 6,

    /// <summary>Independent points.</summary>
    Points = 7,
}

/// <summary>One primitive command found in a display list.</summary>
public sealed class GxPrimitiveCommand
{
    /// <summary>Offset of the opcode within the display list.</summary>
    public required int Offset { get; init; }

    /// <summary>Shape being drawn.</summary>
    public required GxPrimitive Primitive { get; init; }

    /// <summary>Which of the eight vertex attribute tables the vertices use.</summary>
    public required int Vat { get; init; }

    /// <summary>Number of vertices that follow.</summary>
    public required int VertexCount { get; init; }

    /// <summary>Offset of the first vertex within the display list.</summary>
    public required int VertexOffset { get; init; }

    /// <summary>Triangles this command contributes. Zero for lines and points.</summary>
    public int TriangleCount => Primitive switch
    {
        GxPrimitive.Quads or GxPrimitive.Quads2 => VertexCount / 4 * 2,
        GxPrimitive.Triangles => VertexCount / 3,
        GxPrimitive.TriangleStrip or GxPrimitive.TriangleFan => Math.Max(0, VertexCount - 2),
        _ => 0,
    };
}

/// <summary>
/// A parsed GX display list: the byte stream the graphics FIFO consumes.
///
/// A command is one opcode byte and whatever it needs after it. The ones that appear here are:
///
/// <code>
///   0x00        NOP, also the padding a list is rounded up with
///   0x08        load a command-processor register: u8 address, u32 value
///   0x10        load transform-unit registers: u16 count-1, u16 address, u32[count] values
///   0x20 .. 0x38  indexed transform-unit load from array A, B, C or D
///   0x40        call another display list: u32 address, u32 size
///   0x44        performance metrics
///   0x48        invalidate the vertex cache
///   0x80 .. 0xBF  draw: bits 3 to 6 select the shape and bits 0 to 2 the vertex attribute
///                 table, then u16 vertex count and that many vertices
/// </code>
///
/// Because a vertex's length comes from the register state rather than the stream, decoding needs
/// the vertex format up front. <see cref="Parse"/> takes a resolver so a caller can supply one per
/// attribute table, and register loads found in the stream update the state as it runs.
/// </summary>
public sealed class GxDisplayList
{
    /// <summary>Primitive commands in stream order.</summary>
    public IReadOnlyList<GxPrimitiveCommand> Primitives { get; }

    /// <summary>Command-processor register writes seen in the stream, as (address, value).</summary>
    public IReadOnlyList<(byte Address, uint Value)> CommandProcessorWrites { get; }

    /// <summary>Bytes consumed before the trailing padding.</summary>
    public int Consumed { get; }

    /// <summary>Total vertices across every primitive.</summary>
    public int VertexCount => Primitives.Sum(p => p.VertexCount);

    /// <summary>Total triangles across every primitive.</summary>
    public int TriangleCount => Primitives.Sum(p => p.TriangleCount);

    private GxDisplayList(List<GxPrimitiveCommand> primitives,
                          List<(byte, uint)> commandProcessorWrites, int consumed)
    {
        Primitives = primitives;
        CommandProcessorWrites = commandProcessorWrites;
        Consumed = consumed;
    }

    /// <summary>
    /// Parses a display list, or returns null when the stream does not decode cleanly under
    /// <paramref name="vertexSize"/>. Returning null rather than throwing is what lets a caller
    /// try candidate vertex formats and keep the one that fits.
    /// </summary>
    /// <param name="data">The command stream.</param>
    /// <param name="vertexSize">Vertex length for a given attribute table index, or null if unknown.</param>
    public static GxDisplayList? Parse(ReadOnlySpan<byte> data, Func<int, int?> vertexSize)
    {
        var primitives = new List<GxPrimitiveCommand>();
        var writes = new List<(byte, uint)>();
        int at = 0;

        while (at < data.Length)
        {
            byte opcode = data[at];

            if (opcode == 0x00)
            {
                // The rest must be NOP padding; anything else means the walk has gone wrong.
                for (int i = at; i < data.Length; i++)
                {
                    if (data[i] != 0)
                        return null;
                }

                return new GxDisplayList(primitives, writes, at);
            }

            if (opcode >= 0x80 && opcode <= 0xBF)
            {
                if (at + 3 > data.Length)
                    return null;

                int vat = opcode & 0x07;
                int count = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 1)..]);
                if (vertexSize(vat) is not { } size || size <= 0)
                    return null;

                long end = at + 3L + (long)count * size;
                if (end > data.Length)
                    return null;

                primitives.Add(new GxPrimitiveCommand
                {
                    Offset = at,
                    Primitive = (GxPrimitive)((opcode & 0x78) >> 3),
                    Vat = vat,
                    VertexCount = count,
                    VertexOffset = at + 3,
                });
                at = (int)end;
                continue;
            }

            switch (opcode)
            {
                case 0x08:
                    if (at + 6 > data.Length)
                        return null;
                    writes.Add((data[at + 1], BinaryPrimitives.ReadUInt32BigEndian(data[(at + 2)..])));
                    at += 6;
                    continue;

                case 0x10:
                {
                    if (at + 5 > data.Length)
                        return null;
                    int count = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 1)..]) + 1;
                    at += 5 + count * 4;
                    if (at > data.Length)
                        return null;
                    continue;
                }

                case 0x20 or 0x28 or 0x30 or 0x38:
                    if (at + 5 > data.Length)
                        return null;
                    at += 5;
                    continue;

                case 0x40:
                    if (at + 9 > data.Length)
                        return null;
                    at += 9;
                    continue;

                case 0x44:
                case 0x48:
                    at += 1;
                    continue;

                case 0x61:
                    if (at + 5 > data.Length)
                        return null;
                    at += 5;
                    continue;

                default:
                    return null;
            }
        }

        return new GxDisplayList(primitives, writes, at);
    }

    /// <summary>Expands a primitive into triangles, as index triples into its own vertex run.</summary>
    public static IEnumerable<(int A, int B, int C)> Triangulate(GxPrimitiveCommand command)
    {
        int n = command.VertexCount;
        switch (command.Primitive)
        {
            case GxPrimitive.Triangles:
                for (int i = 0; i + 2 < n; i += 3)
                    yield return (i, i + 1, i + 2);
                break;

            case GxPrimitive.Quads or GxPrimitive.Quads2:
                for (int i = 0; i + 3 < n; i += 4)
                {
                    yield return (i, i + 1, i + 2);
                    yield return (i, i + 2, i + 3);
                }

                break;

            case GxPrimitive.TriangleStrip:
                // Every other triangle is wound backwards so the facing stays consistent.
                for (int i = 0; i + 2 < n; i++)
                    yield return (i & 1) == 0 ? (i, i + 1, i + 2) : (i + 1, i, i + 2);
                break;

            case GxPrimitive.TriangleFan:
                for (int i = 1; i + 1 < n; i++)
                    yield return (0, i, i + 1);
                break;
        }
    }
}
