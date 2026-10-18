using Jellyfin.Plugin.Edl.Api;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Edl.Tests;

public sealed class EdlControllerTests
{
    private readonly Mock<IEdlLookupService> _lookup = new();
    private readonly Mock<IEdlParser> _parser = new();

    [Fact]
    public async Task GetByItemId_ReturnsNotFound_WhenLookupFails()
    {
        _lookup.Setup(x => x.ResolveByItemId(It.IsAny<Guid>()))
            .Returns(EdlLookupResult.Failure(EdlLookupFailureReason.ItemNotFound));

        var sut = CreateSut();

        IActionResult result = await sut.GetByItemId(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetByItemId_ReturnsUnprocessableEntity_WhenEdlInvalid()
    {
        string filePath = CreateTempFile("0 5 cut");
        _lookup.Setup(x => x.ResolveByItemId(It.IsAny<Guid>()))
            .Returns(EdlLookupResult.Found("/media/video.mkv", filePath));
        _parser.Setup(x => x.Validate(It.IsAny<string[]>()))
            .Returns(EdlValidationResult.Invalid(new[] { new EdlValidationDiagnostic(1, "bad") }));

        var sut = CreateSut();

        IActionResult result = await sut.GetByItemId(Guid.NewGuid(), CancellationToken.None);

        var objectResult = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var payload = Assert.IsType<EdlValidationErrorResponse>(objectResult.Value);
        Assert.Single(payload.Diagnostics);
    }

    [Fact]
    public async Task GetByItemId_ReturnsTextPlain_WhenEdlValid()
    {
        const string rawEdl = "0 5 cut\n10 20 mute";
        string filePath = CreateTempFile(rawEdl);

        _lookup.Setup(x => x.ResolveByItemId(It.IsAny<Guid>()))
            .Returns(EdlLookupResult.Found("/media/video.mkv", filePath));
        _parser.Setup(x => x.Validate(It.IsAny<string[]>()))
            .Returns(EdlValidationResult.Valid);

        var sut = CreateSut();

        IActionResult result = await sut.GetByItemId(Guid.NewGuid(), CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.StartsWith("text/plain", content.ContentType, StringComparison.Ordinal);
        Assert.Equal(rawEdl.Replace("\n", Environment.NewLine, StringComparison.Ordinal), content.Content);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        var authorize = typeof(EdlController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        Assert.NotEmpty(authorize);
    }

    private EdlController CreateSut()
    {
        return new EdlController(_lookup.Object, _parser.Object, NullLogger<EdlController>.Instance);
    }

    private static string CreateTempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"edl-test-{Guid.NewGuid():N}.edl");
        File.WriteAllText(path, content);
        return path;
    }
}
