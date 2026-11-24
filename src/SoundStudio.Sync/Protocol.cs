using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Sync;

public static class SyncProtocol
{
    /// <summary>2 = state sync (manifests + audio on request). 1 was the change log of Sound Studio 1.0.x.</summary>
    public const int Version = 2;
    public const int DefaultPort = 5150;
    public const long MaxBlobBytes = 256L * 1024 * 1024;
    public const int MaxManifestSounds = 50_000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static bool IsHex(string? s, int length) => s is not null && s.Length == length && s.All(Uri.IsHexDigit);

    /// <summary>
    /// A bank name from someone else must be a plain bank file name (no folders, so "..\\x" can't point outside
    /// the sound folder).
    /// </summary>
    public static bool IsSafeBankName(string? bank) =>
        !string.IsNullOrWhiteSpace(bank) && bank.Length <= 128 && bank == Path.GetFileName(bank) &&
        bank.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        (bank.EndsWith(".sabs", StringComparison.OrdinalIgnoreCase) || bank.EndsWith(".sabl", StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks one entry of someone else's manifest before anything uses it. Null when valid.</summary>
    public static string? Validate(ManifestEntry e)
    {
        if (!IsSafeBankName(e.Bank))
            return "Invalid bank name.";
        if (e.Index < 0 || e.SampleCount < 0 || e.Channels is < 0 or > 8 || e.SampleRate < 0 || e.Size is < 0 or > MaxBlobBytes)
            return "Invalid sound details.";
        if (!IsHex(e.OriginalChecksum, 32))
            return "Invalid checksum.";
        if (!IsHex(e.Hash, 64))
            return "Invalid audio fingerprint.";
        if ((e.Source?.Length ?? 0) > 260 || (e.SoundName?.Length ?? 0) > 512)
            return "Text too long.";
        return null;
    }

    public static string? Validate(Manifest m)
    {
        if (string.IsNullOrEmpty(m.ClientId) || m.ClientId.Length > 64)
            return "Invalid client.";
        if (m.Sounds.Count > MaxManifestSounds)
            return "Too many sounds.";
        foreach (var e in m.Sounds)
        {
            if (Validate(e) is { } problem)
                return problem;
        }
        return null;
    }

    public static string? Validate(AudioRequest r)
    {
        if (string.IsNullOrEmpty(r.OwnerId) || r.OwnerId.Length > 64 || string.IsNullOrEmpty(r.RequesterId) || r.RequesterId.Length > 64)
            return "Invalid request.";
        if (!IsSafeBankName(r.Bank) || r.Index < 0 || !IsHex(r.Hash, 64))
            return "Invalid request.";
        return null;
    }

    /// <summary>A name from someone else, safe to show and to use in a file name.</summary>
    public static string CleanName(string? name, string fallback = "Someone")
    {
        var cleaned = new string((name ?? "").Where(ch => !char.IsControl(ch) && !Path.GetInvalidFileNameChars().Contains(ch)).ToArray())
            .Trim().TrimEnd('.').Trim();
        if (cleaned.Length > 40)
            cleaned = cleaned[..40].Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>Compares secrets without leaking how many characters matched.</summary>
    public static bool TokensEqual(string? a, string? b) =>
        a != null && b != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Identifies one sound in the game: bank file, table position and id.</summary>
public readonly record struct SoundKey(string Bank, int Index, uint Id)
{
    public override string ToString() => $"{Bank}#{Index}:{Id:X8}";
}

/// <summary>One modified sound as it is in someone's game right now.</summary>
public sealed record ManifestEntry
{
    public required string Bank { get; init; }
    public required int Index { get; init; }
    public required uint Id { get; init; }

    /// <summary>MD5 from the bank's checksum table: both sides must start from the same game sound.</summary>
    public required string OriginalChecksum { get; init; }

    /// <summary>SHA-256 of the sound's current, game-encoded audio. Equal hashes = identical sounds.</summary>
    public required string Hash { get; init; }

    public long Size { get; init; }
    public AudioFormat Format { get; init; }
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public int SampleCount { get; init; }

    /// <summary>Where it came from, for display ("my_voice.wav", "Volume -3.0 dB").</summary>
    public string? Source { get; init; }
    public string? SoundName { get; init; }

    /// <summary>The owner wants it hidden: hide where it came from and don't play it before it's applied.</summary>
    public bool Hidden { get; init; }

    [JsonIgnore] public SoundKey Key => new(Bank, Index, Id);
}

/// <summary>
/// What one install has changed: every modified sound and its fingerprint. Small (no audio), published to the
/// host whenever it changes. Sounds not listed are the original game audio.
/// </summary>
public sealed record Manifest
{
    public required string ClientId { get; init; }
    public required string Name { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public List<ManifestEntry> Sounds { get; init; } = [];
}

/// <summary>"Please upload this sound's audio": asked of the install that has it.</summary>
public sealed record AudioRequest
{
    public required string RequesterId { get; init; }
    public string? RequesterName { get; init; }
    public required string OwnerId { get; init; }
    public required string Bank { get; init; }
    public required int Index { get; init; }
    public required uint Id { get; init; }
    public required string Hash { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    [JsonIgnore] public SoundKey Key => new(Bank, Index, Id);
}

/// <summary>Unauthenticated hello: enough to recognise the right host, nothing secret.</summary>
public sealed record HostHello(string HostId, string HostName, int Protocol);

/// <summary>Authenticated host details: current addresses (they change, e.g. a new tunnel URL) and who's around.</summary>
public sealed record HostDetails(string HostId, string HostName, IReadOnlyList<string> Urls, IReadOnlyList<PeerInfo> Peers);

public sealed record PeerInfo(string ClientId, string Name, DateTime LastSeenUtc, bool Online);

/// <summary>
/// Everything needed to connect, as one copy-pasteable string:
/// <c>SS1.</c> + base64url(JSON { host id, host name, token, candidate URLs }).
/// </summary>
public sealed record InviteCode(string HostId, string HostName, string Token, IReadOnlyList<string> Urls)
{
    private const string Prefix = "SS1.";

    public override string ToString()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(HostId, HostName, Token, Urls.ToArray()));
        return Prefix + SyncProtocol.Base64Url(json);
    }

    public static bool TryParse(string? text, out InviteCode? code)
    {
        code = null;
        text = text?.Trim();
        if (text == null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(SyncProtocol.FromBase64Url(text[Prefix.Length..]));
            if (p == null || string.IsNullOrEmpty(p.H) || string.IsNullOrEmpty(p.T) || p.U is not { Length: > 0 })
                return false;
            code = new InviteCode(p.H, p.N ?? "Host", p.T, p.U);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(
        [property: JsonPropertyName("h")] string H,
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("u")] string[] U);
}
