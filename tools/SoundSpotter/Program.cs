using System.Diagnostics;
using SoundSpotter;
using SoundStudio.Audio;
using SoundStudio.Core.Bo2;

// SoundSpotter: which BO2 sound is the game playing right now? Research prototype; see docs/sound-spotter.md.
// It never touches the game process: `trace` only listens to Windows' own kernel file-I/O trace events.

var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    @"Plutonium\Black_Ops_II\pluto_t6_full_game\sound");

string Opt(string name, string fallback = "")
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
bool Flag(string name) => args.Contains(name);
var folder = Opt("--sound", defaultFolder);
var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "stats":
        Stats(BankIndex.Load(folder));
        break;

    case "trace":
    case "replay":
    {
        Console.Error.WriteLine($"Indexing banks in {folder} ...");
        var index = BankIndex.Load(folder);
        Console.Error.WriteLine($"{index.Banks.Count} banks, {index.Banks.Sum(b => b.Entries.Count)} sounds, {SoundNameDatabase.Count} known names.");
        using var log = Opt("--log") is { Length: > 0 } logPath ? new StreamWriter(logPath, append: false) : null;
        var tracer = new FileReadTracer(index, new SpotterOptions
        {
            Log = log,
            ProcessFilter = Opt("--process") is { Length: > 0 } p ? p : null,
            Raw = Flag("--raw"),
            Verbose = Flag("--verbose"),
        });
        if (command == "replay")
        {
            tracer.Replay(args.Length > 1 ? args[1] : throw new ArgumentException("replay <trace.etl>"));
        }
        else
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.Error.WriteLine("Tracing. Start or keep playing Black Ops II; press Ctrl+C to stop and print a summary.");
            try { tracer.RunLive(cts.Token); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        tracer.Finish();
        break;
    }

    case "selftest":
    {
        // Proves the ETW pipeline without the game: we read one known sound from a .sabs ourselves
        // (read-only, shared) and check the tracer names it. Needs admin like `trace`.
        var index = BankIndex.Load(folder);
        var bank = index.Banks.Where(b => b.Streamed).OrderBy(b => b.Name).First();
        var target = bank.Entries[bank.Entries.Count / 2];
        var self = Process.GetCurrentProcess().ProcessName;
        var tracer = new FileReadTracer(index, new SpotterOptions { ProcessFilter = self, Raw = true });
        var seen = new TaskCompletionSource<SpotEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        tracer.Spotted += ev => { if (ev.IsPlay && ev.Entry == target) seen.TrySetResult(ev); };
        tracer.Started += () => Task.Run(async () =>
        {
            await Task.Delay(1500);
            using var fs = new FileStream(Path.Combine(folder, bank.Name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            fs.Position = target.Offset;
            fs.ReadExactly(new byte[Math.Min(target.Size, 65536)]);
            Console.WriteLine($"selftest: read {bank.Name} @0x{target.Offset:X} (expect {target.DisplayName})");
        });
        var run = Task.Run(() => tracer.RunLive(cts.Token));
        var done = await Task.WhenAny(seen.Task, run, Task.Delay(TimeSpan.FromSeconds(15)));
        cts.Cancel();
        if (done == run && run.IsFaulted) { Console.Error.WriteLine(run.Exception!.InnerException!.Message); return 1; }
        if (seen.Task.IsCompletedSuccessfully)
        {
            var ev = seen.Task.Result;
            Console.WriteLine($"PASS: traced and named {ev.Entry!.DisplayName}; event arrived {(DateTime.Now - ev.Time).TotalMilliseconds:0} ms after the read.");
            return 0;
        }
        Console.WriteLine("FAIL: the read was not seen. Paste the output above into the report thread.");
        tracer.Stats.Print(Console.Out);
        return 2;
    }

    case "reinterpret":
    {
        // Re-runs the interpreter over a saved `trace --raw` log, e.g. after tuning its rules.
        var index = BankIndex.Load(folder);
        var interpreter = new ReadInterpreter(index);
        var pattern = new System.Text.RegularExpressions.Regex(@"^(\S+)\s+\[(?:read |fault)\] (\S+) @0x([0-9A-F]+) \+(\d+)");
        string? loadBank = null; var loadCount = 0;
        foreach (var line in File.ReadLines(args.Length > 1 ? args[1] : throw new ArgumentException("reinterpret <raw.txt>")))
        {
            var m = pattern.Match(line);
            if (!m.Success) continue;
            var time = DateTime.Today + TimeSpan.Parse(m.Groups[1].Value);
            var ev = interpreter.Process(m.Groups[2].Value, Convert.ToInt64(m.Groups[3].Value, 16), long.Parse(m.Groups[4].Value), time);
            if (ev == null) continue;
            if (ev.Kind == SpotKind.Load) { if (loadBank != ev.Bank.Name && loadCount > 0) Console.WriteLine($"          [load ] {loadBank}: {loadCount} sounds"); if (loadBank != ev.Bank.Name) loadCount = 0; loadBank = ev.Bank.Name; loadCount++; continue; }
            if (loadCount > 0) { Console.WriteLine($"          [load ] {loadBank}: {loadCount} sounds"); loadCount = 0; loadBank = null; }
            if (ev.IsPlay || (Flag("--verbose") && ev.Kind == SpotKind.Loop)) Console.WriteLine(ev);
        }
        break;
    }

    case "fpbench":
        FingerprintBench(BankIndex.Load(folder), folder,
            int.Parse(Opt("--sounds", "400")), int.Parse(Opt("--queries", "100")), int.Parse(Opt("--seed", "1")));
        break;

    default:
        Console.WriteLine("""
            SoundSpotter - name the Black Ops II sound being played, from outside the game.

              stats                      streamed (.sabs) vs loaded (.sabl) sound counts and layout
              trace [--process name]     LIVE: watch kernel file reads of banks (Run as administrator)
                    [--raw] [--verbose]    --raw prints every read with offset/size (for the report)
                    [--log file]           also write everything, plus the summary, to a file
              selftest                   check tracing works (admin, no game needed): reads one sound and names it
              replay <trace.etl>        same, from a trace recorded with: wpr -start FileIO ... wpr -stop x.etl
              fpbench [--sounds 400] [--queries 100]
                                         offline accuracy/speed test of audio fingerprinting

              --sound <folder>           game sound folder (default: Documents\Plutonium\...\sound)
            """);
        break;
}
return 0;

static void Stats(BankIndex index)
{
    foreach (var streamed in new[] { true, false })
    {
        var banks = index.Banks.Where(b => b.Streamed == streamed).ToList();
        var entries = banks.SelectMany(b => b.Entries).ToList();
        var sizes = entries.Select(e => (long)e.Size).Order().ToArray();
        var aligned = entries.Count(e => e.Offset % 0x800 == 0);
        Console.WriteLine($"{(streamed ? ".sabs (streamed)" : ".sabl (loaded)  ")}: {banks.Count,3} banks, {entries.Count,6} sounds, " +
                          $"{sizes.Sum() / 1048576.0,6:0} MB, {entries.Sum(e => e.Duration.TotalHours),5:0.0} h audio, " +
                          $"named {entries.Count(e => e.Name != null) * 100.0 / Math.Max(1, entries.Count):0}%");
        if (sizes.Length > 0)
            Console.WriteLine($"    size median {sizes[sizes.Length / 2] / 1024.0:0} KB, p90 {sizes[sizes.Length * 9 / 10] / 1024.0:0} KB, " +
                              $"offsets 0x800-aligned: {aligned * 100.0 / entries.Count:0}%");
    }
    var all = index.Banks.SelectMany(b => b.Entries).ToList();
    var streamedEntries = index.Banks.Where(b => b.Streamed).SelectMany(b => b.Entries).ToList();
    Console.WriteLine($"total {all.Count} sounds; streamed = {streamedEntries.Count * 100.0 / all.Count:0.0}% of sounds, " +
                      $"{streamedEntries.Sum(e => e.Duration.TotalSeconds) * 100 / all.Sum(e => e.Duration.TotalSeconds):0.0}% of audio time");
    Console.WriteLine($"ids present in more than one bank: {all.GroupBy(e => e.Id).Count(g => g.Count() > 1)}");
}

static void FingerprintBench(BankIndex index, string folder, int soundCount, int queryCount, int seed)
{
    var rng = new Random(seed);
    var pool = index.Banks.SelectMany(b => b.Entries).Where(e => e.Duration.TotalSeconds >= 0.4).ToList();
    var picked = pool.OrderBy(_ => rng.Next()).Take(soundCount).ToList();
    var audio = new List<float[]>();
    var fpIndex = new FingerprintIndex();
    var decodeTime = new Stopwatch();
    var fpTime = new Stopwatch();
    double seconds = 0;
    foreach (var e in picked)
    {
        float[] mono;
        decodeTime.Start();
        try
        {
            using var bank = SabBank.Open(Path.Combine(folder, e.Bank));
            var entry = bank.Entries[e.Index];
            var decoded = AudioDecoder.Decode(entry.ReadOriginalData(), entry.Format, Math.Max(1, (int)entry.ChannelCount), entry.SampleRate);
            mono = Fingerprinter.ToMono11k(decoded.Pcm16, decoded.SampleRate, decoded.Channels);
        }
        catch { continue; }
        finally { decodeTime.Stop(); }
        fpTime.Start();
        fpIndex.Add(audio.Count, Fingerprinter.Landmarks(mono));
        fpTime.Stop();
        audio.Add(mono);
        seconds += mono.Length / (double)Fingerprinter.SampleRate;
    }
    var library = index.Banks.SelectMany(b => b.Entries).Sum(e => e.Duration.TotalSeconds);
    var perSec = fpIndex.LandmarkCount / seconds;
    Console.WriteLine($"indexed {audio.Count} sounds, {seconds / 60:0.0} min audio: {fpIndex.LandmarkCount} landmarks ({perSec:0}/s of audio)");
    Console.WriteLine($"  decode {decodeTime.Elapsed.TotalSeconds:0.0}s, fingerprint {fpTime.Elapsed.TotalSeconds:0.0}s (single thread)");
    Console.WriteLine($"  extrapolated to whole library ({library / 3600:0.0} h): ~{perSec * library * 10 / 1048576:0} MB compact index, " +
                      $"~{(decodeTime.Elapsed + fpTime.Elapsed).TotalSeconds * library / seconds / 60:0} min single-thread build");

    // Queries: a 1.5 s window of a random indexed sound, put through the kinds of damage the game's mix does.
    var len = (int)(1.5 * Fingerprinter.SampleRate);
    float[] Cut(float[] s, int start) { var w = new float[len]; Array.Copy(s, start, w, 0, Math.Min(len, s.Length - start)); return w; }
    float[] Other(int i, int k) => Cut(audio[(i * 7 + k * 13 + 1) % audio.Count], 0);
    var cases = new (string Name, Func<int, float[], float[]> Damage)[]
    {
        ("clean window", (_, q) => q),
        ("half volume + noise (SNR 10 dB)", (_, q) => AddNoise(Scale(q, 0.5f), rng, 10)),
        ("+1 other sound, equal level", (i, q) => Mix(q, Other(i, 1))),
        ("+2 other sounds, equal level", (i, q) => Mix(Mix(q, Other(i, 1)), Other(i, 2))),
        ("+3 others, target 6 dB quieter", (i, q) => Mix(Mix(Mix(Scale(q, 0.5f), Other(i, 1)), Other(i, 2)), Other(i, 3))),
        ("pitch/speed +3% (alias pitch variance)", (_, q) => Fingerprinter.Resample(q, 1.03, 1.0)),
        ("lowpass (distance / occlusion)", (_, q) => Lowpass(q, 0.15f)),
    };

    var targets = Enumerable.Range(0, queryCount).Select(_ => rng.Next(audio.Count)).ToArray();
    foreach (var (name, damage) in cases)
    {
        int top1 = 0, top3 = 0;
        var qTime = Stopwatch.StartNew();
        foreach (var t in targets)
        {
            var src = audio[t];
            var q = damage(t, Cut(src, src.Length > len ? rng.Next(src.Length - len) : 0));
            var hits = fpIndex.Query(Fingerprinter.Landmarks(q));
            if (hits.Count > 0 && hits[0].SoundId == t && hits[0].Score >= 4) top1++;
            if (hits.Any(h => h.SoundId == t && h.Score >= 4)) top3++;
        }
        Console.WriteLine($"  {name,-40} top-1 {top1 * 100.0 / targets.Length,4:0}%   in top-3 {top3 * 100.0 / targets.Length,4:0}%   " +
                          $"{qTime.Elapsed.TotalMilliseconds / targets.Length:0.0} ms/query");
    }
}

static float[] Scale(float[] a, float k) => a.Select(x => x * k).ToArray();
static float[] Mix(float[] a, float[] b) { var r = (float[])a.Clone(); for (var i = 0; i < Math.Min(a.Length, b.Length); i++) r[i] += b[i]; return r; }
static float[] Lowpass(float[] a, float alpha) { var r = new float[a.Length]; float y = 0; for (var i = 0; i < a.Length; i++) r[i] = y += alpha * (a[i] - y); return r; }
static float[] AddNoise(float[] a, Random rng, double snrDb)
{
    var rms = Math.Sqrt(a.Average(x => (double)x * x));
    var amp = rms / Math.Pow(10, snrDb / 20) * Math.Sqrt(3); // uniform noise with that RMS
    return a.Select(x => x + (float)((rng.NextDouble() * 2 - 1) * amp)).ToArray();
}
