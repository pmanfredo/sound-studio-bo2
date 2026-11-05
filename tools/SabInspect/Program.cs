using System.Security.Cryptography;
using SoundStudio.Core.Bo2;

if (args.Length == 4 && args[0] == "dump") { using var db = SabBank.Open(args[1]); var de = int.TryParse(args[2], out var di) ? db.Entries[di] : db.Entries.First(x => x.DisplayName.Contains(args[2])); File.WriteAllBytes(args[3], de.ReadOriginalData()); Console.WriteLine($"{de} samples={de.SampleCount} ch={de.ChannelCount} size={de.Size} head={Convert.ToHexString(de.ReadOriginalData(), 0, 48)}"); return; }
if (args.Length == 2 && args[0] == "peek") { Peek(args[1]); return; }
if (args.Length == 3 && args[0] is "edit" or "rawedit") { DevEdit(args); return; }

// Usage: SabInspect <folder-or-file> [--verbose]
// Read-only survey of BO2 banks: layout, gaps, checksum validity, formats, name coverage.
var target = args.Length > 0 ? args[0] : throw new ArgumentException("Pass a folder or .sabs/.sabl file.");
var verbose = args.Contains("--verbose");
var files = Directory.Exists(target)
    ? Directory.EnumerateFiles(target).Where(f => f.EndsWith(".sabs") || f.EndsWith(".sabl")).OrderBy(f => f).ToList()
    : [target];

var formats = new Dictionary<AudioFormat, int>();
var rates = new Dictionary<int, int>();
int totalEntries = 0, named = 0, badChecksums = 0, failed = 0;
foreach (var file in files)
{
    try
    {
        using var bank = SabBank.Open(file);
        var e = bank.Entries;
        totalEntries += e.Count;
        named += e.Count(x => x.Name != null);
        foreach (var x in e) { formats[x.Format] = formats.GetValueOrDefault(x.Format) + 1; rates[x.SampleRate] = rates.GetValueOrDefault(x.SampleRate) + 1; }

        // Layout analysis.
        var byOffset = e.OrderBy(x => x.Offset).ToList();
        bool inEntryOrder = e.Select(x => x.Offset).SequenceEqual(byOffset.Select(x => x.Offset));
        long expected = SabBank.HeaderSize; int gaps = 0; long gapBytes = 0; var gapSizes = new HashSet<long>();
        foreach (var x in byOffset) { if (x.Offset != expected) { gaps++; gapBytes += x.Offset - expected; gapSizes.Add(x.Offset - expected); } expected = x.Offset + x.Size; }
        long Align(long v) => (v + 0x7FF) & ~0x7FFL;
        bool tableAtExpected = bank.EntryTableOffset == Align(expected);
        bool sumsAtExpected = bank.ChecksumTableOffset == Align(bank.EntryTableOffset + e.Count * SabBank.EntrySize);
        bool lenOk = bank.DeclaredLength == bank.FileLength && bank.FileLength == Align(bank.ChecksumTableOffset + e.Count * 16);

        // Checksums: MD5 of data == stored checksum?
        int bad = 0;
        using var md5 = MD5.Create();
        foreach (var x in e) if (!md5.ComputeHash(x.ReadOriginalData()).AsSpan().SequenceEqual(x.Checksum)) bad++;
        badChecksums += bad;

        // Header: anything nonzero past the dependency strings / padding?
        var h = bank.RawHeader;
        int depEnd = 0x48 + bank.Dependencies.Count * 0x40;
        int nonZeroTail = h.Skip(depEnd).Count(b => b != 0);
        int pad1C = BitConverter.ToInt32(h, 0x1C);

        Console.WriteLine($"{Path.GetFileName(file),-34} {e.Count,6} snd {bank.FileLength / 1048576.0,8:0.0} MB  order={(inEntryOrder ? "entry" : "OTHER")} gaps={gaps}({gapBytes}B{(gapSizes.Count > 0 ? " e.g. " + string.Join(",", gapSizes.Take(3)) : "")}) tbl={tableAtExpected} sums={sumsAtExpected} len={lenOk} badMD5={bad} deps={bank.Dependencies.Count} hdrTail!=0:{nonZeroTail} pad1C={pad1C}");
        if (verbose) foreach (var x in e.Take(15)) Console.WriteLine("    " + x + $" off=0x{x.Offset:X} size={x.Size} ch={x.ChannelCount} loop={x.LoopRaw}");
    }
    catch (Exception ex) { failed++; Console.WriteLine($"{Path.GetFileName(file),-34} ERROR {ex.Message}"); }
}
Console.WriteLine();
Console.WriteLine($"banks={files.Count} failed={failed} entries={totalEntries} named={named} ({100.0 * named / Math.Max(1, totalEntries):0.0}%) badChecksums={badChecksums} nameDb={SoundNameDatabase.Count}");
Console.WriteLine("formats: " + string.Join(", ", formats.Select(kv => $"{kv.Key.DisplayName()}={kv.Value}")));
Console.WriteLine("rates:   " + string.Join(", ", rates.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}")));

// --mismatches: list sounds whose data no longer matches the stored MD5.
if (args.Contains("--mismatches"))
{
    foreach (var file in files)
    {
        using var bank = SabBank.Open(file);
        using var md5 = MD5.Create();
        var bad = bank.Entries.Where(x => !md5.ComputeHash(x.ReadOriginalData()).AsSpan().SequenceEqual(x.Checksum)).ToList();
        if (bad.Count == 0) continue;
        Console.WriteLine($"== {Path.GetFileName(file)} ({bad.Count}) by format: " + string.Join(", ", bad.GroupBy(b => b.Format).Select(g => $"{g.Key.DisplayName()}={g.Count()}")) + $" | mtime {File.GetLastWriteTime(file):yyyy-MM-dd}");
        foreach (var x in bad.Take(8)) Console.WriteLine($"   {x}");
    }
}

// Dev helpers for sandboxes (never point these at real game files):
//   edit <bank> <index>       replace a sound with silence via BankEditor (saves original + history; uses SOUNDSTUDIO_DATA)
//   rawedit <bank> <index>    replace without saving an original, like the old tool
static void DevEdit(string[] a)
{
    var path = a[1]; var index = int.Parse(a[2]);
    byte[] silence; uint id; int samples;
    using (var b = SabBank.Open(path)) { var e = b.Entries[index]; id = e.Id; samples = e.SampleCount; silence = new byte[Math.Max(64, e.Size / 2)]; }
    if (a[0] == "edit")
    {
        var editor = new SoundStudio.Core.Editing.BankEditor(
            new SoundStudio.Core.Editing.OriginalsStore(SoundStudio.Core.AppPaths.OriginalsFolder),
            new SoundStudio.Core.Editing.ChangeHistory(SoundStudio.Core.AppPaths.HistoryFile), "sandbox");
        editor.Apply(path, [new SoundStudio.Core.Editing.SoundChange(index, id, silence, samples, "silence.wav")]);
    }
    else
    {
        var bank = SabBank.Open(path); bank.Entries[index].Replace(silence, samples); SabWriter.SaveInPlace(bank).Dispose();
    }
    Console.WriteLine($"{a[0]} {Path.GetFileName(path)} #{index} done");
}

static void Peek(string bankPath)
{
    using var b = SabBank.Open(bankPath);
    foreach (var e in b.Entries.Where(e => e.Format == AudioFormat.Flac).Take(3))
    {
        var d = e.ReadOriginalData();
        Console.WriteLine($"{e.DisplayName}: {System.Text.Encoding.ASCII.GetString(d, 0, 4)} {Convert.ToHexString(d, 0, 16)} size={d.Length} samples={e.SampleCount} ch={e.ChannelCount}");
    }
}
