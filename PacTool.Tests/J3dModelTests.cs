using PacTool.Formats;
using PacTool.Gx;
using PacTool.Imaging;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// The reference data in this repository contains no BMD or BDL, so these tests build the files
/// they read. Every offset is written the way the format specifies - relative to its own section,
/// and, inside TEX1, relative to the individual texture header - so the reader is exercised on the
/// same self-referential structure a real model has.
/// </summary>
public class J3dModelTests
{
    [Fact]
    public void ABmdIsRecognisedAndItsSectionsInventoried()
    {
        byte[] model = J3dBuilder.Build("bmd3", [
            J3dBuilder.NameSection("JNT1", 0x0C, ["root", "arm_l", "arm_r"]),
            J3dBuilder.NameSection("MAT3", 0x14, ["skin", "cloth"]),
            J3dBuilder.Tex1([("grass", GxTextureFormat.Rgb565, 8, 8)]),
        ]);

        Assert.True(J3dModel.LooksLikeModel(model));
        Assert.Equal(ContentKind.J3dModel, ContentSniffer.Identify("stage.bmd", model, out _));

        J3dModel parsed = J3dModel.Parse(model, "stage.bmd");

        Assert.Empty(parsed.Warnings);
        Assert.Equal("bmd3", parsed.Variant);
        Assert.Equal("SVR3", parsed.Subversion);
        Assert.Equal(["JNT1", "MAT3", "TEX1"], parsed.Sections.Select(s => s.Magic));
        Assert.Equal(["root", "arm_l", "arm_r"], parsed.JointNames);
        Assert.Equal(["skin", "cloth"], parsed.MaterialNames);
    }

    [Fact]
    public void ABdlParsesThroughTheSameReader()
    {
        byte[] model = J3dBuilder.Build("bdl4", [
            J3dBuilder.Raw("MDL3", 64),
            J3dBuilder.Tex1([("panel", GxTextureFormat.Cmpr, 8, 8)]),
        ]);

        J3dModel parsed = J3dModel.Parse(model, "stage.bdl");

        Assert.Equal("bdl4", parsed.Variant);
        Assert.Equal(2, parsed.Sections.Count);
        Assert.Equal("panel", Assert.Single(parsed.Textures).Name);
    }

    [Fact]
    public void Tex1TexturesDecodeThroughTheSharedGxDecoder()
    {
        byte[] model = J3dBuilder.Build("bmd3", [
            J3dBuilder.Tex1([
                ("white", GxTextureFormat.Rgb565, 4, 4),
                ("big", GxTextureFormat.Cmpr, 16, 16),
            ]),
        ]);

        J3dModel parsed = J3dModel.Parse(model, "stage.bmd");

        Assert.Equal(2, parsed.Textures.Count);
        Assert.Equal("white", parsed.Textures[0].Name);
        Assert.Equal(GxTextureFormat.Rgb565, parsed.Textures[0].Format);

        Rgba32Image image = parsed.Textures[0].Decode();
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        // J3dBuilder fills RGB565 payloads with 0xFFFF, which is white.
        Assert.Equal((255, 255, 255, 255), GxImageDecoderTests.PixelAt(image, 0, 0));

        Assert.Equal(16, parsed.Textures[1].Width);
        Assert.Equal(16, parsed.Textures[1].Decode().Width);
    }

    [Fact]
    public void TheSceneGraphIsFlattenedWithItsNestingPreserved()
    {
        byte[] model = J3dBuilder.Build("bmd3", [
            J3dBuilder.NameSection("JNT1", 0x0C, ["root", "child"]),
            J3dBuilder.Inf1([(1, 0), (0x10, 0), (1, 0), (0x10, 1), (0x11, 3), (2, 0), (2, 0), (0, 0)]),
        ]);

        J3dModel parsed = J3dModel.Parse(model, "stage.bmd");

        Assert.Equal(3, parsed.Hierarchy.Count);
        Assert.Equal((1, 0x10, 0), (parsed.Hierarchy[0].Depth, parsed.Hierarchy[0].Type, parsed.Hierarchy[0].Index));
        Assert.Equal((2, 0x10, 1), (parsed.Hierarchy[1].Depth, parsed.Hierarchy[1].Type, parsed.Hierarchy[1].Index));
        Assert.Equal("material", parsed.Hierarchy[2].TypeName);

        // The listing resolves joint indices against JNT1.
        Assert.Contains("'child'", parsed.Describe("stage.bmd"));
    }

    [Fact]
    public void SeveralTextureHeadersMaySharePixelData()
    {
        byte[] model = J3dBuilder.Build("bmd3", [J3dBuilder.Tex1SharedImage("copyA", "copyB", 8, 8)]);

        J3dModel parsed = J3dModel.Parse(model, "stage.bmd");

        Assert.Equal(2, parsed.Textures.Count);
        Assert.Equal(parsed.Textures[0].ImageOffset, parsed.Textures[1].ImageOffset);
        Assert.Equal(parsed.Textures[0].Data, parsed.Textures[1].Data);
    }

    [Fact]
    public void APalettedTextureReadsItsEmbeddedTlut()
    {
        byte[] model = J3dBuilder.Build("bmd3", [J3dBuilder.Tex1Paletted("icon", 8, 8)]);

        BtiTexture texture = Assert.Single(J3dModel.Parse(model, "stage.bmd").Textures);

        Assert.Equal(GxTextureFormat.C4, texture.Format);
        Assert.NotNull(texture.Palette);
        Assert.Equal(2, texture.Palette!.Count);
        // J3dBuilder's palette is opaque red then opaque blue, and its indices alternate.
        Assert.Equal((255, 0, 0, 255), GxImageDecoderTests.PixelAt(texture.Decode(), 0, 0));
        Assert.Equal((0, 0, 255, 255), GxImageDecoderTests.PixelAt(texture.Decode(), 1, 0));
    }

    [Fact]
    public void ASectionThatOverrunsTheFileIsReportedRatherThanRead()
    {
        byte[] model = J3dBuilder.Build("bmd3", [J3dBuilder.Raw("VTX1", 32)]);
        // Inflate the section's stated size past the end of the file.
        model[J3dModel.HeaderSize + 7] = 0xF0;

        J3dModel parsed = J3dModel.Parse(model, "stage.bmd");

        Assert.Empty(parsed.Sections);
        Assert.Contains(parsed.Warnings, w => w.Contains("does not fit"));
    }

    [Fact]
    public void SomethingThatIsNotAJ3dIsRejected()
    {
        Assert.False(J3dModel.LooksLikeModel("J3D1bck1"u8.ToArray()));
        Assert.Throws<PacFormatException>(() => J3dModel.Parse("not a model at all"u8.ToArray(), "x.bmd"));
    }
}

public class BtiTextureTests
{
    [Fact]
    public void AStandaloneFileIsSniffedAndDecoded()
    {
        byte[] bti = J3dBuilder.StandaloneBti(GxTextureFormat.Rgb565, 8, 8);

        Assert.True(BtiTexture.LooksLikeBti(bti));
        Assert.Equal(ContentKind.BtiTexture, ContentSniffer.Identify("tile.bti", bti, out _));

        BtiTexture texture = BtiTexture.Parse(bti, 0, "tile.bti");

        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(1, texture.MipCount);
        Assert.Equal((255, 255, 255, 255), GxImageDecoderTests.PixelAt(texture.Decode(), 0, 0));
    }

    [Fact]
    public void AMipCountBiggerThanTheDataIsClampedRatherThanTrusted()
    {
        // Claim five levels but supply only enough bytes for the base one.
        byte[] bti = J3dBuilder.StandaloneBti(GxTextureFormat.Rgb565, 8, 8, mipCount: 5);

        BtiTexture texture = BtiTexture.Parse(bti, 0, "tile.bti");

        Assert.Equal(1, texture.MipCount);
    }

    [Fact]
    public void AFormatTheHardwareDoesNotHaveIsRejected()
    {
        byte[] bti = J3dBuilder.StandaloneBti(GxTextureFormat.Rgb565, 8, 8);
        bti[0] = 0x07;                                  // no GX format has value 7

        Assert.False(BtiTexture.LooksLikeBti(bti));
        Assert.Throws<PacFormatException>(() => BtiTexture.Parse(bti, 0, "tile.bti"));
    }
}
