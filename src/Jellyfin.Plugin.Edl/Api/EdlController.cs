using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Edl.Api;

/// <summary>Serves a library video's export: the signed-in user's personal or default file, or the default to a server API key.</summary>
[ApiController]
[Authorize]
[Route("Plugins/Edl")]
public sealed class EdlController : ControllerBase
{
    private readonly IAuthorizationContext _authorizationContext;
    private readonly AuthorizedMediaResolver _mediaResolver;
    private readonly ExportSelector _selector;
    private readonly EdlExportFormat _edl;

    /// <summary>Initializes a new instance of the <see cref="EdlController"/> class.</summary>
    /// <param name="authorizationContext">Jellyfin's request authorization context.</param>
    /// <param name="mediaResolver">Resolves items and versions for the requester.</param>
    /// <param name="selector">Selects the personal or default export.</param>
    /// <param name="edl">The EDL format.</param>
    public EdlController(
        IAuthorizationContext authorizationContext,
        AuthorizedMediaResolver mediaResolver,
        ExportSelector selector,
        EdlExportFormat edl)
    {
        _authorizationContext = authorizationContext;
        _mediaResolver = mediaResolver;
        _selector = selector;
        _edl = edl;
    }

    /// <summary>Gets the EDL the signed-in user should play for an item, or the item's default EDL for a server API key.</summary>
    /// <param name="itemId">The library item.</param>
    /// <param name="mediaSourceId">The item's media source (version); the item's own file when omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exact file bytes, or the reason none is served.</returns>
    [HttpGet("ByItem/{itemId:guid}")]
    [Produces("text/plain", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public Task<IActionResult> GetByItemId(Guid itemId, [FromQuery] Guid? mediaSourceId, CancellationToken cancellationToken)
        => ServeAsync(itemId, mediaSourceId, _edl, cancellationToken);

    /// <summary>
    /// Authorizes the requester, item, and version, then selects and serves one format's export.
    /// A signed-in user comes only from the request's Jellyfin session. A server API key reads as the
    /// server even when the request names a user, so it sees every item but only default files.
    /// Requests with neither get 403 before any library or filesystem lookup. Every format's route goes through here.
    /// </summary>
    private async Task<IActionResult> ServeAsync(Guid itemId, Guid? mediaSourceId, IExportFormat format, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-cache";
        AuthorizationInfo authorization = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        AuthorizedMedia? media;
        if (authorization is { IsAuthenticated: true, IsApiKey: true })
        {
            media = _mediaResolver.ResolveForServerKey(itemId, mediaSourceId);
        }
        else if (authorization is { IsAuthenticated: true, User: { } user } && user.Id != Guid.Empty)
        {
            media = _mediaResolver.ResolveForUser(user, itemId, mediaSourceId);
        }
        else
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (media is null)
        {
            return NotFound();
        }

        return await _selector.SelectAsync(media, format, cancellationToken).ConfigureAwait(false) switch
        {
            ExportSelection.Found found => new FileContentResult(found.Content, format.ContentType) { EntityTag = EntityTagFor(media, format, found.Content) },
            ExportSelection.Invalid invalid => UnprocessableEntity(new EdlValidationErrorResponse(invalid.Diagnostics)),
            ExportSelection.Unreadable => StatusCode(StatusCodes.Status500InternalServerError),
            _ => NotFound(),
        };
    }

    // The tag covers requester, item, version, and format, so a validator from another response never matches.
    private static EntityTagHeaderValue EntityTagFor(AuthorizedMedia media, IExportFormat format, byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{format.Name}\n{media.Requester.CacheKey}\n{media.ItemId:N}\n{media.MediaSourceId:N}\n"));
        hash.AppendData(content);
        return new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(hash.GetHashAndReset())}\"");
    }
}
