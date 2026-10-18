using Jellyfin.Plugin.Edl.Services;

namespace Jellyfin.Plugin.Edl.Tests;

public sealed class EdlParserTests
{
    private readonly EdlParser _parser = new();

    [Fact]
    public void Validate_ReturnsValid_ForSupportedVariantsAndComments()
    {
        string[] lines =
        {
            "# comment",
            "",
            "10.5 25.0 cut",
            "30 35 0",
            "; another comment",
            "// one more",
            "40 50 commercial"
        };

        var result = _parser.Validate(lines);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Validate_ReturnsInvalidDiagnostics_ForMalformedEntries()
    {
        string[] lines =
        {
            "one two cut",
            "10 9 mute",
            "10 20 unknown",
            "10"
        };

        var result = _parser.Validate(lines);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, d => d.LineNumber == 1 && d.Message.Contains("Invalid start time", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.LineNumber == 2 && d.Message.Contains("End time must be greater", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.LineNumber == 3 && d.Message.Contains("Unsupported action", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.LineNumber == 4 && d.Message.Contains("Expected at least three fields", StringComparison.Ordinal));
    }
}
