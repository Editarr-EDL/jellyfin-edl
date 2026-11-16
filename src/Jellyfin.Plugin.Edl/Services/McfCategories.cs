using System.Collections.Frozen;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// The 97 category tokens an MCF cue payload may name, grouped by family. Copied from Editarr's
/// <c>tests/fixtures/mcf/category-registry-v1.json</c> (SHA-256 230c2588…14b873), which matches
/// Editarr's <c>MCF_CATEGORY_TO_FAMILY</c>. Tokens are case-sensitive.
/// </summary>
internal static class McfCategories
{
    public static readonly FrozenSet<string> Tokens = new[]
    {
        "commercial", "advertBreak", "consumerism", "productPlacement",
        "discrimination", "adultism", "antisemitism", "genderism", "homophobia", "misandry", "misogyny", "racism",
        "sexism", "supremacism", "transphobia", "xenophobia",
        "dispensable", "idiocy", "tedious",
        "drugs", "alcohol", "antipsychotics", "cigarettes", "depressants", "gambling", "hallucinogens", "stimulants",
        "fear", "accident", "acrophobia", "aliens", "arachnophobia", "astraphobia", "aviophobia", "chemophobia",
        "claustrophobia", "coulrophobia", "cynophobia", "death", "dentophobia", "emetophobia", "enochlophobia",
        "explosion", "fire", "gerascophobia", "ghosts", "grave", "hemophobia", "hylophobia", "melissophobia",
        "misophonia", "musophobia", "mysophobia", "nosocomephobia", "nyctophobia", "siderodromophobia",
        "thalassophobia", "vampires",
        "language", "blasphemy", "nameCalling", "sexualDialogue", "swearing", "vulgarity",
        "nudity", "bareButtocks", "exposedGenitalia", "fullNudity", "toplessness",
        "sex", "adultery", "analSex", "coitus", "kissing", "masturbation", "objectification", "oralSex",
        "premaritalSex", "promiscuity", "prostitution",
        "violence", "choking", "crueltyToAnimals", "culturalViolence", "desecration", "emotionalViolence", "kicking",
        "massacre", "murder", "punching", "rape", "slapping", "slavery", "stabbing", "torture", "warfare", "weapons",
    }.ToFrozenSet(StringComparer.Ordinal);
}
