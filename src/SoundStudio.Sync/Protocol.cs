using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Sync;

public static class SyncProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 5150;
    public const int BeaconPort = 5151;
    public const long MaxBlobBytes = 256L * 1024 * 1024;

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

    /// <summary>
    /// Checks a change from someone else before anything uses it. Bank names must be plain file names
    /// (no folders, so "..\\x" can't point outside the sound folder); text fields are bounded.
    /// Returns null when valid.
    /// </summary>
    public static string? Validate(SyncChange c)
    {
        if (string.IsNullOrWhiteSpace(c.Bank) || c.Bank.Length > 128 || c.Bank != Path.GetFileName(c.Bank) ||
            c.Bank.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !(c.Bank.EndsWith(".sabs", StringComparison.OrdinalIgnoreCase) || c.Bank.EndsWith(".sabl", StringComparison.OrdinalIgnoreCase)))
            return "Invalid bank name.";
        if (c.Index < 0 || c.SampleCount < 0 || c.Channels is < 0 or > 8 || c.SampleRate < 0 || c.BlobSize is < 0 or > MaxBlobBytes)
            return "Invalid sound details.";
        if (c.OriginalChecksum is not { Length: 32 } || !c.OriginalChecksum.All(Uri.IsHexDigit))
            return "Invalid checksum.";
        if (c.Action == SyncAction.Replace && (c.BlobSha256 is not { Length: 64 } || !c.BlobSha256.All(Uri.IsHexDigit)))
            return "Invalid audio reference.";
        if (c.Author.Length > 64 || c.ClientId.Length > 64 || (c.SourceName?.Length ?? 0) > 260 || (c.SoundName?.Length ?? 0) > 512)
            return "Text too long.";
        return null;
    }

    /// <summary>A name from someone else, safe to show and to use in a file name.</summary>
    public static string CleanName(string? name, string fallback = "Friend")
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

public enum SyncAction
{
    /// <summary>The sound now has the audio in the blob.</summary>
    Replace,

    /// <summary>The sound went back to the original game audio.</summary>
    Revert,
}

/// <summary>
/// One change to one sound, as shared between Sound Studio installs. The audio itself travels separately
/// as a content-addressed blob, already encoded for the game (so receivers write it as-is).
/// </summary>
public sealed record SyncChange
{
    /// <summary>Server order; 0 until the server accepts it.</summary>
    public long Seq { get; init; }
    public Guid ChangeId { get; init; } = Guid.NewGuid();
    public required string ClientId { get; init; }
    public required string Author { get; init; }
    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;
    public required SyncAction Action { get; init; }

    public required string Bank { get; init; }
    public required int Index { get; init; }
    public required uint Id { get; init; }

    /// <summary>MD5 from the bank's checksum table: proves both sides start from the same game sound.</summary>
    public required string OriginalChecksum { get; init; }

    public AudioFormat Format { get; init; }
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public int SampleCount { get; init; }

    public string? BlobSha256 { get; init; }
    public long BlobSize { get; init; }

    /// <summary>File the author used (e.g. "my_voice.wav"), for display.</summary>
    public string? SourceName { get; init; }
    public string? SoundName { get; init; }
}

/// <summary>Unauthenticated hello: enough to recognise the right host, nothing secret.</summary>
public sealed record HostHello(string HostId, string HostName, int Protocol);

/// <summary>Authenticated host details: current addresses (they change, e.g. a new tunnel URL).</summary>
public sealed record HostDetails(string HostId, string HostName, IReadOnlyList<string> Urls, IReadOnlyList<PeerInfo> Peers, long LatestSeq);

public sealed record PeerInfo(string ClientId, string Name, DateTime LastSeenUtc);

/// <summary>
/// Everything a friend needs to connect, as one copy-pasteable string:
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
