using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Jellyfin.Plugin.Edl.Tests.Contract;

namespace Jellyfin.Plugin.Edl.Tests;

/// <summary>
/// MCF validity as a signed-in user sees it: each vector is the user's personal MCF beside a valid default.
/// A valid vector comes back byte for byte as <c>text/vtt</c>; an invalid one is 422 with Editarr's parser
/// message and line, and the default is never served instead. Expected results follow Editarr's
/// <c>scanner/formats/mcf_parser.py</c> except where a vector says the plugin differs.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class McfValidationTests : IDisposable
{
    private const string V10 = "WEBVTT MovieContentFilter 1.0.0\n\n";
    private const string V11 = "WEBVTT MovieContentFilter 1.1.0\n\n";
    private const string Bounds = "NOTE\nSTART 00:00:00.000\nEND 00:10:00.000\n\n";
    private const string Cue = "00:00:05.000 --> 00:00:08.000\nviolence=high=both\n";

    // Editarr tests/fixtures/mcf/valid-1.0.mcf and valid-1.1.mcf, pinned by SHA-256 below.
    private const string EditarrValid10 = "WEBVTT MovieContentFilter 1.0.0\n\nNOTE\nSTART 00:00:04.020\nEND 01:24:00.100\n\n"
        + "00:00:06.075 --> 00:00:10.500\nviolence=high\n\n00:06:14.000 --> 00:06:17.581\ngambling=medium # Some comment\ndrugs=high=video\n\n";
    private const string EditarrValid11 = "WEBVTT MovieContentFilter 1.1.0\n\nNOTE\nTITLE Ozymandias\nYEAR 2013\nTYPE episode\nSEASON 5\n"
        + "EPISODE 14\nSOURCE https://www.moviecontentfilter.com/works/7Cmr6B\nIMDB https://www.imdb.com/title/tt2301451/\n"
        + "RELEASE North America\nCOMMENT Quite an intense episode!\n\nNOTE\nSTART 00:00:04.020\nEND 01:24:00.100\n\n"
        + "00:00:06.075 --> 00:00:10.500\nviolence=high\n\n00:06:14.000 --> 00:06:17.581\ngambling=medium # Some comment\ndrugs=high=video\n\n";

    private static readonly Dictionary<string, (byte[] Content, int Line, string? Message)> Vectors = new()
    {
        ["editarr valid 1.0.0"] = Valid(EditarrValid10),
        ["editarr valid 1.1.0"] = Valid(EditarrValid11),
        ["no cues"] = Valid(V11 + Bounds),
        ["last cue without a closing blank line"] = Valid(V11 + Bounds + Cue),
        ["CRLF line endings"] = Valid((V11 + Bounds + Cue + "\n").Replace("\n", "\r\n", StringComparison.Ordinal)),
        ["1.0.0 two-part payload means both channels"] = Valid(V10 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high\n"),
        ["empty 1.1.0 metadata block"] = Valid(V11 + "NOTE\n\n" + Bounds + Cue),
        ["some metadata fields in order"] = Valid(V11 + "NOTE\nTITLE Café\nCOMMENT x\n\n" + Bounds + Cue),
        ["comment containing the marker"] = Valid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\ndrugs=low=audio # a # b\n"),
        ["three-digit hours"] = Valid(V11 + "NOTE\nSTART 00:00:00.000\nEND 100:00:00.000\n\n"),
        ["year at the 64-bit limit"] = Valid(V11 + "NOTE\nYEAR 9223372036854775807\n\n" + Bounds),
        ["year zero"] = Valid(V11 + "NOTE\nYEAR 0\n\n" + Bounds),
        ["URL scheme and IMDb host in any case, with user and port"] =
            Valid(V11 + "NOTE\nSOURCE HTTPS://example.com\nIMDB http://user@M.IMDB.COM:80/x\n\n" + Bounds),
        ["IMDb host imdb.com"] = Valid(V11 + "NOTE\nIMDB https://imdb.com\n\n" + Bounds),
        ["control characters urlsplit removes"] = Valid(V11 + "NOTE\nIMDB \u0001https://imdb.\tcom/\n\n" + Bounds),
        ["line of 4096 code points"] = Valid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high # " + new string('x', 4080) + "\n"),
        ["line of 4096 code points with astral characters"] =
            Valid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high # " + string.Concat(Enumerable.Repeat("😀", 4080)) + "\n"),
        ["10000 payload entries"] = Valid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\n" + string.Concat(Enumerable.Repeat("fear=low\n", 10_000))),

        ["empty file"] = Invalid(string.Empty, 1, "MCF header is required"),
        ["byte order mark"] = Invalid("\uFEFF" + V11 + Bounds, 1, "MCF content must not contain a BOM"),
        ["invalid UTF-8"] = Invalid([.. Encoding.UTF8.GetBytes(V11 + Bounds), 0xFF, 0x0A], 0, "MCF content must be valid UTF-8"),
        ["UTF-8 encoded surrogate"] = Invalid([.. Encoding.UTF8.GetBytes(V11 + "NOTE\nTITLE "), 0xED, 0xA0, 0x80, 0x0A, 0x0A, .. Encoding.UTF8.GetBytes(Bounds)], 0, "MCF content must be valid UTF-8"),
        ["lone carriage return"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\rviolence=high\n", 0, "MCF content contains an invalid line terminator"),
        ["no final line terminator"] = Invalid(V11 + Bounds + Cue.TrimEnd('\n'), 0, "MCF content must end with a line terminator"),
        ["header missing patch version"] = Invalid("WEBVTT MovieContentFilter 1.1\n\n" + Bounds, 1, "MCF header is invalid"),
        ["header with trailing space"] = Invalid("WEBVTT MovieContentFilter 1.1.0 \n\n" + Bounds, 1, "MCF header is invalid"),
        ["unsupported version 1.2.0"] = Invalid("WEBVTT MovieContentFilter 1.2.0\n\n" + Bounds, 1, "MCF version is not supported"),
        ["no blank line after the header"] = Invalid("WEBVTT MovieContentFilter 1.1.0\n" + Bounds, 2, "MCF header must be followed by a blank line"),
        ["metadata in a 1.0.0 document"] = Invalid(V10 + "NOTE\nTITLE x\n\n" + Bounds, 4, "MCF material bounds section is invalid"),
        ["NOTE block followed by another NOTE is metadata"] = Invalid(V11 + "NOTE\nfoo\n\n" + Bounds, 4, "MCF metadata field is invalid"),
        ["metadata out of order"] = Invalid(V11 + "NOTE\nYEAR 2013\nTITLE x\n\n" + Bounds, 5, "MCF metadata fields are out of order"),
        ["metadata field repeated"] = Invalid(V11 + "NOTE\nTITLE x\nTITLE y\n\n" + Bounds, 5, "MCF metadata fields are out of order"),
        ["unknown metadata field"] = Invalid(V11 + "NOTE\nTITLE x\nGENRE drama\n\n" + Bounds, 5, "MCF metadata field is invalid"),
        ["empty metadata value"] = Invalid(V11 + "NOTE\nTITLE x\nCOMMENT \n\n" + Bounds, 5, "MCF metadata field is invalid"),
        ["lowercase metadata field"] = Invalid(V11 + "NOTE\nTITLE x\nyear 2013\n\n" + Bounds, 5, "MCF metadata field is invalid"),
        ["metadata value with a trailing space"] = Invalid(V11 + "NOTE\nTITLE x \n\n" + Bounds, 4, "MCF metadata value is invalid"),
        ["metadata value ending in U+001F, Python whitespace"] = Invalid(V11 + "NOTE\nTITLE x\u001f\n\n" + Bounds, 4, "MCF metadata value is invalid"),
        ["metadata value starting with U+3000"] = Invalid(V11 + "NOTE\nTITLE \u3000x\n\n" + Bounds, 4, "MCF metadata value is invalid"),
        ["negative year"] = Invalid(V11 + "NOTE\nYEAR -1\n\n" + Bounds, 4, "MCF year must be a non-negative integer"),
        ["year with a leading zero"] = Invalid(V11 + "NOTE\nYEAR 02013\n\n" + Bounds, 4, "MCF year must be a non-negative integer"),
        ["year past the 64-bit limit"] = Invalid(V11 + "NOTE\nYEAR 9223372036854775808\n\n" + Bounds, 4, "MCF year is too large"),
        ["season zero"] = Invalid(V11 + "NOTE\nSEASON 0\n\n" + Bounds, 4, "MCF season and episode must be positive integers"),
        ["episode past the 64-bit limit"] = Invalid(V11 + "NOTE\nEPISODE 9223372036854775808\n\n" + Bounds, 4, "MCF season or episode is too large"),
        ["unknown work type"] = Invalid(V11 + "NOTE\nTYPE film\n\n" + Bounds, 4, "MCF work type is invalid"),
        ["source URL with an ftp scheme"] = Invalid(V11 + "NOTE\nSOURCE ftp://example.com/\n\n" + Bounds, 4, "MCF metadata URL is invalid"),
        ["source URL without an authority"] = Invalid(V11 + "NOTE\nSOURCE https:///works/1\n\n" + Bounds, 4, "MCF metadata URL is invalid"),
        ["source URL with an unclosed bracket"] = Invalid(V11 + "NOTE\nSOURCE https://[::1/\n\n" + Bounds, 4, "MCF metadata URL is invalid"),
        ["IMDb URL on another host"] = Invalid(V11 + "NOTE\nIMDB https://notimdb.com/title/tt1/\n\n" + Bounds, 4, "MCF IMDb URL host is invalid"),
        ["IMDb URL on a host ending in imdb.com.evil"] = Invalid(V11 + "NOTE\nIMDB https://imdb.com.evil/\n\n" + Bounds, 4, "MCF IMDb URL host is invalid"),
        ["IMDb host with a dotted capital I"] = Invalid(V11 + "NOTE\nIMDB https://\u0130MDB.com/\n\n" + Bounds, 4, "MCF IMDb URL host is invalid"),
        ["metadata without a closing blank line"] = Invalid(V11 + "NOTE\nTITLE x\n", 4, "MCF metadata must end with a blank line"),
        ["material bounds incomplete"] = Invalid(V11 + "NOTE\nSTART 00:00:00.000\nEND 00:10:00.000\n", 3, "MCF material bounds are incomplete"),
        ["material bounds missing"] = Invalid(V11 + Cue + "\n" + Cue, 3, "MCF material bounds section is invalid"),
        ["material end before start"] = Invalid(V11 + "NOTE\nSTART 00:10:00.000\nEND 00:00:00.000\n\n", 5, "MCF material bounds are invalid"),
        ["one-digit hours"] = Invalid(V11 + "NOTE\nSTART 0:00:00.000\nEND 00:10:00.000\n\n", 4, "MCF timestamp is invalid"),
        ["sixty minutes"] = Invalid(V11 + "NOTE\nSTART 00:60:00.000\nEND 01:10:00.000\n\n", 4, "MCF timestamp is invalid"),
        ["hours too large for a double"] = Invalid(V11 + "NOTE\nSTART 00:00:00.000\nEND " + new string('9', 400) + ":00:00.000\n\n", 5, "MCF timestamp is not finite"),
        ["Arabic-Indic hour digits"] = Invalid(V11 + "NOTE\nSTART ٠٠:00:00.000\nEND 00:10:00.000\n\n", 4, "MCF timestamp is invalid"),
        ["fullwidth millisecond digits"] = Invalid(V11 + "NOTE\nSTART 00:00:00.０００\nEND 00:10:00.000\n\n", 4, "MCF timestamp is invalid"),
        ["mathematical bold cue hour digits"] = Invalid(V11 + Bounds + "𝟎𝟎:00:05.000 --> 00:00:08.000\nviolence=high\n", 7, "MCF cue timing is invalid"),
        ["Arabic-Indic cue hour digits"] = Invalid(V11 + Bounds + "٠٠:00:05.000 --> 00:00:08.000\nviolence=high\n", 7, "MCF cue timing is invalid"),
        ["Arabic-Indic year digit"] = Invalid(V11 + "NOTE\nYEAR 1٢\n\n" + Bounds, 4, "MCF year must be a non-negative integer"),
        ["Devanagari season digit"] = Invalid(V11 + "NOTE\nSEASON 1०\n\n" + Bounds, 4, "MCF season and episode must be positive integers"),
        ["Arabic-Indic header version"] = Invalid("WEBVTT MovieContentFilter ١.١.٠\n\n" + Bounds, 1, "MCF header is invalid"),
        ["cue timing without spaces"] = Invalid(V11 + Bounds + "00:00:05.000-->00:00:08.000\nviolence=high\n", 7, "MCF cue timing is invalid"),
        ["two blank lines between cues"] = Invalid(V11 + Bounds + Cue + "\n\n" + Cue, 10, "MCF cue timing is invalid"),
        ["cue with equal start and end"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:05.000\nviolence=high\n", 7, "MCF cue range is invalid"),
        ["cue without payload"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\n\n" + Cue, 7, "MCF cue must contain at least one payload entry"),
        ["category in the wrong case"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nViolence=high\n", 8, "MCF cue payload token is invalid"),
        ["unknown severity"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=extreme\n", 8, "MCF cue payload token is invalid"),
        ["unknown channel"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high=sides\n", 8, "MCF cue payload token is invalid"),
        ["four payload parts"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high=both=x\n", 8, "MCF cue payload is invalid"),
        ["empty payload part"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=\n", 8, "MCF cue payload is invalid"),
        ["empty comment"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high # \n", 8, "MCF cue comment is invalid"),
        ["comment with a cue arrow"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high # a --> b\n", 8, "MCF cue comment is invalid"),
        ["line of 4097 code points"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\nviolence=high # " + new string('x', 4081) + "\n", 8, "MCF line exceeds the size limit"),
        ["more than 1 MiB"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\n" + string.Concat(Enumerable.Repeat("violence=high # " + new string('x', 1000) + "\n", 1100)), 0, "MCF content exceeds the size limit"),
        ["10001 payload entries"] = Invalid(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\n" + string.Concat(Enumerable.Repeat("fear=low\n", 10_001)), 10_008, "MCF payload-entry limit exceeded"),
    };

    private readonly TempDirectory _directory = new();
    private readonly PluginHarness _plugin = new();
    private readonly User _user = PluginHarness.NewUser(Guid.Parse("5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d"));

    public static TheoryData<string> ValidVectors() => new(Vectors.Where(v => v.Value.Message is null).Select(v => v.Key));

    public static TheoryData<string> InvalidVectors() => new(Vectors.Where(v => v.Value.Message is not null).Select(v => v.Key));

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Embedded_editarr_goldens_match_their_fixture_files()
    {
        Assert.Equal("8e888bb8d301c13876dba9a341fd0d5748686d1fce4c8b609ccf5e0340a5e672", Sha256(EditarrValid10));
        Assert.Equal("f5dbffa04d8d38eaf159dd7b81abe25c392372ca95441d3a368a125f220f8a5d", Sha256(EditarrValid11));
    }

    [Theory]
    [MemberData(nameof(ValidVectors))]
    public async Task Valid_personal_mcf_is_served_byte_for_byte(string name)
    {
        byte[] content = Vectors[name].Content;
        Guid item = AddMovie(content);

        PluginResponse response = await _plugin.GetAsync(_user, item, format: "mcf");

        Assert.Equal(200, response.Status);
        Assert.Equal("text/vtt; charset=utf-8", response.Headers.ContentType.ToString());
        Assert.Equal(content, response.Body);
    }

    [Theory]
    [MemberData(nameof(InvalidVectors))]
    public async Task Invalid_personal_mcf_is_reported_and_never_replaced_by_the_default(string name)
    {
        (byte[] content, int line, string? message) = Vectors[name];
        Guid item = AddMovie(content);

        PluginResponse response = await _plugin.GetAsync(_user, item, format: "mcf");

        Assert.Equal(422, response.Status);
        ExportValidationErrorResponse error = JsonSerializer.Deserialize<ExportValidationErrorResponse>(
            response.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(new ExportValidationDiagnostic(line, message!), Assert.Single(error.Diagnostics));
    }

    [Fact]
    public void Every_registry_category_is_accepted_and_the_set_is_editarr_registry_v1()
    {
        string tokens = string.Concat(McfCategories.Tokens.Order(StringComparer.Ordinal).Select(token => token + "\n"));
        string payloads = string.Concat(McfCategories.Tokens.Select(token => token + "=medium\n"));

        Assert.Equal(97, McfCategories.Tokens.Count);
        Assert.Equal("02dae6e6303518e5e7299312648cca6f1dd80b78b95d1c3e998e737c0989b4e5", Sha256(tokens));
        Assert.True(new McfExportFormat().Validate(Encoding.UTF8.GetBytes(V11 + Bounds + "00:00:05.000 --> 00:00:08.000\n" + payloads)).IsValid);
    }

    [Theory]
    [InlineData("mcf-personal-a")]
    [InlineData("mcf-personal-b")]
    [InlineData("mcf-default")]
    [InlineData("mcf-legacy")]
    [InlineData("mcf-local")]
    [InlineData("mcf-malformed")]
    public void Fixture_payload_validity_matches_the_manifest(string payload)
    {
        Assert.Equal(
            ExportLayoutFixture.Manifest.Payloads[payload].Valid,
            new McfExportFormat().Validate(ExportLayoutFixture.PayloadBytes(payload)).IsValid);
    }

    private static (byte[], int, string?) Valid(string content) => (Encoding.UTF8.GetBytes(content), 0, null);

    private static (byte[], int, string?) Invalid(string content, int line, string message) => (Encoding.UTF8.GetBytes(content), line, message);

    private static (byte[], int, string?) Invalid(byte[] content, int line, string message) => (content, line, message);

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private Guid AddMovie(byte[] personal)
    {
        string mediaPath = Path.Combine(_directory.Path, "Movie.mkv");
        Write($"mcf/{_user.Id:N}/Movie.user-{_user.Id:N}.mcf", personal);
        Write("mcf/Movie.mcf", ExportLayoutFixture.PayloadBytes("mcf-default"));
        return _plugin.AddVideo(new[] { _user }, PluginHarness.Source(Guid.NewGuid(), mediaPath));
    }

    private void Write(string relativePath, byte[] content)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
