using Jellyfin.Plugin.Edl.Models;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Edl.Services;

public sealed class EdlLookupService : IEdlLookupService
{
    private readonly ILibraryItemResolver _itemResolver;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<EdlLookupService> _logger;

    public EdlLookupService(
        ILibraryItemResolver itemResolver,
        IFileSystem fileSystem,
        ILogger<EdlLookupService> logger)
    {
        _itemResolver = itemResolver;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public EdlLookupResult ResolveByItemId(Guid itemId)
    {
        ResolvedLibraryItem resolvedItem = _itemResolver.Resolve(itemId);
        if (!resolvedItem.Found)
        {
            return EdlLookupResult.Failure(EdlLookupFailureReason.ItemNotFound);
        }

        if (!resolvedItem.IsVideo)
        {
            return EdlLookupResult.Failure(EdlLookupFailureReason.NotVideo);
        }

        if (string.IsNullOrWhiteSpace(resolvedItem.PrimaryPath))
        {
            return EdlLookupResult.Failure(EdlLookupFailureReason.NoLocalPath);
        }

        string edlPath = Path.ChangeExtension(resolvedItem.PrimaryPath, ".edl");
        if (!_fileSystem.FileExists(edlPath))
        {
            _logger.LogInformation("No sidecar EDL found for item {ItemId}", itemId);
            return EdlLookupResult.Failure(EdlLookupFailureReason.EdlNotFound);
        }

        return EdlLookupResult.Found(resolvedItem.PrimaryPath, edlPath);
    }
}
