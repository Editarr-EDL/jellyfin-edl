using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

public interface IEdlParser
{
    ExportValidationResult Validate(string[] lines);
}
