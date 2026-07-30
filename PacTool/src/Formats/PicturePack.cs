using System.Buffers.Binary;
using System.Text;
using PacTool.Gx;
using PacTool.Imaging;

namespace PacTool.Formats;

/// <summary>
/// A Picture Pack: the texture container the developer tool <c>RockmanPicConv</c> produced. It is
/// stored either as a standalone <c>.pcp</c> file or as the payload of a <c>CAPR</c> member named
/// <c>*.pcp</c> or <c>*.scn</c>. A <c>.scn</c> is the same container with a scene table appended
/// after the last texture; see <see cref="SceneTable"/>.
///
/// <code>
///   0x00  u32       tableSize   bytes of texture data, i.e. the offset of the scene table
///   0x04  u32       count       number of textures
///   0x08  u8[0x18]  reserved    zero in all reference data
///   0x20  ...       texture[count], each a 0x40 header followed by its mip chain
/// </code>
///
/// The 0x40 texture header:
///
/// <code>
///   0x00  char[16]  name       ASCII, NUL-padded
///   0x10  u32       format     GX texture format, see GxTextureFormat
///   0x14  u32       dataSize   bytes of the whole mip chain that follows the header
///   0x18  u16       width
///   0x1A  u16       height
///   0x1C  u32       wrapS      0 clamp, 1 repeat
///   0x20  u32       wrapT
///   0x24  u32       minFilter  GX filter mode; 1 is linear, 5 is trilinear and implies mipmaps
///   0x28  u32       magFilter  1 in all reference data
///   0x2C  u32       lodBias    0 in all reference data
///   0x30  u8        reserved
///   0x31  u8        minLod
///   0x32  u8        maxLod     the chain holds maxLod + 1 levels
///   0x33  u8        reserved
///   0x34  u8[0xC]   reserved   zero in all reference data
///   0x40  ...       mip chain, dataSize bytes
/// </code>
///
/// The mip level count is confirmed rather than assumed: <c>maxLod + 1</c> levels reproduces
/// <c>dataSize</c> exactly for all 3048 textures in the reference data.
/// </summary>
public sealed class PicturePack
{
    /// <summary>Size of the container header before the first texture.</summary>
    public const int HeaderSize = 0x20;

    /// <summary>Size of one texture header.</summary>
    public const int TextureHeaderSize = 0x40;

    /// <summary>Textures in stored order.</summary>
    public IReadOnlyList<PicturePackTexture> Textures { get; }

    /// <summary>The scene table that follows the textures in a <c>.scn</c>, or null for a plain <c>.pcp</c>.</summary>
    public SceneTable? Scene { get; }

    /// <summary>Non-fatal oddities noticed while parsing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    private PicturePack(List<PicturePackTexture> textures, SceneTable? scene, List<string> warnings)
    {
        Textures = textures;
        Scene = scene;
        Warnings = warnings;
    }

    /// <summary>True if <paramref name="data"/> plausibly starts with a Picture Pack header.</summary>
    public static bool LooksLikePicturePack(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + TextureHeaderSize)
            return false;

        uint tableSize = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (count == 0 || count > 0x1000 || tableSize > data.Length || tableSize < HeaderSize)
            return false;

        // The first texture header has to name a format this hardware has and a sane size.
        uint format = BinaryPrimitives.ReadUInt32BigEndian(data[(HeaderSize + 0x10)..]);
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(data[(HeaderSize + 0x18)..]);
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(data[(HeaderSize + 0x1A)..]);
        return width > 0 && height > 0 && GxTextureFormats.IsKnown((GxTextureFormat)format);
    }

    /// <summary>Parses a Picture Pack.</summary>
    public static PicturePack Parse(ReadOnlySpan<byte> data, string sourceName)
    {
        if (data.Length < HeaderSize)
            throw new PacFormatException($"{sourceName}: {data.Length} bytes is too short for a Picture Pack header.");

        long tableSize = BinaryPrimitives.ReadUInt32BigEndian(data);
        long count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var warnings = new List<string>();

        if (count is < 0 or > 0x10000)
            throw new PacFormatException($"{sourceName}: texture count {count} is out of range.");

        var textures = new List<PicturePackTexture>((int)count);
        int offset = HeaderSize;

        for (int i = 0; i < count; i++)
        {
            if (offset + TextureHeaderSize > data.Length)
            {
                warnings.Add($"texture {i} of {count} starts at 0x{offset:X}, past the end of the data.");
                break;
            }

            ReadOnlySpan<byte> header = data.Slice(offset, TextureHeaderSize);
            string name = Ascii.Decode(header[..16]);
            var format = (GxTextureFormat)BinaryPrimitives.ReadUInt32BigEndian(header[0x10..]);
            long dataSize = BinaryPrimitives.ReadUInt32BigEndian(header[0x14..]);
            int width = BinaryPrimitives.ReadUInt16BigEndian(header[0x18..]);
            int height = BinaryPrimitives.ReadUInt16BigEndian(header[0x1A..]);

            long available = data.Length - (offset + TextureHeaderSize);
            if (dataSize > available)
            {
                warnings.Add($"texture {i} ('{name}') claims 0x{dataSize:X} bytes but only 0x{available:X} remain; truncating.");
                dataSize = available;
            }

            int declaredLevels = header[0x32] + 1;
            int fittingLevels = width > 0 && height > 0 && GxTextureFormats.IsKnown(format)
                ? GxTextureFormats.LevelsFilling(format, width, height, dataSize)
                : 0;

            if (fittingLevels == 0)
            {
                warnings.Add($"texture {i} ('{name}', {DescribeFormat(format)} {width}x{height}) has 0x{dataSize:X} bytes, " +
                             "which is not the size of any mip chain; only the base level will be decoded.");
            }
            else if (fittingLevels != declaredLevels)
            {
                warnings.Add($"texture {i} ('{name}') declares maxLod {header[0x32]} but its payload holds {fittingLevels} level(s).");
            }

            textures.Add(new PicturePackTexture
            {
                Index = i,
                Name = name,
                Format = format,
                Width = width,
                Height = height,
                WrapS = BinaryPrimitives.ReadUInt32BigEndian(header[0x1C..]),
                WrapT = BinaryPrimitives.ReadUInt32BigEndian(header[0x20..]),
                MinFilter = BinaryPrimitives.ReadUInt32BigEndian(header[0x24..]),
                MagFilter = BinaryPrimitives.ReadUInt32BigEndian(header[0x28..]),
                MinLod = header[0x31],
                MaxLod = header[0x32],
                MipLevels = fittingLevels == 0 ? 1 : fittingLevels,
                HeaderOffset = offset,
                Data = data.Slice(offset + TextureHeaderSize, (int)dataSize).ToArray(),
            });

            offset += TextureHeaderSize + (int)dataSize;
        }

        SceneTable? scene = null;
        if (tableSize > 0 && tableSize != offset && tableSize <= data.Length)
        {
            warnings.Add($"header says the texture area ends at 0x{tableSize:X} but the textures end at 0x{offset:X}.");
            offset = (int)tableSize;
        }

        if (offset < data.Length)
            scene = SceneTable.Parse(data[offset..], offset);

        return new PicturePack(textures, scene, warnings);
    }

    /// <summary>Human-readable form of a GX format value, including ones this tool cannot decode.</summary>
    public static string DescribeFormat(GxTextureFormat format) =>
        GxTextureFormats.IsKnown(format) ? format.ToString().ToUpperInvariant() : $"0x{(int)format:X}";
}

/// <summary>One texture inside a <see cref="PicturePack"/>.</summary>
public sealed class PicturePackTexture
{
    /// <summary>Position in the container, starting at zero.</summary>
    public required int Index { get; init; }

    /// <summary>Name from the 16-byte name field.</summary>
    public required string Name { get; init; }

    /// <summary>GX texture format.</summary>
    public required GxTextureFormat Format { get; init; }

    /// <summary>Width of the base level in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the base level in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Horizontal wrap mode: 0 clamp, 1 repeat, 2 mirror.</summary>
    public required uint WrapS { get; init; }

    /// <summary>Vertical wrap mode: 0 clamp, 1 repeat, 2 mirror.</summary>
    public required uint WrapT { get; init; }

    /// <summary>GX minification filter. 5 (trilinear) is what mipmapped textures use.</summary>
    public required uint MinFilter { get; init; }

    /// <summary>GX magnification filter.</summary>
    public required uint MagFilter { get; init; }

    /// <summary>Smallest LOD the game will sample.</summary>
    public required byte MinLod { get; init; }

    /// <summary>Largest LOD the game will sample; the stored chain holds one more level than this.</summary>
    public required byte MaxLod { get; init; }

    /// <summary>Mip levels actually present, derived from the payload length.</summary>
    public required int MipLevels { get; init; }

    /// <summary>Offset of this texture's header inside the container.</summary>
    public required int HeaderOffset { get; init; }

    /// <summary>The stored mip chain.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Width of mip level <paramref name="level"/>.</summary>
    public int LevelWidth(int level) => Math.Max(1, Width >> level);

    /// <summary>Height of mip level <paramref name="level"/>.</summary>
    public int LevelHeight(int level) => Math.Max(1, Height >> level);

    /// <summary>Decodes one mip level to RGBA.</summary>
    public Rgba32Image Decode(int level = 0, GxPalette? palette = null)
    {
        if ((uint)level >= (uint)MipLevels)
            throw new ArgumentOutOfRangeException(nameof(level), $"'{Name}' has {MipLevels} mip level(s).");

        long start = GxTextureFormats.LevelOffset(Format, Width, Height, level);
        long size = GxTextureFormats.LevelSize(Format, LevelWidth(level), LevelHeight(level));
        if (start >= Data.Length)
            throw new PacFormatException($"'{Name}': mip level {level} starts past the end of the stored data.");

        size = Math.Min(size, Data.Length - start);
        return GxImageDecoder.Decode(Format, Data.AsSpan((int)start, (int)size),
                                     LevelWidth(level), LevelHeight(level), palette);
    }

    /// <summary>One-line summary for listings.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append($"{PicturePack.DescribeFormat(Format),-6} {Width,4}x{Height,-4}");
        text.Append(MipLevels > 1 ? $" {MipLevels,2} mips" : "        ");
        text.Append($"  wrap {WrapName(WrapS)}/{WrapName(WrapT)}");
        text.Append($"  {Data.Length,9:N0} B");
        return text.ToString();
    }

    private static string WrapName(uint mode) => mode switch
    {
        0 => "clamp",
        1 => "repeat",
        2 => "mirror",
        _ => $"?{mode}",
    };
}
