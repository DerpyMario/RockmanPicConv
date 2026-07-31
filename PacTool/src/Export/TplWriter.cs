using PacTool.Formats;
using PacTool.Gx;
using PacTool.Imaging;

namespace PacTool.Export;

/// <summary>One texture as a TPL stores it: a GX format, its mip chain, and how it is sampled.</summary>
public sealed class TplTexture
{
    /// <summary>Name. TPL itself stores no names; this is only used for listings and file names.</summary>
    public required string Name { get; init; }

    /// <summary>GX texture format.</summary>
    public required GxTextureFormat Format { get; init; }

    /// <summary>Width of the base level in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the base level in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Levels the chain holds, at least one.</summary>
    public required int MipLevels { get; init; }

    /// <summary>The stored mip chain, exactly as the source held it.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Horizontal wrap mode: 0 clamp, 1 repeat, 2 mirror.</summary>
    public int WrapS { get; init; }

    /// <summary>Vertical wrap mode.</summary>
    public int WrapT { get; init; }

    /// <summary>GX minification filter.</summary>
    public int MinFilter { get; init; } = 1;

    /// <summary>GX magnification filter.</summary>
    public int MagFilter { get; init; } = 1;

    /// <summary>Level-of-detail bias.</summary>
    public float LodBias { get; init; }

    /// <summary>The TLUT a paletted format needs, or null.</summary>
    public GxPalette? Palette { get; init; }

    /// <summary>A Picture Pack texture, whose stored bytes go across unchanged.</summary>
    public static TplTexture From(PicturePackTexture texture) => new()
    {
        Name = texture.Name,
        Format = texture.Format,
        Width = texture.Width,
        Height = texture.Height,
        MipLevels = Math.Max(1, texture.MipLevels),
        Data = texture.Data,
        WrapS = (int)texture.WrapS,
        WrapT = (int)texture.WrapT,
        MinFilter = (int)texture.MinFilter,
        MagFilter = (int)texture.MagFilter,
    };

    /// <summary>A BTI texture, standalone or out of a <c>TEX1</c> section.</summary>
    public static TplTexture From(BtiTexture texture, string name) => new()
    {
        Name = name,
        Format = texture.Format,
        Width = texture.Width,
        Height = texture.Height,
        MipLevels = Math.Max(1, texture.MipCount),
        Data = texture.Data,
        WrapS = texture.WrapS,
        WrapT = texture.WrapT,
        MinFilter = texture.MinFilter,
        MagFilter = texture.MagFilter,
        LodBias = texture.LodBias / 100f,
        Palette = texture.Palette,
    };

    /// <summary>
    /// An image that only ever existed as RGBA - the Softimage PIC source art - encoded as RGBA8,
    /// the one format that keeps every colour and every alpha value intact.
    /// </summary>
    public static TplTexture From(Rgba32Image image, string name) => new()
    {
        Name = name,
        Format = GxTextureFormat.Rgba8,
        Width = image.Width,
        Height = image.Height,
        MipLevels = 1,
        Data = GxImageEncoder.EncodeRgba8(image),
        WrapS = 0,
        WrapT = 0,
    };
}

/// <summary>
/// Writes a TPL - the texture bank the GameCube and Wii SDKs load directly, and what every tool in
/// that ecosystem reads. A Picture Pack is already a texture bank in all but name, so the mapping
/// is the natural one: one TPL per container, holding every texture in it, with each mip chain
/// copied across byte for byte rather than re-encoded.
///
/// <code>
///   file header
///   0x00  u32  version        0x0020AF30
///   0x04  u32  textureCount
///   0x08  u32  headerSize     0x0C, which is also where the descriptors start
///
///   descriptor, one per texture
///   0x00  u32  imageHeaderOffset      absolute, from the start of the file
///   0x04  u32  paletteHeaderOffset    absolute, or 0 when the format is not paletted
///
///   image header (0x24)              palette header (0x0C)
///   0x00  u16  height                 0x00  u16  entryCount
///   0x02  u16  width                  0x02  u8   unpacked
///   0x04  u32  format                 0x03  u8   padding
///   0x08  u32  imageDataOffset        0x04  u32  format
///   0x0C  u32  wrapS                  0x08  u32  paletteDataOffset
///   0x10  u32  wrapT
///   0x14  u32  minFilter
///   0x18  u32  magFilter
///   0x1C  f32  lodBias
///   0x20  u8   edgeLodEnable
///   0x21  u8   minLod
///   0x22  u8   maxLod
///   0x23  u8   unpacked
/// </code>
///
/// Note that height comes before width, which is the other way round from every other header in
/// this repository. Every offset is from the start of the file: the SDK's loader walks the
/// descriptors and turns each one into a pointer by adding the base address. It takes the
/// descriptors from offset 12 outright rather than from the header size at 0x08, so that field is
/// written for the tools that do read it and is otherwise inert.
///
/// Image and palette data are aligned to 32 bytes, which is what the hardware wants. Palette
/// entries are two bytes each, and the loader sizes the table from the entry count alone.
/// </summary>
public static class TplWriter
{
    /// <summary>The version word every TPL starts with.</summary>
    public const uint Version = 0x0020AF30;

    /// <summary>Size of a TPL image header.</summary>
    public const int ImageHeaderSize = 0x24;

    /// <summary>Size of a TPL palette header.</summary>
    public const int PaletteHeaderSize = 0x0C;

    /// <summary>Alignment the SDK's loader expects of image and palette data.</summary>
    public const int DataAlignment = 32;

    /// <summary>Builds a TPL holding <paramref name="textures"/>, in order.</summary>
    public static byte[] Build(IReadOnlyList<TplTexture> textures)
    {
        if (textures.Count == 0)
            throw new PacFormatException("a TPL needs at least one texture.");

        foreach (TplTexture texture in textures)
        {
            if (!GxTextureFormats.IsKnown(texture.Format))
                throw new PacFormatException($"texture '{texture.Name}' uses GX format 0x{(int)texture.Format:X}, which is not a TPL format.");
            if (GxTextureFormats.IsPaletted(texture.Format) && texture.Palette is null)
                throw new PacFormatException($"texture '{texture.Name}' is {texture.Format} but carries no palette.");
        }

        var w = new BigEndianOutput();
        w.U32(Version).U32(textures.Count).U32(0x0C);

        int descriptors = w.Length;
        w.Fill(textures.Count * 8);

        // Headers first, then the data they point at, so every offset is known by the time the
        // header table is patched.
        var imageHeaders = new int[textures.Count];
        var paletteHeaders = new int[textures.Count];
        for (int i = 0; i < textures.Count; i++)
        {
            imageHeaders[i] = w.Length;
            w.Fill(ImageHeaderSize);
            if (textures[i].Palette is not null)
            {
                paletteHeaders[i] = w.Length;
                w.Fill(PaletteHeaderSize);
            }

            w.PatchU32(descriptors + i * 8, imageHeaders[i]);
            w.PatchU32(descriptors + i * 8 + 4, paletteHeaders[i]);
        }

        for (int i = 0; i < textures.Count; i++)
        {
            TplTexture texture = textures[i];

            if (texture.Palette is { } palette)
            {
                w.Align(DataAlignment, fill: 0);
                int paletteData = w.Length;
                w.Bytes(palette.Raw);

                int at = paletteHeaders[i];
                w.PatchU16(at, palette.Count);
                w.PatchU16(at + 2, 0);                          // unpacked, then padding
                w.PatchU32(at + 4, (uint)palette.Format);
                w.PatchU32(at + 8, paletteData);
            }

            w.Align(DataAlignment, fill: 0);
            int imageData = w.Length;
            w.Bytes(texture.Data);

            // Only whole mip levels are advertised: a chain truncated by a damaged source would
            // otherwise send a reader past the end of the data.
            int levels = Levels(texture);

            int header = imageHeaders[i];
            w.PatchU16(header, texture.Height);
            w.PatchU16(header + 2, texture.Width);
            w.PatchU32(header + 4, (uint)texture.Format);
            w.PatchU32(header + 8, imageData);
            w.PatchU32(header + 0x0C, (uint)texture.WrapS);
            w.PatchU32(header + 0x10, (uint)texture.WrapT);
            w.PatchU32(header + 0x14, (uint)texture.MinFilter);
            w.PatchU32(header + 0x18, (uint)texture.MagFilter);
            w.PatchF32(header + 0x1C, texture.LodBias);
            w.PatchU16(header + 0x20, 0);                       // edge LOD off, minimum LOD 0
            w.PatchU16(header + 0x22, (levels - 1) << 8);       // maximum LOD, then unpacked
        }

        w.Align(DataAlignment, fill: 0);
        return w.ToArray();
    }

    /// <summary>Levels the stored data actually holds, which may be fewer than the source claimed.</summary>
    private static int Levels(TplTexture texture)
    {
        int levels = 1;
        while (levels < texture.MipLevels &&
               GxTextureFormats.MipChainSize(texture.Format, texture.Width, texture.Height, levels + 1) <= texture.Data.Length)
        {
            levels++;
        }

        return levels;
    }

    /// <summary>Renders the contents as the <c>textures.tpl.txt</c> listing.</summary>
    public static string Describe(string title, IReadOnlyList<TplTexture> textures)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"# {title}: {textures.Count} texture(s)");
        text.AppendLine("#");
        text.AppendLine("# TPL stores no names, so this is the order they appear in.");
        text.AppendLine("#");
        text.AppendLine("#  idx  name              format  size       mips  palette");
        text.AppendLine("# ----  ----------------  ------  ---------  ----  -------------");

        for (int i = 0; i < textures.Count; i++)
        {
            TplTexture texture = textures[i];
            string palette = texture.Palette is { } tlut ? $"{tlut.Count}-entry {tlut.Format}" : "";
            text.AppendLine($"  {i,4}  {texture.Name,-16}  {PicturePack.DescribeFormat(texture.Format),-6}  " +
                            $"{texture.Width,4}x{texture.Height,-4}  {Levels(texture),4}  {palette}");
        }

        return text.ToString();
    }
}
