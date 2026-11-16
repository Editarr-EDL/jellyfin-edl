using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Edl.Api;
using Jellyfin.Plugin.Edl.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Edl.Tests;

internal sealed record PluginResponse(int Status, IHeaderDictionary Headers, byte[] Body)
{
    public string ETag => Headers.ETag.ToString();
}

/// <summary>
/// Sends requests through <see cref="EdlController"/> with the real resolver, selector, format, and parser,
/// then executes the action result into an HTTP response. Jellyfin's authorization context, library manager,
/// and media source manager are mocked at their interfaces. This is the unit tier; verify.py run is the
/// running-server tier.
/// </summary>
internal sealed class PluginHarness
{
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();

    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IMediaSourceManager> _mediaSources = new();

    /// <summary>Gets the number of calls into Jellyfin's library and media source managers.</summary>
    public int LibraryLookups => _library.Invocations.Count + _mediaSources.Invocations.Count;

    public static User NewUser(Guid id) => new("synthetic", "provider", "reset") { Id = id };

    /// <summary>The plugin's format named <paramref name="name"/>, <c>edl</c> or <c>mcf</c>.</summary>
    public static IExportFormat Format(string name) => name switch
    {
        "edl" => new EdlExportFormat(new EdlParser()),
        "mcf" => new McfExportFormat(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown format"),
    };

    public static MediaSourceInfo Source(Guid id, string path, MediaProtocol protocol = MediaProtocol.File) =>
        new() { Id = id.ToString("N"), Path = path, Protocol = protocol };

    /// <summary>
    /// Adds a video that only <paramref name="viewers"/> can see as users. A server API key sees every video.
    /// Its first source is the item's own file.
    /// </summary>
    public Guid AddVideo(IEnumerable<User> viewers, params MediaSourceInfo[] sources)
    {
        Guid itemId = Guid.Parse(sources[0].Id);
        var video = new Video { Id = itemId, Path = sources[0].Path };
        _library.Setup(l => l.GetItemById<Video>(itemId)).Returns(video);
        _mediaSources.Setup(m => m.GetStaticMediaSources(video, false, null)).Returns(sources.ToList());
        foreach (User viewer in viewers)
        {
            _library.Setup(l => l.GetItemById<Video>(itemId, viewer)).Returns(video);
            _mediaSources.Setup(m => m.GetStaticMediaSources(video, false, viewer)).Returns(sources.ToList());
        }

        return itemId;
    }

    /// <summary>
    /// Sends <c>GET /Plugins/Edl/ByItem/{itemId}</c>, or <c>GET /Plugins/Edl/Mcf/ByItem/{itemId}</c> for
    /// <paramref name="format"/> <c>mcf</c>, as <paramref name="user"/>, or as an API key when asked.
    /// </summary>
    public async Task<PluginResponse> GetAsync(User? user, Guid itemId, Guid? mediaSourceId = null, string? ifNoneMatch = null, bool apiKey = false, string format = "edl")
    {
        _authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = user, IsAuthenticated = true, IsApiKey = apiKey });
        var http = new DefaultHttpContext { RequestServices = Services };
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = new MemoryStream();
        if (ifNoneMatch is not null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        var controller = new EdlController(
            _authorization.Object,
            new AuthorizedMediaResolver(_library.Object, _mediaSources.Object),
            new ExportSelector(NullLogger<ExportSelector>.Instance),
            new EdlExportFormat(new EdlParser()),
            new McfExportFormat())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        IActionResult result = format switch
        {
            "edl" => await controller.GetByItemId(itemId, mediaSourceId, CancellationToken.None),
            "mcf" => await controller.GetMcfByItemId(itemId, mediaSourceId, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "unknown format"),
        };
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        return new PluginResponse(http.Response.StatusCode, http.Response.Headers, ((MemoryStream)http.Response.Body).ToArray());
    }
}
