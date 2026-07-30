using PacTool.Formats;
using PacTool.Gx;

namespace PacTool.Tests;

/// <summary>
/// Builds BMD, BDL and BTI fixtures. Section sizes, string tables and the offsets inside TEX1 are
/// all computed rather than hard-coded, so a fixture stays valid as its contents change.
/// </summary>
internal static class J3dBuilder
{
    /// <summary>Wraps sections in a J3D file header.</summary>
    public static byte[] Build(string variant, IEnumerable<byte[]> sections)
    {
        var list = sections.ToList();
        var writer = new BigEndianWriter()
            .Ascii("J3D1").Ascii(variant)
            .U32(0)                                     // patched below
            .U32(list.Count)
            .Ascii("SVR3")
            .Bytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        foreach (byte[] section in list)
            writer.Bytes(section);

        return writer.PatchU32(8, writer.Length).ToArray();
    }

    /// <summary>A section of the given magic filled with placeholder bytes.</summary>
    public static byte[] Raw(string magic, int payloadSize) =>
        new BigEndianWriter().Ascii(magic).U32(8 + payloadSize).Zeros(payloadSize).ToArray();

    /// <summary>
    /// A section whose only content is a string table, pointed at from
    /// <paramref name="pointerOffset"/>. JNT1 keeps that pointer at 0x0C and MAT3 at 0x14.
    /// </summary>
    public static byte[] NameSection(string magic, int pointerOffset, IReadOnlyList<string> names)
    {
        var writer = new BigEndianWriter().Ascii(magic).U32(0).PadTo(pointerOffset + 4);
        int tableOffset = writer.Length;
        writer.PatchU32(pointerOffset, tableOffset);
        writer.Bytes(StringTable(names));
        return writer.PatchU32(4, writer.Length).ToArray();
    }

    /// <summary>An INF1 section holding the given (type, index) pairs.</summary>
    public static byte[] Inf1(IEnumerable<(int Type, int Index)> nodes)
    {
        var writer = new BigEndianWriter().Ascii("INF1").U32(0).PadTo(0x1C);
        writer.PatchU32(0x18, writer.Length);
        foreach ((int type, int index) in nodes)
            writer.U16(type).U16(index);

        return writer.PatchU32(4, writer.Length).ToArray();
    }

    /// <summary>A TEX1 section, one texture header per entry, each with its own image data.</summary>
    public static byte[] Tex1(IReadOnlyList<(string Name, GxTextureFormat Format, int Width, int Height)> textures)
    {
        var writer = StartTex1(textures.Count, out int headerPointer, out int namePointer);

        int headerTable = writer.Length;
        writer.PatchU32(headerPointer, headerTable);
        writer.Zeros(textures.Count * BtiTexture.HeaderSize);

        for (int i = 0; i < textures.Count; i++)
        {
            (string _, GxTextureFormat format, int width, int height) = textures[i];
            int at = headerTable + i * BtiTexture.HeaderSize;
            int image = writer.Length;
            writer.Bytes(Payload(format, width, height, 1));
            WriteBtiHeader(writer, at, format, width, height, mipCount: 1,
                           imageOffset: image - at, paletteOffset: 0, paletteCount: 0);
        }

        writer.PatchU32(namePointer, writer.Length);
        writer.Bytes(StringTable(textures.Select(t => t.Name).ToList()));
        return writer.PatchU32(4, writer.Length).ToArray();
    }

    /// <summary>A TEX1 section whose two headers point at the same image data.</summary>
    public static byte[] Tex1SharedImage(string first, string second, int width, int height)
    {
        var writer = StartTex1(2, out int headerPointer, out int namePointer);

        int headerTable = writer.Length;
        writer.PatchU32(headerPointer, headerTable);
        writer.Zeros(2 * BtiTexture.HeaderSize);

        int image = writer.Length;
        writer.Bytes(Payload(GxTextureFormat.Rgb565, width, height, 1));
        for (int i = 0; i < 2; i++)
        {
            int at = headerTable + i * BtiTexture.HeaderSize;
            WriteBtiHeader(writer, at, GxTextureFormat.Rgb565, width, height, mipCount: 1,
                           imageOffset: image - at, paletteOffset: 0, paletteCount: 0);
        }

        writer.PatchU32(namePointer, writer.Length);
        writer.Bytes(StringTable([first, second]));
        return writer.PatchU32(4, writer.Length).ToArray();
    }

    /// <summary>A TEX1 section with one C4 texture and a two-entry RGB5A3 palette.</summary>
    public static byte[] Tex1Paletted(string name, int width, int height)
    {
        var writer = StartTex1(1, out int headerPointer, out int namePointer);

        int headerTable = writer.Length;
        writer.PatchU32(headerPointer, headerTable);
        writer.Zeros(BtiTexture.HeaderSize);

        int palette = writer.Length;
        writer.U16(0xFC00).U16(0x801F);                 // opaque red, opaque blue

        int image = writer.Length;
        var indices = new byte[GxTextureFormats.MipChainSize(GxTextureFormat.C4, width, height, 1)];
        Array.Fill(indices, (byte)0x01);                // every pair of pixels is index 0 then 1
        writer.Bytes(indices);

        WriteBtiHeader(writer, headerTable, GxTextureFormat.C4, width, height, mipCount: 1,
                       imageOffset: image - headerTable, paletteOffset: palette - headerTable, paletteCount: 2);

        writer.PatchU32(namePointer, writer.Length);
        writer.Bytes(StringTable([name]));
        return writer.PatchU32(4, writer.Length).ToArray();
    }

    /// <summary>A standalone BTI file: one header at offset 0 followed by its image data.</summary>
    public static byte[] StandaloneBti(GxTextureFormat format, int width, int height, int mipCount = 1)
    {
        var writer = new BigEndianWriter().Zeros(BtiTexture.HeaderSize);
        int image = writer.Length;
        writer.Bytes(Payload(format, width, height, 1));
        WriteBtiHeader(writer, 0, format, width, height, mipCount, image, paletteOffset: 0, paletteCount: 0);
        return writer.ToArray();
    }

    private static BigEndianWriter StartTex1(int count, out int headerPointer, out int namePointer)
    {
        var writer = new BigEndianWriter()
            .Ascii("TEX1").U32(0)
            .U16(count).U16(0xFFFF);
        headerPointer = writer.Length;
        namePointer = headerPointer + 4;
        return writer.U32(0).U32(0);
    }

    /// <summary>Patches a complete BTI header into place at <paramref name="at"/>.</summary>
    private static void WriteBtiHeader(BigEndianWriter writer, int at, GxTextureFormat format,
                                       int width, int height, int mipCount,
                                       int imageOffset, int paletteOffset, int paletteCount)
    {
        var header = new BigEndianWriter()
            .U8((int)format).U8(0)
            .U16(width).U16(height)
            .U8(0).U8(0)                                                // wrapS, wrapT
            .U8(paletteCount > 0 ? 1 : 0).U8((int)GxTlutFormat.Rgb5A3)
            .U16(paletteCount)
            .U32((uint)paletteOffset)
            .U32(0)                                                     // border colour
            .U8(1).U8(1).U8(0).U8(mipCount - 1)                         // filters, min and max LOD
            .U8(mipCount).U8(0).U16(0)
            .U32((uint)imageOffset)
            .ToArray();

        for (int i = 0; i < header.Length; i += 4)
            writer.PatchU32(at + i, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(i)));
    }

    /// <summary>Image data of the right length for one mip level, filled so decoding is checkable.</summary>
    private static byte[] Payload(GxTextureFormat format, int width, int height, int levels)
    {
        var data = new byte[GxTextureFormats.MipChainSize(format, width, height, levels)];
        Array.Fill(data, (byte)0xFF);                   // white in RGB565, and opaque in CMPR
        return data;
    }

    /// <summary>A J3D string table: a count, then a hash and offset per string, then the strings.</summary>
    private static byte[] StringTable(IReadOnlyList<string> names)
    {
        var writer = new BigEndianWriter().U16(names.Count).U16(0xFFFF);
        int entryTable = writer.Length;
        writer.Zeros(names.Count * 4);

        for (int i = 0; i < names.Count; i++)
        {
            int offset = writer.Length;
            writer.Ascii(names[i]).U8(0);
            // Entry i is a u16 hash then a u16 offset; only the offset is read back.
            writer.PatchU32(entryTable + i * 4, (uint)(Hash(names[i]) << 16 | (uint)offset));
        }

        return writer.ToArray();
    }

    /// <summary>The hash J3D string tables carry. Not used when reading, but written for realism.</summary>
    private static uint Hash(string name)
    {
        uint hash = 0;
        foreach (char c in name)
            hash = (uint)((hash * 3 + c) & 0xFFFF);

        return hash;
    }
}
