namespace Jellyfin.Plugin.Edl.Models;

public enum EdlLookupFailureReason
{
    None = 0,
    ItemNotFound,
    NotVideo,
    NoLocalPath,
    EdlNotFound
}

public sealed record EdlLookupResult(
    bool Success,
    string? MediaPath,
    string? EdlPath,
    EdlLookupFailureReason FailureReason)
{
    public static EdlLookupResult Found(string mediaPath, string edlPath) =>
        new(true, mediaPath, edlPath, EdlLookupFailureReason.None);

    public static EdlLookupResult Failure(EdlLookupFailureReason reason) =>
        new(false, null, null, reason);
}
