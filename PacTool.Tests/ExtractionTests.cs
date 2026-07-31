using PacTool.Extract;
using PacTool.Formats;
using PacTool.Gx;
using Xunit;

namespace PacTool.Tests;

/// <summary>End-to-end checks: an archive in, a directory of usable files out.</summary>
public class ContentExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pactool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AnArchiveIsWalkedAndEachMemberDecodedIntoItsOwnDirectory()
    {
        byte[] archive = Archive([
            ("stage.pcp", PicturePackTests.Build([
                PicturePackTests.Texture("tex0", GxTextureFormat.Cmpr, 8, 8, maxLod: 0),
                PicturePackTests.Texture("tex1", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0),
            ])),
            ("map.dat", MapDat()),
        ]);

        ExtractResult result = ContentExtractor.ExtractArchive("test.pac", archive, _directory, Options());

        Assert.Equal(2, result.ImagesWritten);
        Assert.True(File.Exists(Path.Combine(_directory, "stage.pcp", "textures", "tex0.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "stage.pcp", "textures", "tex1.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "stage.pcp", "textures", "textures.txt")));
        Assert.Contains("'b00blkmm'", File.ReadAllText(Path.Combine(_directory, "map.dat", "directory.txt")));
    }

    [Fact]
    public void MipLevelsAreWrittenOnlyWhenAskedFor()
    {
        byte[] pack = PicturePackTests.Build([
            PicturePackTests.Texture("chain", GxTextureFormat.Cmpr, 16, 16, maxLod: 4),
        ]);

        ContentExtractor.Extract("a.pcp", pack, Path.Combine(_directory, "base"), Options());
        ContentExtractor.Extract("a.pcp", pack, Path.Combine(_directory, "all"), Options(allMips: true));

        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "base", "textures"), "*.png"));
        Assert.Equal(5, Directory.GetFiles(Path.Combine(_directory, "all", "textures"), "*.png").Length);
        Assert.True(File.Exists(Path.Combine(_directory, "all", "textures", "chain.mip4.png")));
    }

    [Fact]
    public void RawKeepsTheStoredBytesAlongsideThePng()
    {
        byte[] pack = PicturePackTests.Build([
            PicturePackTests.Texture("tex", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0),
        ]);

        ContentExtractor.Extract("a.pcp", pack, _directory, Options(keepRaw: true));

        Assert.True(File.Exists(Path.Combine(_directory, "textures", "tex.png")));
        Assert.Equal(32, new FileInfo(Path.Combine(_directory, "textures", "tex.bin")).Length);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData("")]
    public void ATextureNameCannotEscapeTheOutputDirectory(string name)
    {
        // Names come out of the archive, so they are untrusted.
        byte[] pack = PicturePackTests.Build([
            PicturePackTests.Texture(name, GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0),
        ]);

        ContentExtractor.Extract("a.pcp", pack, _directory, Options());

        string textures = Path.Combine(_directory, "textures");
        string written = Assert.Single(Directory.GetFiles(textures, "*.png"));
        Assert.StartsWith(Path.GetFullPath(textures) + Path.DirectorySeparatorChar, Path.GetFullPath(written));
    }

    [Fact]
    public void DuplicateNamesGetDistinctFilesRatherThanOverwritingEachOther()
    {
        byte[] pack = PicturePackTests.Build([
            PicturePackTests.Texture("same", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0),
            PicturePackTests.Texture("same", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0),
        ]);

        ContentExtractor.Extract("a.pcp", pack, _directory, Options());

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "textures"), "*.png").Length);
    }

    [Fact]
    public void SomethingUnrecognisedIsCopiedOutRatherThanDropped()
    {
        byte[] blob = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

        ExtractResult result = ContentExtractor.Extract("mystery.dat", blob, _directory, Options());

        Assert.Equal(1, result.Unrecognised);
        Assert.Equal(blob, File.ReadAllBytes(Path.Combine(_directory, "mystery.dat")));
    }

    [Fact]
    public void AJ3dModelYieldsItsSectionsAndItsTextures()
    {
        byte[] model = J3dBuilder.Build("bmd3", [
            J3dBuilder.NameSection("JNT1", 0x14, ["root"]),
            J3dBuilder.Tex1([("bark", GxTextureFormat.Rgb565, 8, 8)]),
        ]);

        ExtractResult result = ContentExtractor.Extract("tree.bmd", model, _directory, Options());

        Assert.Equal(1, result.ImagesWritten);
        Assert.True(File.Exists(Path.Combine(_directory, "textures", "bark.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "sections", "00_JNT1.bin")));
        Assert.True(File.Exists(Path.Combine(_directory, "sections", "01_TEX1.bin")));
        Assert.Contains("'root'", File.ReadAllText(Path.Combine(_directory, "model.txt")));
    }

    [Fact]
    public void ASceneTableYieldsItsListingAndItsGeometry()
    {
        byte[] pack = PicturePackTests.Build(
            [PicturePackTests.Texture("t", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0)], Scene());

        ExtractResult result = ContentExtractor.Extract("a.scn", pack, _directory, Options());

        Assert.Equal(1, result.MeshesWritten);
        string listing = File.ReadAllText(Path.Combine(_directory, "scene.txt"));
        Assert.Contains("'mesh'", listing);
        Assert.Contains("3 vert, 1 tri", listing);
        Assert.True(File.Exists(Path.Combine(_directory, "scene.bin")));

        string obj = File.ReadAllText(Path.Combine(_directory, "geometry", "mesh.M0.obj"));
        Assert.Contains("v 1 2 3", obj);
        Assert.Contains("f 1/1/1 2/2/2 3/3/3", obj);

        // One combined file too, named after the container.
        Assert.True(File.Exists(Path.Combine(_directory, "a.obj")));
    }

    [Fact]
    public void DisplayListsAreKeptVerbatimOnlyWhenAskedFor()
    {
        byte[] pack = PicturePackTests.Build(
            [PicturePackTests.Texture("t", GxTextureFormat.Rgb5A3, 4, 4, maxLod: 0)], Scene());

        ContentExtractor.Extract("a.scn", pack, Path.Combine(_directory, "plain"), Options());
        ContentExtractor.Extract("a.scn", pack, Path.Combine(_directory, "raw"), Options(keepRaw: true));

        Assert.False(Directory.Exists(Path.Combine(_directory, "plain", "displaylists")));
        Assert.True(File.Exists(Path.Combine(_directory, "raw", "displaylists", "mesh.M0.bin")));
    }

    /// <summary>A scene table with one shape: a section header, then a shape record and its display list.</summary>
    private static byte[] Scene()
    {
        var vertices = new BigEndianWriter();
        foreach ((int x, int y, int z) in new[] { (1, 2, 3), (4, 5, 6), (7, 8, 9) })
        {
            vertices.U16(x * 256).U16(y * 256).U16(z * 256)     // position, 8-bit fraction
                    .U8(0).U8(64).U8(0)                          // normal, 6-bit fraction
                    .U16(0).U16(0);                              // texture coordinate
        }

        byte[] list = new BigEndianWriter().U8(0x98).U16(3).Bytes(vertices.ToArray()).PadTo(64).ToArray();
        return new BigEndianWriter()
            .U16(1).Zeros(30)
            .U32(0).Field("mesh", 8).Field("M0", 4)
                .Zeros(6).U16(list.Length + 32).U16(0).U16(list.Length).U16(3).U16(1)
            .Bytes(list).Zeros(32)
            .ToArray();
    }

    [Fact]
    public void ListingsAreWrittenWithoutAByteOrderMark()
    {
        ContentExtractor.Extract("map.dat", MapDat(), _directory, Options());

        byte[] listing = File.ReadAllBytes(Path.Combine(_directory, "directory.txt"));
        Assert.Equal((byte)'#', listing[0]);
    }

    private static ExtractOptions Options(bool allMips = false, bool keepRaw = false) =>
        new() { AllMips = allMips, KeepRaw = keepRaw };

    private static byte[] MapDat() => new BigEndianWriter()
        .U32(DataDirectory.HeaderSize + 4 + 20)         // table size, measured from the block start
        .U32(1)
        .U32(4)                                         // one offset, relative to 0x08
        .U16(0).Field("b00blkmm", 8).Zeros(6).U32(0)
        .ToArray();

    /// <summary>Wraps members in a CAPR archive.</summary>
    private static byte[] Archive(IReadOnlyList<(string Name, byte[] Data)> members)
    {
        var writer = new BigEndianWriter();
        foreach ((string name, byte[] data) in members)
        {
            int padded = (data.Length + 31) / 32 * 32;
            writer.Ascii("CAPR").U32(padded).U32(0).U32(0).Field(name, 16)
                  .Bytes(data).Zeros(padded - data.Length);
        }

        return writer.ToArray();
    }
}

public class ContentSnifferTests
{
    [Fact]
    public void TheBytesWinWhenTheNameDisagreesWithThem()
    {
        byte[] pack = PicturePackTests.Build([
            PicturePackTests.Texture("t", GxTextureFormat.Cmpr, 8, 8, maxLod: 0),
        ]);

        // Named .mpc, but nothing about it is an MPC.
        Assert.Equal(ContentKind.PicturePack, ContentSniffer.Identify("thing.mpc", pack, out _));
    }

    [Fact]
    public void TheThreeStageDirectoriesAreRecognisedByName()
    {
        var block = new BigEndianWriter().U32(12).U32(1).U32(4).ToArray();

        Assert.Equal(ContentKind.DataDirectory, ContentSniffer.Identify("bg.dat", block, out _));
        Assert.Equal(ContentKind.Unknown, ContentSniffer.Identify("other.dat", block, out _));
    }

    [Fact]
    public void ACaprArchiveIsRecognisedBeforeAnythingElse()
    {
        byte[] archive = new BigEndianWriter()
            .Ascii("CAPR").U32(32).U32(0).U32(0).Field("inner.pcp", 16).Zeros(32)
            .ToArray();

        Assert.Equal(ContentKind.Archive, ContentSniffer.Identify("outer.pac", archive, out _));
    }

    [Fact]
    public void EveryKindHasADescription()
    {
        foreach (ContentKind kind in Enum.GetValues<ContentKind>())
            Assert.False(string.IsNullOrWhiteSpace(ContentSniffer.Describe(kind)));
    }
}
