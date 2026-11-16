namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// Checks an MCF <c>SOURCE</c> or <c>IMDB</c> URL the way Editarr does with Python 3.11's <c>urlsplit</c>:
/// an <c>http</c> or <c>https</c> scheme in any case, a non-empty authority, and for IMDb a host of
/// <c>imdb.com</c> or a subdomain of it. It repeats urlsplit's preprocessing and authority split but not
/// its NFKC or bracketed-host checks, so it accepts a few URLs Editarr rejects.
/// </summary>
internal static class McfMetadataUrl
{
    private static readonly char[] LeadingIgnored = Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray();

    /// <summary>Returns Editarr's message for an invalid URL, or null for a valid one.</summary>
    /// <param name="value">The metadata value.</param>
    /// <param name="imdb">Whether the field is <c>IMDB</c>.</param>
    /// <returns>The problem, or null.</returns>
    public static string? Problem(string value, bool imdb)
    {
        string url = value.TrimStart(LeadingIgnored).Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        string scheme = string.Empty;
        int colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && char.IsAsciiLetter(url[0]) && url.Take(colon).All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }

        string authority = string.Empty;
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            int end = url.IndexOfAny(['/', '?', '#'], 2);
            authority = end < 0 ? url[2..] : url[2..end];
            if (authority.Contains('[') != authority.Contains(']'))
            {
                return "MCF metadata URL is invalid";
            }
        }

        if (scheme is not ("http" or "https") || authority.Length == 0)
        {
            return "MCF metadata URL is invalid";
        }

        string? host = Hostname(authority);
        return imdb && host != "imdb.com" && !(host ?? string.Empty).EndsWith(".imdb.com", StringComparison.Ordinal)
            ? "MCF IMDb URL host is invalid"
            : null;
    }

    // SplitResult.hostname: after the last '@', inside brackets or before ':', lowercased up to any '%' zone.
    private static string? Hostname(string authority)
    {
        string hostInfo = authority[(authority.LastIndexOf('@') + 1)..];
        int open = hostInfo.IndexOf('[', StringComparison.Ordinal);
        string host = open >= 0 ? hostInfo[(open + 1)..] : hostInfo;
        int end = host.IndexOf(open >= 0 ? ']' : ':', StringComparison.Ordinal);
        host = end < 0 ? host : host[..end];
        if (host.Length == 0)
        {
            return null;
        }

        int zone = host.IndexOf('%', StringComparison.Ordinal);
        return zone < 0 ? AsciiLower(host) : AsciiLower(host[..zone]) + host[zone..];
    }

    // Python lowercases every letter, but only ASCII letters can make a host equal or end with ".imdb.com",
    // so lowering ASCII alone gives Python's verdict without depending on .NET's culture data.
    private static string AsciiLower(string value) => string.Create(value.Length, value, static (span, source) =>
    {
        for (int i = 0; i < source.Length; i++)
        {
            span[i] = char.IsAsciiLetterUpper(source[i]) ? (char)(source[i] | 0x20) : source[i];
        }
    });
}
