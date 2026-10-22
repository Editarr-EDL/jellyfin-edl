using Jellyfin.Plugin.Edl.Services;

namespace Jellyfin.Plugin.Edl.Models;

/// <summary>Who an export is selected for. It decides which files are eligible.</summary>
public abstract record Requester
{
    private Requester()
    {
    }

    /// <summary>Gets the requester's part of a cache validator. No two requesters share one.</summary>
    public abstract string CacheKey { get; }

    /// <summary>A signed-in Jellyfin user, who gets their personal file before the default.</summary>
    /// <param name="UserId">The user's Jellyfin ID.</param>
    public sealed record Account(Guid UserId) : Requester
    {
        /// <inheritdoc />
        public override string CacheKey => ExportLayout.ExportId(UserId);
    }

    /// <summary>A server API key, which gets only the default file, even when the request names a user.</summary>
    public sealed record ServerKey : Requester
    {
        /// <summary>Gets the only instance.</summary>
        public static readonly ServerKey Instance = new();

        private ServerKey()
        {
        }

        /// <inheritdoc />
        public override string CacheKey => "server-key";
    }
}
