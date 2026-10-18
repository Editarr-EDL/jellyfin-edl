using System.Text;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Edl.Api;

[ApiController]
[Authorize]
[Route("Plugins/Edl")]
public sealed class EdlController : ControllerBase
{
    private readonly IEdlLookupService _lookupService;
    private readonly IEdlParser _edlParser;
    private readonly ILogger<EdlController> _logger;

    public EdlController(
        IEdlLookupService lookupService,
        IEdlParser edlParser,
        ILogger<EdlController> logger)
    {
        _lookupService = lookupService;
        _edlParser = edlParser;
        _logger = logger;
    }

    [HttpGet("ByItem/{itemId:guid}")]
    [Produces("text/plain", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetByItemId(Guid itemId, CancellationToken cancellationToken)
    {
        EdlLookupResult lookup = _lookupService.ResolveByItemId(itemId);
        if (!lookup.Success)
        {
            return NotFound();
        }

        string edlPath = lookup.EdlPath!;
        string[] lines;

        try
        {
            lines = await System.IO.File.ReadAllLinesAsync(edlPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading EDL for item {ItemId}", itemId);
            return NotFound();
        }

        EdlValidationResult validation = _edlParser.Validate(lines);
        if (!validation.IsValid)
        {
            return UnprocessableEntity(new EdlValidationErrorResponse(validation.Diagnostics));
        }

        string rawEdl = string.Join(Environment.NewLine, lines);
        return Content(rawEdl, "text/plain", Encoding.UTF8);
    }
}
