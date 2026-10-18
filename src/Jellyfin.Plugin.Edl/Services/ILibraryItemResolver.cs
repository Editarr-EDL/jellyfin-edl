using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

public sealed record ResolvedLibraryItem(bool Found, bool IsVideo, string? PrimaryPath);

public interface ILibraryItemResolver
{
    ResolvedLibraryItem Resolve(Guid itemId);
}
