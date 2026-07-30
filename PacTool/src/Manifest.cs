using System.Text.Json;
using System.Text.Json.Serialization;

namespace PacTool;

/// <summary>
/// Describes an archive precisely enough to rebuild it byte for byte: member order, the exact
/// stored name (which may differ from the extracted file name when names collide), and the
/// reserved header fields.
/// </summary>
public sealed class PacManifest
{
    /// <summary>Container magic. Present so the file is self-describing.</summary>
    public string Format { get; set; } = "CAPR";

    /// <summary>Archive the manifest was produced from.</summary>
    public string? Source { get; set; }

    /// <summary>Payload alignment to apply when repacking.</summary>
    public int Align { get; set; } = PacFormat.DefaultAlignment;

    /// <summary>Members in archive order.</summary>
    public List<PacManifestEntry> Members { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>Writes the manifest as JSON.</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    /// <summary>Reads a manifest and resolves its member paths relative to the manifest's directory.</summary>
    public static List<PacSource> Resolve(string manifestPath, out int alignment)
    {
        PacManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PacManifest>(File.ReadAllText(manifestPath), Options)
                       ?? throw new PacFormatException($"{manifestPath}: manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new PacFormatException($"{manifestPath}: not a valid manifest ({ex.Message})");
        }

        alignment = manifest.Align < 1 ? PacFormat.DefaultAlignment : manifest.Align;
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        var sources = new List<PacSource>(manifest.Members.Count);

        for (int i = 0; i < manifest.Members.Count; i++)
        {
            PacManifestEntry entry = manifest.Members[i];
            if (string.IsNullOrWhiteSpace(entry.File))
                throw new PacFormatException($"{manifestPath}: member {i} has no 'file'.");

            string resolved = Path.GetFullPath(Path.Combine(baseDirectory, entry.File));
            if (!File.Exists(resolved))
                throw new PacFormatException($"{manifestPath}: member {i} ('{entry.File}') not found at {resolved}");

            // An entry may omit 'name' to keep the payload file's own base name.
            PacSource source = PacSource.FromFile(resolved, string.IsNullOrEmpty(entry.Name) ? null : entry.Name);
            sources.Add(new PacSource
            {
                Name = source.Name,
                Length = source.Length,
                Open = source.Open,
                Origin = source.Origin,
                Reserved0 = entry.Reserved0,
                Reserved1 = entry.Reserved1,
            });
        }

        return sources;
    }
}

/// <summary>One member entry in a <see cref="PacManifest"/>.</summary>
public sealed class PacManifestEntry
{
    /// <summary>Name to store in the archive's name field.</summary>
    public string Name { get; set; } = "";

    /// <summary>Path to the payload, relative to the manifest.</summary>
    public string File { get; set; } = "";

    /// <summary>Payload size recorded at extraction time. Informational; the file's real length wins.</summary>
    public long Size { get; set; }

    /// <summary>Header field at 0x08. Omitted from JSON when zero.</summary>
    public uint Reserved0 { get; set; }

    /// <summary>Header field at 0x0C. Omitted from JSON when zero.</summary>
    public uint Reserved1 { get; set; }
}
