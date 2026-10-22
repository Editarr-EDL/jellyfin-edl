using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// Validates a MovieContentFilter 1.0.0 or 1.1.0 document by the rules of Editarr's strict parser,
/// <c>scanner/formats/mcf_parser.py</c> without a media duration, and reports the first problem with that
/// parser's message and 1-based line number (0 when the problem has no line). The README lists where the
/// plugin differs: it skips urlsplit's NFKC and bracketed-host checks.
/// </summary>
internal static partial class McfValidator
{
    public const int MaxContentBytes = 1_048_576;
    public const int MaxLineCodePoints = 4_096;
    public const int MaxPayloadEntries = 10_000;

    private static readonly string[] MetadataFields = ["TITLE", "YEAR", "TYPE", "SEASON", "EPISODE", "SOURCE", "IMDB", "RELEASE", "COMMENT"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Validates a document's exact bytes.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <returns>Valid, or invalid with one diagnostic.</returns>
    public static ExportValidationResult Validate(byte[] content)
    {
        try
        {
            string[] lines = Lines(content);
            if (lines.Length == 0)
            {
                throw new Invalid(1, "MCF header is required");
            }

            Match header = Header().Match(lines[0]);
            if (!header.Success)
            {
                throw new Invalid(1, "MCF header is invalid");
            }

            string version = header.Groups[1].Value;
            if (version is not ("1.0.0" or "1.1.0"))
            {
                throw new Invalid(1, "MCF version is not supported");
            }

            if (lines.Length < 2 || lines[1].Length != 0)
            {
                throw new Invalid(2, "MCF header must be followed by a blank line");
            }

            int cursor = version == "1.1.0" && StartsMetadata(lines, 2) ? Metadata(lines, 2) : 2;
            Cues(lines, MaterialBounds(lines, cursor));
            return ExportValidationResult.Valid;
        }
        catch (Invalid invalid)
        {
            return ExportValidationResult.Invalid([new ExportValidationDiagnostic(invalid.LineNumber, invalid.Message)]);
        }
    }

    private static string[] Lines(byte[] content)
    {
        if (content.Length > MaxContentBytes)
        {
            throw new Invalid(0, "MCF content exceeds the size limit");
        }

        if (content.AsSpan().StartsWith("\uFEFF"u8))
        {
            throw new Invalid(1, "MCF content must not contain a BOM");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new Invalid(0, "MCF content must be valid UTF-8");
        }

        if (text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\r'))
        {
            throw new Invalid(0, "MCF content contains an invalid line terminator");
        }

        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            throw new Invalid(0, "MCF content must end with a line terminator");
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[..^1];
        for (int i = 0; i < lines.Length; i++)
        {
            // Strict decoding leaves only paired surrogates, so each high surrogate is one extra UTF-16 unit.
            if (lines[i].Length - lines[i].Count(char.IsHighSurrogate) > MaxLineCodePoints)
            {
                throw new Invalid(i + 1, "MCF line exceeds the size limit");
            }
        }

        return lines;
    }

    // A 1.1.0 NOTE block is metadata when its first line is blank or a field, or when another NOTE follows it.
    private static bool StartsMetadata(string[] lines, int cursor)
    {
        if (cursor + 1 >= lines.Length || lines[cursor] != "NOTE")
        {
            return false;
        }

        string first = lines[cursor + 1];
        if (first.Length == 0 || MetadataFields.Any(field => first.StartsWith(field + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        int separator = cursor + 1;
        while (separator < lines.Length && lines[separator].Length != 0)
        {
            separator++;
        }

        return separator + 1 < lines.Length && lines[separator + 1] == "NOTE";
    }

    private static int Metadata(string[] lines, int cursor)
    {
        int last = -1;
        for (cursor++; cursor < lines.Length && lines[cursor].Length != 0; cursor++)
        {
            string line = lines[cursor];
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            int position = space < 0 ? -1 : Array.IndexOf(MetadataFields, line[..space]);
            if (position < 0 || space == line.Length - 1)
            {
                throw new Invalid(cursor + 1, "MCF metadata field is invalid");
            }

            if (position <= last)
            {
                throw new Invalid(cursor + 1, "MCF metadata fields are out of order");
            }

            last = position;
            MetadataValue(MetadataFields[position], line[(space + 1)..], cursor + 1);
        }

        if (cursor >= lines.Length)
        {
            throw new Invalid(lines.Length, "MCF metadata must end with a blank line");
        }

        return cursor + 1;
    }

    private static void MetadataValue(string field, string value, int line)
    {
        if (IsPythonSpace(value[0]) || IsPythonSpace(value[^1]))
        {
            throw new Invalid(line, "MCF metadata value is invalid");
        }

        switch (field)
        {
            case "YEAR" when !NonNegativeInteger().IsMatch(value):
                throw new Invalid(line, "MCF year must be a non-negative integer");
            case "YEAR" when !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _):
                throw new Invalid(line, "MCF year is too large");
            case "SEASON" or "EPISODE" when !PositiveInteger().IsMatch(value):
                throw new Invalid(line, "MCF season and episode must be positive integers");
            case "SEASON" or "EPISODE" when !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _):
                throw new Invalid(line, "MCF season or episode is too large");
            case "TYPE" when value is not ("movie" or "series" or "episode"):
                throw new Invalid(line, "MCF work type is invalid");
            case "SOURCE" or "IMDB" when McfMetadataUrl.Problem(value, imdb: field == "IMDB") is { } problem:
                throw new Invalid(line, problem);
        }
    }

    private static int MaterialBounds(string[] lines, int cursor)
    {
        if (cursor + 3 >= lines.Length)
        {
            throw new Invalid(cursor + 1, "MCF material bounds are incomplete");
        }

        string[] expected = ["NOTE", "START ", "END ", string.Empty];
        for (int offset = 0; offset < expected.Length; offset++)
        {
            string line = lines[cursor + offset];
            if (offset is 0 or 3 ? line != expected[offset] : !line.StartsWith(expected[offset], StringComparison.Ordinal))
            {
                throw new Invalid(cursor + offset + 1, "MCF material bounds section is invalid");
            }
        }

        double start = Seconds(lines[cursor + 1][6..], cursor + 2);
        double end = Seconds(lines[cursor + 2][4..], cursor + 3);
        if (!IsRange(start, end))
        {
            throw new Invalid(cursor + 3, "MCF material bounds are invalid");
        }

        return cursor + 4;
    }

    private static void Cues(string[] lines, int cursor)
    {
        int entries = 0;
        while (cursor < lines.Length)
        {
            int timingLine = cursor + 1;
            Match timing = Timing().Match(lines[cursor]);
            if (!timing.Success)
            {
                throw new Invalid(timingLine, "MCF cue timing is invalid");
            }

            if (!IsRange(Seconds(timing.Groups[1].Value, timingLine), Seconds(timing.Groups[2].Value, timingLine)))
            {
                throw new Invalid(timingLine, "MCF cue range is invalid");
            }

            int payloads = 0;
            for (cursor++; cursor < lines.Length && lines[cursor].Length != 0; cursor++, payloads++, entries++)
            {
                if (entries >= MaxPayloadEntries)
                {
                    throw new Invalid(cursor + 1, "MCF payload-entry limit exceeded");
                }

                Payload(lines[cursor], cursor + 1);
            }

            if (payloads == 0)
            {
                throw new Invalid(timingLine, "MCF cue must contain at least one payload entry");
            }

            cursor++;
        }
    }

    private static void Payload(string line, int lineNumber)
    {
        int marker = line.IndexOf(" # ", StringComparison.Ordinal);
        if (marker >= 0 && (marker + 3 == line.Length || line.IndexOf("-->", marker + 3, StringComparison.Ordinal) >= 0))
        {
            throw new Invalid(lineNumber, "MCF cue comment is invalid");
        }

        string[] parts = (marker < 0 ? line : line[..marker]).Split('=');
        if (parts.Length is not (2 or 3) || parts.Any(part => part.Length == 0))
        {
            throw new Invalid(lineNumber, "MCF cue payload is invalid");
        }

        if (!McfCategories.Tokens.Contains(parts[0]) || parts[1] is not ("low" or "medium" or "high")
            || (parts.Length == 3 && parts[2] is not ("both" or "video" or "audio")))
        {
            throw new Invalid(lineNumber, "MCF cue payload token is invalid");
        }
    }

    // Matches Python's float(hours) * 3600 + minutes * 60 + seconds + milliseconds / 1000 bit for bit.
    private static double Seconds(string value, int line)
    {
        Match match = Timestamp().Match(value);
        if (!match.Success)
        {
            throw new Invalid(line, "MCF timestamp is invalid");
        }

        double hours = double.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        double result = (hours * 3600) + (Part(match, 2) * 60) + Part(match, 3) + (Part(match, 4) / 1000.0);
        return double.IsFinite(result) ? result : throw new Invalid(line, "MCF timestamp is not finite");
    }

    private static int Part(Match match, int group) => int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool IsRange(double start, double end) => start >= 0 && start < end;

    // Python's str.isspace, which unlike char.IsWhiteSpace includes the separators U+001C to U+001F.
    private static bool IsPythonSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    [GeneratedRegex(@"\AWEBVTT MovieContentFilter ([0-9]+\.[0-9]+\.[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"\A([0-9]{2,}):([0-5][0-9]):([0-5][0-9])\.([0-9]{3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\A([0-9]{2,}:[0-5][0-9]:[0-5][0-9]\.[0-9]{3}) --> ([0-9]{2,}:[0-5][0-9]:[0-5][0-9]\.[0-9]{3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();

    [GeneratedRegex(@"\A(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NonNegativeInteger();

    [GeneratedRegex(@"\A[1-9][0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveInteger();

    /// <summary>The first problem found, with its 1-based line number or 0.</summary>
    private sealed class Invalid(int lineNumber, string message) : Exception(message)
    {
        public int LineNumber { get; } = lineNumber;
    }
}
