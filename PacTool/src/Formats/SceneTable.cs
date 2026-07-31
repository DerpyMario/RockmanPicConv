using System.Buffers.Binary;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// The scene description that follows the textures in a <c>.scn</c>. A plain <c>.pcp</c> has none;
/// the container header's first word is the offset where this begins.
///
/// The table is a run of 32-byte records. Most are one of two shapes, told apart structurally
/// rather than by guessing at names:
///
/// <list type="bullet">
/// <item><b>Section header</b> - a count in the first two bytes and thirty zero bytes after it.
/// One introduces each run of entries: the texture references, the materials and the shapes.</item>
/// <item><b>Named entry</b> - a name of three or more characters at 0x04, plus per-section fields:
/// a four-byte level-of-detail tag at 0x0C on shape entries (<c>M0</c>, <c>M1</c>) and, on material
/// entries, an RGBA colour in the word at 0x14.</item>
/// </list>
///
/// A <b>shape</b> entry is a named entry whose remaining fields describe the GX display list stored
/// immediately after it:
///
/// <code>
///   0x16  u16  streamBytes    bytes of display list following this record, padding included
///   0x1A  u16  listBytes      streamBytes - 32
///   0x1C  u16  vertexCount
///   0x1E  u16  triangleCount
/// </code>
///
/// Those fields make the table walkable exactly rather than by scanning: a shape record's display
/// list is skipped by its own length, so the next record always lands on a real boundary. The
/// counts are also what identify the vertex layout - see <see cref="ScnMesh"/>.
///
/// Anything a record is not recognised as is kept as payload and extracted verbatim.
/// </summary>
public sealed class SceneTable
{
    /// <summary>Size of one record.</summary>
    public const int RecordSize = 0x20;

    /// <summary>Offset of the table within its container.</summary>
    public int Offset { get; }

    /// <summary>The whole table, verbatim.</summary>
    public byte[] Raw { get; }

    /// <summary>Records in stored order.</summary>
    public IReadOnlyList<SceneRecord> Records { get; }

    private SceneTable(int offset, byte[] raw, List<SceneRecord> records)
    {
        Offset = offset;
        Raw = raw;
        Records = records;
    }

    /// <summary>Named entries only, in stored order.</summary>
    public IEnumerable<SceneRecord> Entries => Records.Where(r => r.Kind is SceneRecordKind.Entry or SceneRecordKind.Shape);

    /// <summary>Shape entries only, in stored order.</summary>
    public IEnumerable<SceneRecord> Shapes => Records.Where(r => r.Kind == SceneRecordKind.Shape);

    /// <summary>Parses the table at <paramref name="data"/>.</summary>
    public static SceneTable Parse(ReadOnlySpan<byte> data, int offset)
    {
        var records = new List<SceneRecord>();
        int payloadStart = -1;
        int at = 0;

        while (at + RecordSize <= data.Length)
        {
            ReadOnlySpan<byte> record = data.Slice(at, RecordSize);

            if (Ascii.IsAllZero(record[2..]) && !Ascii.IsAllZero(record[..2]))
            {
                FlushPayload(data, records, ref payloadStart, at);
                records.Add(new SceneRecord
                {
                    Offset = at,
                    Kind = SceneRecordKind.SectionHeader,
                    Count = BinaryPrimitives.ReadUInt16BigEndian(record),
                    Raw = record.ToArray(),
                });
                at += RecordSize;
                continue;
            }

            // Three characters is the shortest real name in the reference data, and requiring it
            // keeps float pairs inside a display list from being read as entries: 41 70 00 00,
            // the value 15.0, otherwise looks like a name field holding "Ap".
            if (Ascii.IsPrintableName(record[4..12], minimumLength: 3))
            {
                int streamBytes = BinaryPrimitives.ReadUInt16BigEndian(record[0x16..]);
                int listBytes = BinaryPrimitives.ReadUInt16BigEndian(record[0x1A..]);
                int vertices = BinaryPrimitives.ReadUInt16BigEndian(record[0x1C..]);
                int triangles = BinaryPrimitives.ReadUInt16BigEndian(record[0x1E..]);
                bool isShape = streamBytes == listBytes + RecordSize && vertices > 0 &&
                               streamBytes > 0 && at + RecordSize + streamBytes <= data.Length;

                FlushPayload(data, records, ref payloadStart, at);
                var entry = new SceneRecord
                {
                    Offset = at,
                    Kind = isShape ? SceneRecordKind.Shape : SceneRecordKind.Entry,
                    Name = Ascii.Decode(record[4..12]),
                    Tag = Ascii.IsPrintableName(record[12..16]) ? Ascii.Decode(record[12..16]) : "",
                    Field00 = BinaryPrimitives.ReadUInt32BigEndian(record),
                    Field14 = Ascii.IsAllZero(record[20..24]) ? null : record[20..24].ToArray(),
                    VertexCount = isShape ? vertices : 0,
                    TriangleCount = isShape ? triangles : 0,
                    DisplayList = isShape ? data.Slice(at + RecordSize, streamBytes).ToArray() : null,
                    Raw = record.ToArray(),
                };
                records.Add(entry);
                at += RecordSize + (isShape ? streamBytes : 0);
                continue;
            }

            if (payloadStart < 0)
                payloadStart = at;
            at += RecordSize;
        }

        FlushPayload(data, records, ref payloadStart, data.Length / RecordSize * RecordSize);
        return new SceneTable(offset, data.ToArray(), records);
    }

    /// <summary>Closes off a run of unrecognised records.</summary>
    private static void FlushPayload(ReadOnlySpan<byte> data, List<SceneRecord> records,
                                     ref int payloadStart, int end)
    {
        if (payloadStart < 0 || end <= payloadStart)
        {
            payloadStart = -1;
            return;
        }

        records.Add(new SceneRecord
        {
            Offset = payloadStart,
            Kind = SceneRecordKind.Payload,
            Raw = data[payloadStart..end].ToArray(),
        });
        payloadStart = -1;
    }

    /// <summary>Renders the table as the <c>scene.txt</c> listing.</summary>
    public string Describe(string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"# scene table of {title}");
        text.AppendLine($"# {Raw.Length:N0} bytes at 0x{Offset:X} in the container, {Records.Count} record(s)");
        text.AppendLine("#");
        text.AppendLine("# offset    kind      detail");
        text.AppendLine("# --------  --------  ---------------------------------------------------------");

        foreach (SceneRecord record in Records)
        {
            switch (record.Kind)
            {
                case SceneRecordKind.SectionHeader:
                    text.AppendLine($"  0x{record.Offset:X6}  section   {record.Count} entr{(record.Count == 1 ? "y" : "ies")}");
                    break;

                case SceneRecordKind.Entry:
                {
                    var detail = new StringBuilder($"'{record.Name}'");
                    if (record.Tag.Length > 0)
                        detail.Append($"  tag={record.Tag}");
                    if (record.Field14 is { } field)
                        detail.Append($"  +0x14=#{field[0]:x2}{field[1]:x2}{field[2]:x2}{field[3]:x2}");
                    text.AppendLine($"  0x{record.Offset:X6}  entry     {detail}");
                    break;
                }

                case SceneRecordKind.Shape:
                {
                    var detail = new StringBuilder($"'{record.Name}'");
                    if (record.Tag.Length > 0)
                        detail.Append($"  lod={record.Tag}");
                    detail.Append($"  {record.VertexCount} vert, {record.TriangleCount} tri");
                    detail.Append($"  {record.DisplayList!.Length:N0} B display list");
                    if (record.Mesh is { } mesh)
                        detail.Append($"  [{mesh.FormatName}, {mesh.Format.VertexSize} B/vertex]");
                    else
                        detail.Append("  [vertex layout not identified]");
                    text.AppendLine($"  0x{record.Offset:X6}  shape     {detail}");
                    break;
                }

                case SceneRecordKind.Payload:
                    text.AppendLine($"  0x{record.Offset:X6}  data      {record.Raw.Length:N0} bytes, " +
                                    $"starts {Ascii.Hex(record.Raw.AsSpan(0, Math.Min(8, record.Raw.Length)))}");
                    break;
            }
        }

        return text.ToString();
    }
}

/// <summary>What a <see cref="SceneRecord"/> turned out to be.</summary>
public enum SceneRecordKind
{
    /// <summary>A count followed by padding, introducing a run of entries.</summary>
    SectionHeader,

    /// <summary>A named object: a texture reference, a material or a node.</summary>
    Entry,

    /// <summary>A named object followed by the GX display list that draws it.</summary>
    Shape,

    /// <summary>Records that matched neither shape, kept verbatim.</summary>
    Payload,
}

/// <summary>One record of a <see cref="SceneTable"/>.</summary>
public sealed class SceneRecord
{
    /// <summary>Offset within the scene table.</summary>
    public required int Offset { get; init; }

    /// <summary>How this record was classified.</summary>
    public required SceneRecordKind Kind { get; init; }

    /// <summary>The record's bytes. For a payload run this is the whole run, not one record.</summary>
    public required byte[] Raw { get; init; }

    /// <summary>Name of an entry, empty otherwise.</summary>
    public string Name { get; init; } = "";

    /// <summary>Level-of-detail tag of a shape entry ("M0", "M1"), empty otherwise.</summary>
    public string Tag { get; init; } = "";

    /// <summary>Entry count of a section header.</summary>
    public int Count { get; init; }

    /// <summary>
    /// The word at 0x00. Zero on stage and effect entries; 1 on the shape entries of character
    /// models, whose geometry is stored in a different form - see <see cref="SceneRecordKind.Shape"/>.
    /// </summary>
    public uint Field00 { get; init; }

    /// <summary>The word at 0x14, which is an RGBA colour on material entries. Null when zero.</summary>
    public byte[]? Field14 { get; init; }

    /// <summary>Vertices the shape record declares.</summary>
    public int VertexCount { get; init; }

    /// <summary>Triangles the shape record declares.</summary>
    public int TriangleCount { get; init; }

    /// <summary>The display list following a shape record, or null.</summary>
    public byte[]? DisplayList { get; init; }

    /// <summary>Decoded geometry, set by <see cref="SceneTable"/>'s caller. Null when the layout was not identified.</summary>
    public ScnMesh? Mesh { get; set; }

    /// <summary>A file-name-safe form of <see cref="Name"/> plus <see cref="Tag"/>.</summary>
    public string FileStem => Tag.Length > 0 ? $"{Name}.{Tag}" : Name;
}
