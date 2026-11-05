namespace SoundStudio.Core.Library;

public enum BankCategory { Zombies, Multiplayer, Common, Weapons, Other }

/// <summary>What a bank file name like <c>zmb_tomb_load.english.sabs</c> tells us.</summary>
public sealed record BankDescription(
    BankCategory Category,
    string MapKey,      // "tomb"
    string MapName,     // "Origins"
    string Part,        // "" or "Loading screen", "Intro", ...
    string Language,    // "all", "english"
    bool IsStreamed)    // .sabs = streamed from disk, .sabl = loaded into memory
{
    public string KindLabel => IsStreamed ? "Streamed" : "Loaded";

    /// <summary>Short label for a bank under its map, e.g. "English · Streamed" or "Loading screen · All · Streamed".</summary>
    public string ShortLabel
    {
        get
        {
            var lang = Language == "all" ? "All languages" : char.ToUpperInvariant(Language[0]) + Language[1..];
            return Part.Length > 0 ? $"{Part} · {lang} · {KindLabel}" : $"{lang} · {KindLabel}";
        }
    }
}

public static class BankNaming
{
    // Internal map name -> in-game name. Only names we're confident about; others are prettified.
    private static readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Zombies
        ["tomb"] = "Origins",
        ["buried"] = "Buried",
        ["alcatraz"] = "Mob of the Dead",
        ["highrise"] = "Die Rise",
        ["nuked"] = "Nuketown Zombies",
        ["nuked_real"] = "Nuketown Zombies",
        ["classic_transit"] = "TranZit",
        ["survival_transit"] = "TranZit Survival",
        ["rt"] = "Returned Tranzit", // zmb_rt_load = loading screen of returned_tranzit
        // Multiplayer
        ["la"] = "Aftermath",
        ["carrier"] = "Carrier",
        ["express"] = "Express",
        ["hijacked"] = "Hijacked",
        ["meltdown"] = "Meltdown",
        ["nightclub"] = "Plaza",
        ["raid"] = "Raid",
        ["slums"] = "Slums",
        ["village"] = "Standoff",
        ["turbine"] = "Turbine",
        ["nuketown_2020"] = "Nuketown 2025",
        ["concert"] = "Encore",
        ["magma"] = "Magma",
        ["vertigo"] = "Vertigo",
        ["studio"] = "Studio",
        ["castaway"] = "Cove",
        ["bridge"] = "Detour",
        ["paintball"] = "Rush",
        ["uplink"] = "Uplink",
        ["downhill"] = "Downhill",
        ["skate"] = "Grind",
        ["hydro"] = "Hydro",
        ["mirage"] = "Mirage",
        ["pod"] = "Pod",
        ["takeoff"] = "Takeoff",
        ["frostbite"] = "Frost",
        ["dig"] = "Dig",
        // Shared
        ["root"] = "Core sounds",
        ["common"] = "Shared sounds",
        ["code_post_gfx"] = "Announcer & UI",
        ["patch"] = "Patch",
        ["patch_ui"] = "Patch UI",
        ["vox_multi_team"] = "Team voices",
    };

    private static readonly (string Suffix, string Label)[] Parts =
    [
        ("_load", "Loading screen"),
        ("_intro", "Intro"),
    ];

    public static BankDescription Describe(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var isStreamed = name.EndsWith(".sabs", StringComparison.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(name);               // zmb_tomb_load.english
        var dot = stem.LastIndexOf('.');
        var language = dot >= 0 ? stem[(dot + 1)..].ToLowerInvariant() : "all";
        var baseName = dot >= 0 ? stem[..dot] : stem;                   // zmb_tomb_load

        var underscore = baseName.IndexOf('_');
        var prefix = underscore > 0 ? baseName[..underscore] : "";
        var category = prefix.ToLowerInvariant() switch
        {
            "zmb" => BankCategory.Zombies,
            "mpl" => BankCategory.Multiplayer,
            "cmn" => BankCategory.Common,
            "wpn" => BankCategory.Weapons,
            _ => BankCategory.Other,
        };
        var key = underscore > 0 && category != BankCategory.Other ? baseName[(underscore + 1)..] : baseName;

        var part = "";
        foreach (var (suffix, label) in Parts)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && key.Length > suffix.Length)
            {
                key = key[..^suffix.Length];
                part = label;
                break;
            }
        }

        var mapName = Maps.TryGetValue(key, out var friendly) ? friendly : Prettify(key);
        return new BankDescription(category, key.ToLowerInvariant(), mapName, part, language, isStreamed);
    }

    public static string CategoryName(BankCategory category) => category switch
    {
        BankCategory.Zombies => "Zombies",
        BankCategory.Multiplayer => "Multiplayer",
        BankCategory.Common => "Common",
        BankCategory.Weapons => "Weapons",
        _ => "Other",
    };

    /// <summary>"returned_tranzit" -> "Returned Tranzit".</summary>
    public static string Prettify(string key) =>
        string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
