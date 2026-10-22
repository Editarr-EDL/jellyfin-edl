namespace Jellyfin.Plugin.Edl.Models;

/// <summary>
/// One media version that a signed-in Jellyfin user is allowed to play.
/// Only <see cref="Services.AuthorizedMediaResolver"/> creates it, so export selection
/// never touches the filesystem for an item or version the user cannot see.
/// </summary>
/// <param name="UserId">The authenticated Jellyfin user. It names the personal export directory.</param>
/// <param name="ItemId">The requested library item.</param>
/// <param name="MediaSourceId">The exact version of that item, which is the item itself unless the request named another.</param>
/// <param name="MediaPath">The server's local path of that version.</param>
public sealed record AuthorizedMedia(Guid UserId, Guid ItemId, Guid MediaSourceId, string MediaPath);
