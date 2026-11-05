using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundStudio.Core.Editing;

[JsonConverter(typeof(JsonStringEnumConverter<ChangeAction>))]
public enum ChangeAction
{
    Replaced,
    Reverted,
    OriginalRecovered,
}

/// <summary>One line of the change history.</summary>
public sealed record ChangeRecord(
    DateTime TimeUtc,
    ChangeAction Action,
    string Bank,
    int Index,
    uint Id,
    string? SoundName,
    string? Detail,
    string Author);

/// <summary>
/// Append-only log of every change (JSON Lines), so the app can show who changed what and when.
/// Later phases sync these records between users.
/// </summary>
public sealed class ChangeHistory(string path)
{
    private static readonly JsonSerializerOptions Json = new();
    private readonly object _lock = new();

    public string FilePath { get; } = path;

    public void Append(IEnumerable<ChangeRecord> records)
    {
        var lines = records.Select(r => JsonSerializer.Serialize(r, Json)).ToList();
        if (lines.Count == 0)
            return;
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllLines(FilePath, lines);
        }
    }

    /// <summary>All records, oldest first. Unreadable lines are skipped.</summary>
    public List<ChangeRecord> ReadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
                return [];
            var result = new List<ChangeRecord>();
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<ChangeRecord>(line, Json) is { } record)
                        result.Add(record);
                }
                catch (JsonException) { }
            }
            return result;
        }
    }
}
