namespace SoundStudio.Core.Library;

/// <summary>What a sound is, worked out from its original asset path.</summary>
public sealed record SoundType(string Group, string Subgroup)
{
    public override string ToString() => Subgroup.Length > 0 ? $"{Group} · {Subgroup}" : Group;
}

/// <summary>
/// Classifies sounds by the folder they came from in the game's source tree, e.g.
/// <c>raw\sound\evt\zombie_global\hellhounds\spawn\spn_flux_r.LN55.pc.snd</c> → Events · zombie_global.
/// Voice lines are grouped by their script folder (map/mode), e.g. Voices · zmb_tomb.
/// </summary>
public static class SoundTypes
{
    public static readonly SoundType Unknown = new("Unnamed", "");

    private static readonly Dictionary<string, string> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vox"] = "Voices",
        ["mus"] = "Music",
        ["wpn"] = "Weapons",
        ["prj"] = "Bullets & projectiles",
        ["exp"] = "Explosions",
        ["amb"] = "Ambience",
        ["raven"] = "Ambience",
        ["zmb"] = "Zombies (creatures & level)",
        ["zmb_blops"] = "Zombies (creatures & level)",
        ["evt"] = "Events & effects",
        ["fly"] = "Footsteps & movement",
        ["chr"] = "Footsteps & movement",
        ["phy"] = "Physics & impacts",
        ["dst"] = "Physics & impacts",
        ["veh"] = "Vehicles",
        ["mpl"] = "Multiplayer events",
        ["uin"] = "Menus & UI",
        ["ui"] = "Menus & UI",
        ["aml"] = "Animals",
        ["bik"] = "Cinematics",
    };

    public static SoundType Describe(string? assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return Unknown;
        var parts = assetPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var sound = Array.FindIndex(parts, p => p.Equals("sound", StringComparison.OrdinalIgnoreCase));
        if (sound < 0 || sound + 1 >= parts.Length - 1)
            return new SoundType("Other", "");

        var top = parts[sound + 1].ToLowerInvariant();
        var group = Groups.TryGetValue(top, out var g) ? g : BankNaming.Prettify(top);
        var folders = parts[(sound + 2)..^1]; // between the type folder and the file name

        // Voices: "vox\scripted\<map or mode>\..." is the useful split; "vox\dds\..." is combat chatter.
        string sub;
        if (top == "vox" && folders.Length >= 2 && folders[0].Equals("scripted", StringComparison.OrdinalIgnoreCase))
            sub = folders[1];
        else if (top == "vox" && folders.Length >= 1 && folders[0].Equals("dds", StringComparison.OrdinalIgnoreCase))
            sub = "combat chatter";
        else
            sub = folders.Length > 0 ? folders[0] : "";
        return new SoundType(group, sub.ToLowerInvariant());
    }
}
