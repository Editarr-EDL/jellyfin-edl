using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

public interface IEdlParser
{
    EdlValidationResult Validate(string[] lines);
}
