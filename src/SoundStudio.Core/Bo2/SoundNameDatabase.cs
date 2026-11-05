using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace SoundStudio.Core.Bo2;

/// <summary>
/// Maps sound ids (hashes) to their original asset paths, e.g.
/// <c>devraw\sound\vox\...\vox_us_death_00.SN55.pc.snd</c>. Loaded lazily from an embedded resource.
/// </summary>
public static class SoundNameDatabase
{
    private static readonly Lazy<Dictionary<uint, string>> Names = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Count => Names.Value.Count;

    public static string? TryGetPath(uint id) => Names.Value.TryGetValue(id, out var path) ? path : null;

    /// <summary>Forces the database to load (e.g. on a background thread at startup).</summary>
    public static void Preload() => _ = Names.Value;

    private static Dictionary<uint, string> Load()
    {
        var names = new Dictionary<uint, string>(400_000);
        using var resource = typeof(SoundNameDatabase).Assembly.GetManifestResourceStream("SoundStudio.Core.Names.deflate")
            ?? throw new InvalidOperationException("Embedded sound name database is missing.");
        using var deflate = new DeflateStream(resource, CompressionMode.Decompress);
        using var reader = XmlReader.Create(deflate, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "Entry")
                continue;
            var hash = reader.GetAttribute("Hash");
            var path = reader.GetAttribute("Path");
            if (hash != null && path != null &&
                uint.TryParse(hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                names[id] = path;
        }
        return names;
    }
}
