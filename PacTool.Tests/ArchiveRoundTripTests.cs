using Xunit;

namespace PacTool.Tests;

/// <summary>
/// Guards the unpack and repack path that the content converters were added alongside: reading an
/// archive and writing it back must still land on the same bytes.
/// </summary>
public class ArchiveRoundTripTests
{
    [Fact]
    public void AnArchiveRebuiltFromItsOwnMembersIsByteIdentical()
    {
        byte[] original = Archive([
            ("first.pcp", 64),
            ("second.mpc", 32),
            ("first.pcp", 96),                          // names are not unique in the shipped data
        ]);

        using var stream = new MemoryStream(original, writable: false);
        using var archive = PacArchive.Open(stream, "test.pac");

        var sources = archive.Members.Select(m => new PacSource
        {
            Name = m.Name,
            Length = m.Size,
            Origin = $"test.pac#{m.Index}",
            Reserved0 = m.Reserved0,
            Reserved1 = m.Reserved1,
            Open = () => new MemoryStream(archive.ReadMember(m), writable: false),
        }).ToList();

        using var rebuilt = new MemoryStream();
        PacBuilder.Build(rebuilt, sources, alignment: 1);

        Assert.Equal(original, rebuilt.ToArray());
    }

    [Fact]
    public void PayloadsArePaddedUpToTheAlignmentAndTheStatedSizeIncludesThePadding()
    {
        var sources = new List<PacSource> { PacSource.FromBytes("odd.bin", new byte[40]) };

        using var built = new MemoryStream();
        PacBuilder.Build(built, sources, alignment: 32);

        byte[] bytes = built.ToArray();
        Assert.Equal(32 + 64, bytes.Length);

        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = PacArchive.Open(stream, "built.pac");
        Assert.Equal(64u, Assert.Single(archive.Members).Size);
    }

    [Fact]
    public void ANameTooLongForTheFieldIsRejectedRatherThanTruncated()
    {
        var sources = new List<PacSource> { PacSource.FromBytes("a-name-well-over-sixteen.bin", new byte[32]) };

        using var built = new MemoryStream();
        Assert.Throws<PacFormatException>(() => PacBuilder.Build(built, sources, alignment: 32));
    }

    [Fact]
    public void AChainThatDoesNotLandOnTheEndOfTheFileIsRejected()
    {
        byte[] archive = Archive([("only.pcp", 32)]);

        using var stream = new MemoryStream(archive[..^8], writable: false);
        Assert.Throws<PacFormatException>(() => PacArchive.Open(stream, "short.pac"));
    }

    private static byte[] Archive(IReadOnlyList<(string Name, int Size)> members)
    {
        var writer = new BigEndianWriter();
        foreach ((string name, int size) in members)
        {
            writer.Ascii("CAPR").U32(size).U32(0).U32(0).Field(name, 16);
            for (int i = 0; i < size; i++)
                writer.U8(i & 0xFF);
        }

        return writer.ToArray();
    }
}
