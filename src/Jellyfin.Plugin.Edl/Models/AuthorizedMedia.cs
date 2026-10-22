namespace Jellyfin.Plugin.Edl.Models;

/// <summary>
/// One media version that a requester is allowed to read exports for.
/// Only <see cref="Services.AuthorizedMediaResolver"/> creates it, so export selection
/// never touches the filesystem for an item or version the requester cannot see.
/// </summary>
/// <param name="Requester">The signed-in user or server API key. It decides which export files are eligible.</param>
/// <param name="ItemId">The requested library item.</param>
/// <param name="MediaSourceId">The exact version of that item, which is the item itself unless the request named another.</param>
/// <param name="MediaPath">The server's local path of that version.</param>
public sealed record AuthorizedMedia(Requester Requester, Guid ItemId, Guid MediaSourceId, string MediaPath);
