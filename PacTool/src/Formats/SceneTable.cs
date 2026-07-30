using System.Buffers.Binary;
using System.Text;

namespace PacTool.Formats;

/// <summary>
/// The scene description that follows the textures in a <c>.scn</c>. A plain <c>.pcp</c> has none;
/// the container header's first word is the offset where this begins.
///
/// The table is a run of 32-byte records of three shapes, which this reader classifies structurally
/// rather than guessing at names:
///
/// <list type="bullet">
/// <item><b>Section header</b> - a count in the first two bytes and thirty zero bytes after it.
/// The reference data always contains three of these, for the texture references, the materials
/// and the shapes.</item>
/// <item><b>Named entry</b> - four zero bytes, a name of three or more characters in the eight
/// bytes after them, then per-section fields: a four-byte level-of-detail tag at 0x0C on shape
/// entries (<c>M0</c>, <c>M1</c>) and, on material entries, an RGBA colour in the word at 0x14.
/// The listing prints that word for every entry without claiming it is always a colour, because
/// on shape and node entries it holds something else.</item>
/// <item><b>Payload</b> - anything else. Shape entries are each followed by a run of GX display
/// list commands: <c>fbarir.scn</c>'s single shape begins <c>99 00 04</c>, a triangle strip of
/// four vertices drawn through vertex attribute table 1.</item>
/// </list>
///
/// Command streams and vertex data are extracted verbatim. Decoding them needs the vertex
/// attribute descriptors, which live outside this table, so it is not attempted here.
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
    public IEnumerable<SceneRecord> Entries => Records.Where(r => r.Kind == SceneRecordKind.Entry);

    /// <summary>Parses the table at <paramref name="data"/>.</summary>
    public static SceneTable Parse(ReadOnlySpan<byte> data, int offset)
    {
        var records = new List<SceneRecord>();
        SceneRecord? lastEntry = null;
        int payloadStart = -1;

        for (int at = 0; at + RecordSize <= data.Length; at += RecordSize)
        {
            ReadOnlySpan<byte> record = data.Slice(at, RecordSize);

            if (Ascii.IsAllZero(record[2..]) && !Ascii.IsAllZero(record[..2]))
            {
                FlushPayload(data, records, ref lastEntry, ref payloadStart, at);
                records.Add(new SceneRecord
                {
                    Offset = at,
                    Kind = SceneRecordKind.SectionHeader,
                    Count = BinaryPrimitives.ReadUInt16BigEndian(record),
                    Raw = record.ToArray(),
                });
                continue;
            }

            // Three characters is the shortest real name in the reference data, and requiring it
            // keeps float pairs inside a command stream from being read as entries: 41 70 00 00,
            // the value 15.0, otherwise looks like a name field holding "Ap".
            if (Ascii.IsAllZero(record[..4]) && Ascii.IsPrintableName(record[4..12], minimumLength: 3))
            {
                FlushPayload(data, records, ref lastEntry, ref payloadStart, at);
                var entry = new SceneRecord
                {
                    Offset = at,
                    Kind = SceneRecordKind.Entry,
                    Name = Ascii.Decode(record[4..12]),
                    Tag = Ascii.IsPrintableName(record[12..16]) ? Ascii.Decode(record[12..16]) : "",
                    Field14 = Ascii.IsAllZero(record[20..24]) ? null : record[20..24].ToArray(),
                    Raw = record.ToArray(),
                };
                records.Add(entry);
                lastEntry = entry;
                continue;
            }

            if (payloadStart < 0)
                payloadStart = at;
        }

        FlushPayload(data, records, ref lastEntry, ref payloadStart, data.Length / RecordSize * RecordSize);
        return new SceneTable(offset, data.ToArray(), records);
    }

    /// <summary>Closes off a run of unstructured records and attaches it to the entry that owns it.</summary>
    private static void FlushPayload(ReadOnlySpan<byte> data, List<SceneRecord> records,
                                     ref SceneRecord? lastEntry, ref int payloadStart, int end)
    {
        if (payloadStart < 0 || end <= payloadStart)
        {
            payloadStart = -1;
            return;
        }

        var payload = new SceneRecord
        {
            Offset = payloadStart,
            Kind = SceneRecordKind.Payload,
            Raw = data[payloadStart..end].ToArray(),
            Owner = lastEntry,
        };
        records.Add(payload);
        lastEntry?.Payloads.Add(payload);
        payloadStart = -1;
    }

    /// <summary>Renders the table as the <c>scene.txt</c> listing.</summary>
    public string Describe(string title)
    {
        var text = new StringBuilder();
        text.AppendLine($"# scene table of {title}");
        text.AppendLine($"# {Raw.Length:N0} bytes at 0x{Offset:X} in the container, {Records.Count} record(s)");
        text.AppendLine("#");
        text.AppendLine("# offset    kind     detail");
        text.AppendLine("# --------  -------  ----------------------------------------------------------");

        foreach (SceneRecord record in Records)
        {
            switch (record.Kind)
            {
                case SceneRecordKind.SectionHeader:
                    text.AppendLine($"  0x{record.Offset:X6}  section  {record.Count} entr{(record.Count == 1 ? "y" : "ies")}");
                    break;

                case SceneRecordKind.Entry:
                    var detail = new StringBuilder($"'{record.Name}'");
                    if (record.Tag.Length > 0)
                        detail.Append($"  lod={record.Tag}");
                    if (record.Field14 is { } field)
                        detail.Append($"  +0x14=#{field[0]:x2}{field[1]:x2}{field[2]:x2}{field[3]:x2}");
                    if (record.Payloads.Count > 0)
                        detail.Append($"  +{record.Payloads.Sum(p => p.Raw.Length):N0} B of commands");
                    text.AppendLine($"  0x{record.Offset:X6}  entry    {detail}");
                    break;

                case SceneRecordKind.Payload:
                    string owner = record.Owner is null ? "" : $" for '{record.Owner.Name}'";
                    text.AppendLine($"  0x{record.Offset:X6}  data     {record.Raw.Length:N0} bytes{owner}, " +
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

    /// <summary>A named object: a texture reference, a material or a shape.</summary>
    Entry,

    /// <summary>Unstructured bytes - GX display list commands and the vertex data they index.</summary>
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

    /// <summary>The word at 0x14, which is an RGBA colour on material entries. Null when zero.</summary>
    public byte[]? Field14 { get; init; }

    /// <summary>Entry a payload run follows, or null when it precedes every entry.</summary>
    public SceneRecord? Owner { get; init; }

    /// <summary>Payload runs that follow this entry.</summary>
    public List<SceneRecord> Payloads { get; } = [];

    /// <summary>A file-name-safe form of <see cref="Name"/> plus <see cref="Tag"/>.</summary>
    public string FileStem => Tag.Length > 0 ? $"{Name}.{Tag}" : Name;
}
