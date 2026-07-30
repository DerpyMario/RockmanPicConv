namespace PacTool.Formats;

/// <summary>The formats <c>pactool</c> can look inside.</summary>
public enum ContentKind
{
    /// <summary>Not one of the formats below; extracted verbatim.</summary>
    Unknown,

    /// <summary>A <c>CAPR</c> archive, i.e. a nested <c>.pac</c>.</summary>
    Archive,

    /// <summary>A Picture Pack: <c>.pcp</c>, or <c>.scn</c> with a scene table after the textures.</summary>
    PicturePack,

    /// <summary>A character model: <c>.mpc</c>.</summary>
    MpcModel,

    /// <summary>A stage directory block: <c>map.dat</c>, <c>bg.dat</c> or <c>enemy.dat</c>.</summary>
    DataDirectory,

    /// <summary>A J3D model: <c>.bmd</c> or <c>.bdl</c>.</summary>
    J3dModel,

    /// <summary>A standalone BTI texture.</summary>
    BtiTexture,

    /// <summary>A Softimage PIC image.</summary>
    SoftimagePic,
}

/// <summary>
/// Works out what a blob is. Names are a hint, not the answer: a member called <c>b0bdfblk.scn</c>
/// and one called <c>l0blight.pcp</c> hold the same container, and a hand-renamed file should still
/// be recognised. So the extension picks the first candidate and the bytes confirm it; if they
/// disagree, the bytes win.
/// </summary>
public static class ContentSniffer
{
    /// <summary>
    /// Identifies <paramref name="data"/>, using <paramref name="name"/> only to order the
    /// candidates. A Yaz0 wrapper is reported through <paramref name="compressed"/> and the
    /// decompressed bytes are what get identified.
    /// </summary>
    public static ContentKind Identify(string name, ReadOnlySpan<byte> data, out bool compressed)
    {
        compressed = Yaz0.IsCompressed(data);
        return compressed
            ? IdentifyUncompressed(name, Yaz0.Decompress(data, name))
            : IdentifyUncompressed(name, data);
    }

    private static ContentKind IdentifyUncompressed(string name, ReadOnlySpan<byte> data)
    {
        if (data.Length >= PacFormat.HeaderSize && data[..4].SequenceEqual(PacFormat.Magic))
            return ContentKind.Archive;
        if (J3dModel.LooksLikeModel(data))
            return ContentKind.J3dModel;
        if (SoftimagePic.LooksLikePic(data))
            return ContentKind.SoftimagePic;
        if (DataDirectory.IsKnownName(Path.GetFileName(name)))
            return ContentKind.DataDirectory;

        // The remaining formats have no magic, so try the one the extension suggests first.
        string extension = Path.GetExtension(name).ToLowerInvariant();
        bool mpcFirst = extension == ".mpc";

        if (mpcFirst && MpcModel.LooksLikeModel(data))
            return ContentKind.MpcModel;
        if (PicturePack.LooksLikePicturePack(data))
            return ContentKind.PicturePack;
        if (!mpcFirst && MpcModel.LooksLikeModel(data))
            return ContentKind.MpcModel;
        if (extension == ".bti" && BtiTexture.LooksLikeBti(data))
            return ContentKind.BtiTexture;

        return ContentKind.Unknown;
    }

    /// <summary>Short description of a kind, for listings.</summary>
    public static string Describe(ContentKind kind) => kind switch
    {
        ContentKind.Archive => "CAPR archive",
        ContentKind.PicturePack => "Picture Pack",
        ContentKind.MpcModel => "MPC model",
        ContentKind.DataDirectory => "stage directory",
        ContentKind.J3dModel => "J3D model",
        ContentKind.BtiTexture => "BTI texture",
        ContentKind.SoftimagePic => "Softimage PIC",
        _ => "unrecognised",
    };
}
