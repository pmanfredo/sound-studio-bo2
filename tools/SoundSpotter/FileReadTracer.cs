using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace SoundSpotter;

/// <summary>Options shared by live tracing and .etl replay.</summary>
public sealed record SpotterOptions
{
    /// <summary>Only count reads from processes whose name contains this (case-insensitive). Null = any process.</summary>
    public string? ProcessFilter { get; init; }
    /// <summary>Print every bank read (offset, size, owning sound), not just play starts.</summary>
    public bool Raw { get; init; }
    /// <summary>Also print bulk and metadata reads.</summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// Watches kernel file-I/O ETW events for reads of BO2 sound banks and names the sound at each read offset.
/// This only consumes OS trace events: it never opens, reads, or signals the game process.
/// </summary>
public sealed class FileReadTracer
{
    private readonly BankIndex _index;
    private readonly ReadInterpreter _interpreter;
    private readonly SpotterOptions _options;
    private readonly ReadStats _stats = new();
    private readonly object _gate = new();

    public FileReadTracer(BankIndex index, SpotterOptions options)
    {
        _index = index;
        _options = options;
        _interpreter = new ReadInterpreter(index);
    }

    public ReadStats Stats => _stats;

    /// <summary>Raised for every interpreted bank read (on the ETW thread).</summary>
    public event Action<SpotEvent>? Spotted;

    /// <summary>Raised once the live session is enabled and events are flowing.</summary>
    public event Action? Started;

    /// <summary>Runs a real-time kernel session until <paramref name="cancel"/> fires. Needs admin.</summary>
    public void RunLive(CancellationToken cancel)
    {
        if (TraceEventSession.IsElevated() != true)
            throw new UnauthorizedAccessException("Kernel file I/O tracing needs an elevated (Run as administrator) terminal.");

        // Windows 8+ allows private kernel sessions, so we don't fight other tools for "NT Kernel Logger".
        using var session = new TraceEventSession("SoundSpotter-FileIO") { StopOnDispose = true, BufferSizeMB = 64 };
        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.FileIOInit |   // FileCreate / FileName events: maps file objects to paths
            KernelTraceEventParser.Keywords.FileIO |       // FileIo Read with offset + size, cached or not
            KernelTraceEventParser.Keywords.DiskFileIO |   // file-name rundown for files opened before we started
            KernelTraceEventParser.Keywords.MemoryHardFaults | // would reveal memory-mapped access, if the game uses it
            KernelTraceEventParser.Keywords.Process);
        using var reg = cancel.Register(() => session.Stop());
        Attach(session.Source);
        Started?.Invoke();
        session.Source.Process();
    }

    /// <summary>Replays a trace recorded elsewhere, e.g. <c>wpr -start FileIO</c> … <c>wpr -stop game.etl</c>.</summary>
    public void Replay(string etlPath)
    {
        using var source = new ETWTraceEventSource(etlPath);
        Attach(source);
        source.Process();
    }

    private void Attach(TraceEventSource source)
    {
        var kernel = new KernelTraceEventParser(source);
        kernel.FileIORead += OnRead;
        kernel.FileIOCreate += data =>
        {
            if (!BankIndex.IsBankFile(data.FileName) || !ProcessMatches(data.ProcessName)) return;
            lock (_gate) _stats.Opens++;
            if (_options.Raw || _options.Verbose)
                Console.WriteLine($"{data.TimeStamp:HH:mm:ss.fff}  [open ] {Path.GetFileName(data.FileName)} by {data.ProcessName} ({data.ProcessID})");
        };
        kernel.MemoryHardFault += data =>
        {
            if (!BankIndex.IsBankFile(data.FileName) || !ProcessMatches(data.ProcessName)) return;
            lock (_gate) _stats.HardFaults++;
            // A hard fault on a bank means the game maps it into memory; treat the faulting page like a read.
            Handle(data.FileName, (long)data.ReadOffset, data.ByteCount, data.TimeStamp, data.ProcessName, "fault");
        };
    }

    private void OnRead(FileIOReadWriteTraceData data)
    {
        if (!BankIndex.IsBankFile(data.FileName) || !ProcessMatches(data.ProcessName)) return;
        Handle(data.FileName, data.Offset, data.IoSize, data.TimeStamp, data.ProcessName, "read");
    }

    private bool ProcessMatches(string? name) =>
        _options.ProcessFilter == null || (name?.Contains(_options.ProcessFilter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void Handle(string fileName, long offset, long size, DateTime time, string process, string what)
    {
        lock (_gate)
        {
            var ev = _interpreter.Process(fileName, offset, size, time, process);
            if (ev == null) return;
            _stats.Record(ev);
            Spotted?.Invoke(ev);

            if (_options.Raw)
                Console.WriteLine($"{time:HH:mm:ss.fff}  [{what,-5}] {ev.Bank.Name} @0x{offset:X8} +{size,-8} {process,-28} -> {ev.Kind}" +
                                  (ev.Entry != null ? $" {ev.Entry.DisplayName} (+{offset - ev.Entry.Offset} of {ev.Entry.Size})" : ""));
            else if (ev.IsPlay || (_options.Verbose && ev.Kind is SpotKind.Bulk or SpotKind.Metadata))
                Console.WriteLine(ev);
        }
    }
}

/// <summary>Evidence for the report: how the game actually reads banks.</summary>
public sealed class ReadStats
{
    public int Opens;
    public int HardFaults;
    public readonly Dictionary<SpotKind, int> ByKind = new();
    public readonly Dictionary<bool, int> ReadsByStreamed = new() { [true] = 0, [false] = 0 };
    public readonly List<long> ReadSizes = new();
    public readonly HashSet<(string, int)> DistinctPlayed = new();
    public readonly Dictionary<string, int> Processes = new();

    public void Record(SpotEvent ev)
    {
        ByKind[ev.Kind] = ByKind.GetValueOrDefault(ev.Kind) + 1;
        ReadsByStreamed[ev.Bank.Streamed]++;
        if (ReadSizes.Count < 1_000_000) ReadSizes.Add(ev.Length);
        if (ev.IsPlay && ev.Entry != null) DistinctPlayed.Add((ev.Bank.Name, ev.Entry.Index));
        Processes[ev.Process] = Processes.GetValueOrDefault(ev.Process) + 1;
    }

    public void Print(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("=== SoundSpotter summary (paste this back) ===");
        w.WriteLine($"bank opens seen:        {Opens}");
        w.WriteLine($"reads of .sabs / .sabl: {ReadsByStreamed[true]} / {ReadsByStreamed[false]}");
        w.WriteLine($"hard faults on banks:   {HardFaults} (non-zero => the game memory-maps banks)");
        foreach (var (k, v) in ByKind.OrderBy(k => k.Key)) w.WriteLine($"  {k,-12} {v}");
        w.WriteLine($"distinct sounds played: {DistinctPlayed.Count}");
        if (ReadSizes.Count > 0)
        {
            var s = ReadSizes.Order().ToArray();
            w.WriteLine($"read size bytes: min {s[0]}, median {s[s.Length / 2]}, p90 {s[s.Length * 9 / 10]}, max {s[^1]}");
            foreach (var g in s.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(5))
                w.WriteLine($"  {g.Key,10} x{g.Count()}");
        }
        foreach (var (p, n) in Processes.OrderByDescending(p => p.Value)) w.WriteLine($"process: {p} ({n} reads)");
    }
}
