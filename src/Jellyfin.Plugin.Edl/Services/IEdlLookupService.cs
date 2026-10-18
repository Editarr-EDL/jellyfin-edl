using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

public interface IEdlLookupService
{
    EdlLookupResult ResolveByItemId(Guid itemId);
}
