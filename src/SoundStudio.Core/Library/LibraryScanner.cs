using System.Text.Json;
using System.Text.Json.Serialization;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Library;

public sealed record ScanProgress(int BanksDone, int BankCount, string CurrentBank, bool FromCache);

/// <summary>
/// Scans a BO2 <c>sound</c> folder: reads every bank's table and works out which sounds are modified
/// (audio no longer matches the stored MD5). Checking every sound reads ~5 GB, so results are cached per bank
/// and only recomputed when a bank's size or timestamp changes.
/// </summary>
public sealed class LibraryScanner
{
    private const int CacheVersion = 1;
    private readonly string? _cachePath;

    /// <param name="cachePath">JSON file for scan results, or null to disable caching.</param>
    public LibraryScanner(string? cachePath) => _cachePath = cachePath;

    public static bool LooksLikeSoundFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && EnumerateBanks(folder).Any();

    public static IEnumerable<string> EnumerateBanks(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".sabs", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".sabl", StringComparison.OrdinalIgnoreCase));

    public async Task<GameLibrary> ScanAsync(string soundFolder, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateBanks(soundFolder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var cache = await LoadCacheAsync().ConfigureAwait(false);
        var results = new BankInfo[files.Count];
        var done = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = cancellationToken },
            (i, ct) =>
            {
                var file = files[i];
                var fromCache = false;
                results[i] = ScanBank(file, cache, ref fromCache, ct);
                progress?.Report(new ScanProgress(Interlocked.Increment(ref done), files.Count, Path.GetFileName(file), fromCache));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        await SaveCacheAsync(results).ConfigureAwait(false);
        return new GameLibrary(soundFolder, results);
    }

    private static BankInfo ScanBank(string file, CacheFile cache, ref bool fromCache, CancellationToken ct)
    {
        var fi = new FileInfo(file);
        var description = BankNaming.Describe(file);
        try
        {
            using var bank = SabBank.Open(file);
            var info = new BankInfo
            {
                FilePath = fi.FullName,
                FileLength = fi.Length,
                LastWriteTimeUtc = fi.LastWriteTimeUtc,
                Description = description,
            };

            HashSet<int>? modified = null;
            if (cache.Banks.TryGetValue(fi.FullName.ToLowerInvariant(), out var cached) &&
                cached.Length == fi.Length && cached.LastWriteTimeUtc == fi.LastWriteTimeUtc &&
                cached.EntryCount == bank.Entries.Count)
            {
                modified = [.. cached.Modified];
                fromCache = true;
            }

            foreach (var e in bank.Entries)
            {
                ct.ThrowIfCancellationRequested();
                info.Sounds.Add(new SoundInfo
                {
                    Bank = info,
                    Index = e.Index,
                    Id = e.Id,
                    Format = e.Format,
                    SampleCount = e.SampleCount,
                    SampleRate = e.SampleRate,
                    Channels = e.ChannelCount,
                    Loop = e.Loop,
                    Size = e.Size,
                    IsModified = modified?.Contains(e.Index) ?? e.IsModifiedOnDisk(),
                });
            }
            return info;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new BankInfo
            {
                FilePath = fi.FullName,
                FileLength = fi.Exists ? fi.Length : 0,
                LastWriteTimeUtc = fi.Exists ? fi.LastWriteTimeUtc : default,
                Description = description,
                Error = ex.Message,
            };
        }
    }

    // ---- cache -------------------------------------------------------------------------------------------

    private sealed class CacheFile
    {
        public int Version { get; set; } = CacheVersion;
        public Dictionary<string, CachedBank> Banks { get; set; } = [];
    }

    private sealed class CachedBank
    {
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public int EntryCount { get; set; }
        public int[] Modified { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private async Task<CacheFile> LoadCacheAsync()
    {
        if (_cachePath == null || !File.Exists(_cachePath))
            return new CacheFile();
        try
        {
            await using var fs = File.OpenRead(_cachePath);
            var cache = await JsonSerializer.DeserializeAsync<CacheFile>(fs, JsonOptions).ConfigureAwait(false);
            return cache is { Version: CacheVersion } ? cache : new CacheFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new CacheFile(); // a broken cache only costs a slower scan
        }
    }

    private async Task SaveCacheAsync(IEnumerable<BankInfo> banks)
    {
        if (_cachePath == null)
            return;
        var cache = new CacheFile();
        foreach (var b in banks.Where(b => b.Error == null))
        {
            cache.Banks[b.FilePath.ToLowerInvariant()] = new CachedBank
            {
                Length = b.FileLength,
                LastWriteTimeUtc = b.LastWriteTimeUtc,
                EntryCount = b.Sounds.Count,
                Modified = b.Sounds.Where(s => s.IsModified).Select(s => s.Index).ToArray(),
            };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            await using (var fs = File.Create(temp))
                await JsonSerializer.SerializeAsync(fs, cache, JsonOptions).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (IOException)
        {
            // Cache is an optimisation; ignore write failures.
        }
    }
}
