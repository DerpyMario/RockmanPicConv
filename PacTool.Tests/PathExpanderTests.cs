using PacTool.Cli;
using Xunit;

namespace PacTool.Tests;

/// <summary>
/// Turning command-line arguments into a list of files: the part that makes batch conversion and
/// dropping a folder onto the executable behave the same way.
/// </summary>
public sealed class PathExpanderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pactool-paths").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AFileNamedOutrightIsTakenWhateverItIsCalled()
    {
        // The extension filter is for expanding a directory; naming a file is an explicit request.
        string odd = Touch("notes.readme");

        PathExpander.Result result = PathExpander.Expand([odd], PathExpander.ArchiveExtensions);

        Assert.Equal([odd], result.Files);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void ADirectoryContributesOnlyTheKindsTheCommandHandles()
    {
        Touch("stage.pac");
        Touch("tool.exe");
        Touch("pack.bat");
        Touch("textures.pcp");

        PathExpander.Result archives = PathExpander.Expand([_root], PathExpander.ArchiveExtensions);
        PathExpander.Result content = PathExpander.Expand([_root], PathExpander.ContentExtensions);

        Assert.Equal(["stage.pac"], Names(archives));
        Assert.Equal(["stage.pac", "textures.pcp"], Names(content));
        Assert.Equal(1, archives.Directories);
    }

    [Fact]
    public void EverythingInADirectoryIsTakenWhenAsked()
    {
        Touch("stage.pac");
        Touch("tool.exe");

        PathExpander.Result result = PathExpander.Expand([_root], PathExpander.ArchiveExtensions, all: true);

        Assert.Equal(["stage.pac", "tool.exe"], Names(result));
    }

    [Fact]
    public void SubdirectoriesAreOnlyEnteredWhenAsked()
    {
        Touch("top.pac");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        Touch(Path.Combine("nested", "deep.pac"));

        Assert.Equal(["top.pac"], Names(PathExpander.Expand([_root], PathExpander.ArchiveExtensions)));
        Assert.Equal(["deep.pac", "top.pac"],
                     Names(PathExpander.Expand([_root], PathExpander.ArchiveExtensions, recurse: true)).Order());
    }

    [Fact]
    public void AWildcardIsExpandedHereBecauseTheWindowsShellDoesNot()
    {
        Touch("Stage01.pac");
        Touch("Stage02.pac");
        Touch("Fixed.pac");

        PathExpander.Result result = PathExpander.Expand(
            [Path.Combine(_root, "Stage*.pac")], PathExpander.ArchiveExtensions);

        Assert.Equal(["Stage01.pac", "Stage02.pac"], Names(result).Order());
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void AFileThatArrivesTwiceIsOnlyConvertedOnce()
    {
        string named = Touch("stage.pac");

        PathExpander.Result result = PathExpander.Expand([named, _root, named], PathExpander.ArchiveExtensions);

        Assert.Single(result.Files);
    }

    [Fact]
    public void AnInputThatMatchesNothingIsReportedRatherThanIgnored()
    {
        Touch("stage.pac");
        string missing = Path.Combine(_root, "absent.pac");
        string emptyPattern = Path.Combine(_root, "nothing*.pac");

        PathExpander.Result result = PathExpander.Expand(
            [missing, emptyPattern, _root], PathExpander.ArchiveExtensions);

        Assert.Equal([missing, emptyPattern], result.Missing);
        Assert.Equal(["stage.pac"], Names(result));
    }

    [Fact]
    public void ADirectoryWithNothingOfInterestCountsAsAMiss()
    {
        Touch("tool.exe");

        PathExpander.Result result = PathExpander.Expand([_root], PathExpander.ArchiveExtensions);

        Assert.Empty(result.Files);
        Assert.Equal([_root], result.Missing);
    }

    private string Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static IEnumerable<string> Names(PathExpander.Result result) =>
        result.Files.Select(Path.GetFileName)!;
}
