using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Edl.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// Resolves a requested item and version to a local media path through Jellyfin's own access rules for one user,
/// or across the whole library for a server API key.
/// </summary>
public sealed class AuthorizedMediaResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>Initializes a new instance of the <see cref="AuthorizedMediaResolver"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="mediaSourceManager">Jellyfin's media source manager.</param>
    public AuthorizedMediaResolver(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Returns the version the user may play, or null when the item is not a video visible to the user,
    /// the version is not one of that item's media sources for the user, or the version has no local file.
    /// </summary>
    /// <param name="user">The authenticated Jellyfin user.</param>
    /// <param name="itemId">The requested item.</param>
    /// <param name="mediaSourceId">The requested version, or null for the item's own file.</param>
    /// <returns>The authorized media, or null.</returns>
    public AuthorizedMedia? ResolveForUser(User user, Guid itemId, Guid? mediaSourceId) =>
        Resolve(new Requester.Account(user.Id), _libraryManager.GetItemById<Video>(itemId, user), user, itemId, mediaSourceId);

    /// <summary>
    /// Returns the version for a server API key, which sees every library item, or null when the item
    /// is not a video, the version is not one of its media sources, or the version has no local file.
    /// </summary>
    /// <param name="itemId">The requested item.</param>
    /// <param name="mediaSourceId">The requested version, or null for the item's own file.</param>
    /// <returns>The authorized media, or null.</returns>
    public AuthorizedMedia? ResolveForServerKey(Guid itemId, Guid? mediaSourceId) =>
        Resolve(Requester.ServerKey.Instance, _libraryManager.GetItemById<Video>(itemId), null, itemId, mediaSourceId);

    private AuthorizedMedia? Resolve(Requester requester, Video? video, User? user, Guid itemId, Guid? mediaSourceId)
    {
        if (video is null)
        {
            return null;
        }

        Guid versionId = mediaSourceId ?? itemId;
        MediaSourceInfo? source = _mediaSourceManager.GetStaticMediaSources(video, false, user)
            .FirstOrDefault(s => Guid.TryParse(s.Id, out Guid id) && id == versionId);
        if (source is not { Protocol: MediaProtocol.File } || string.IsNullOrEmpty(source.Path) || !Path.IsPathRooted(source.Path))
        {
            return null;
        }

        return new AuthorizedMedia(requester, itemId, versionId, source.Path);
    }
}
