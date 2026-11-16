using System.Runtime.Versioning;
using Jellyfin.Plugin.Edl.Services;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Checks the plugin's path rules against the MU-01 export ID, media-name, and POSIX path vectors.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ExportLayoutTests
{
    public static TheoryData<string> MediaNames() => new(ExportLayoutFixture.Manifest.MediaNames.Select(n => n.Name));

    public static TheoryData<int> PosixPaths() =>
        new(Enumerable.Range(0, ExportLayoutFixture.Manifest.Paths.Count).Where(i => ExportLayoutFixture.Manifest.Paths[i].Style == "posix"));

    [Theory]
    [MemberData(nameof(MediaNames))]
    public void Media_stem_matches_the_contract(string name)
    {
        MediaName vector = ExportLayoutFixture.Manifest.MediaNames.Single(n => n.Name == name);

        Assert.Equal(vector.Stem, ExportLayout.MediaStem(name));
        Assert.Equal(vector.Error is null, vector.Stem is not null);
    }

    [Fact]
    public void Jellyfin_user_ids_become_their_canonical_export_ids()
    {
        IEnumerable<Principal> jellyfin = ExportLayoutFixture.Manifest.Principals.Values.Where(p => p.Kind == "jellyfin");

        Assert.Equal(2, jellyfin.Count());
        Assert.All(jellyfin, p => Assert.Equal(p.ExportId, ExportLayout.ExportId(Guid.Parse(p.ProviderInput!))));
    }

    [Theory]
    [MemberData(nameof(PosixPaths))]
    public void Paths_match_the_contract_and_never_name_the_legacy_file(int index)
    {
        LayoutPath vector = ExportLayoutFixture.Manifest.Paths[index];
        string exportId = ExportLayoutFixture.Manifest.Principals[vector.Principal].ExportId;
        string? personal = ExportLayout.PersonalPath(vector.Media, exportId, vector.Format);
        string? fallback = ExportLayout.DefaultPath(vector.Media, vector.Format);

        Assert.Equal(vector.Personal, personal);
        Assert.Equal(vector.Default, fallback);
        Assert.DoesNotContain(vector.Legacy, new[] { personal, fallback });
    }
}
