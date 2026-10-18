using Jellyfin.Plugin.Edl.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Edl.Services;

public sealed class JellyfinLibraryItemResolver : ILibraryItemResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellyfinLibraryItemResolver> _logger;

    public JellyfinLibraryItemResolver(ILibraryManager libraryManager, ILogger<JellyfinLibraryItemResolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public ResolvedLibraryItem Resolve(Guid itemId)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogInformation("Item {ItemId} was not found", itemId);
            return new ResolvedLibraryItem(false, false, null);
        }

        if (item is not Video video)
        {
            _logger.LogInformation("Item {ItemId} is not a video", itemId);
            return new ResolvedLibraryItem(true, false, null);
        }

        if (string.IsNullOrWhiteSpace(video.Path) || !LooksLikeLocalPath(video.Path))
        {
            _logger.LogInformation("Video item {ItemId} does not have a usable local path", itemId);
            return new ResolvedLibraryItem(true, true, null);
        }

        return new ResolvedLibraryItem(true, true, video.Path);
    }

    private static bool LooksLikeLocalPath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return false;
        }

        return Path.IsPathRooted(path);
    }
}
