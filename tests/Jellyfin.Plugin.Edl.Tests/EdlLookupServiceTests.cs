using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Edl.Tests;

public sealed class EdlLookupServiceTests
{
    private readonly Mock<ILibraryItemResolver> _itemResolver = new();
    private readonly Mock<IFileSystem> _fileSystem = new();

    [Fact]
    public void ResolveByItemId_ReturnsItemNotFound_WhenItemMissing()
    {
        _itemResolver.Setup(x => x.Resolve(It.IsAny<Guid>()))
            .Returns(new ResolvedLibraryItem(false, false, null));

        var sut = CreateSut();

        var result = sut.ResolveByItemId(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(EdlLookupFailureReason.ItemNotFound, result.FailureReason);
    }

    [Fact]
    public void ResolveByItemId_ReturnsNotVideo_WhenItemIsNotVideo()
    {
        _itemResolver.Setup(x => x.Resolve(It.IsAny<Guid>()))
            .Returns(new ResolvedLibraryItem(true, false, null));

        var sut = CreateSut();

        var result = sut.ResolveByItemId(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(EdlLookupFailureReason.NotVideo, result.FailureReason);
    }

    [Fact]
    public void ResolveByItemId_ReturnsNoLocalPath_WhenPathMissing()
    {
        _itemResolver.Setup(x => x.Resolve(It.IsAny<Guid>()))
            .Returns(new ResolvedLibraryItem(true, true, null));

        var sut = CreateSut();

        var result = sut.ResolveByItemId(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(EdlLookupFailureReason.NoLocalPath, result.FailureReason);
    }

    [Fact]
    public void ResolveByItemId_ReturnsEdlNotFound_WhenSidecarMissing()
    {
        const string mediaPath = "/media/movie.mkv";
        _itemResolver.Setup(x => x.Resolve(It.IsAny<Guid>()))
            .Returns(new ResolvedLibraryItem(true, true, mediaPath));
        _fileSystem.Setup(x => x.FileExists("/media/movie.edl")).Returns(false);

        var sut = CreateSut();

        var result = sut.ResolveByItemId(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(EdlLookupFailureReason.EdlNotFound, result.FailureReason);
    }

    [Fact]
    public void ResolveByItemId_ReturnsFound_WhenSidecarExists()
    {
        const string mediaPath = "/media/show.mkv";
        const string edlPath = "/media/show.edl";
        _itemResolver.Setup(x => x.Resolve(It.IsAny<Guid>()))
            .Returns(new ResolvedLibraryItem(true, true, mediaPath));
        _fileSystem.Setup(x => x.FileExists(edlPath)).Returns(true);

        var sut = CreateSut();

        var result = sut.ResolveByItemId(Guid.NewGuid());

        Assert.True(result.Success);
        Assert.Equal(edlPath, result.EdlPath);
        Assert.Equal(mediaPath, result.MediaPath);
    }

    private EdlLookupService CreateSut()
    {
        return new EdlLookupService(_itemResolver.Object, _fileSystem.Object, NullLogger<EdlLookupService>.Instance);
    }
}
