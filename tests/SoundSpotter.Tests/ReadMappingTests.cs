using SoundSpotter;

namespace SoundSpotter.Tests;

public class ReadMappingTests
{
    // Three sounds packed like a real bank: header up to 0x800, unaligned offsets, a padding gap before the last.
    private static readonly SpotEntry A = new("test.sabs", 0, 0xA, 0x800, 0x3000, TimeSpan.FromSeconds(1), @"sound\a.wav");
    private static readonly SpotEntry B = new("test.sabs", 1, 0xB, 0x3800, 0x10123, TimeSpan.FromSeconds(4), @"sound\b.wav");
    private static readonly SpotEntry C = new("test.sabs", 2, 0xC, 0x14000, 0x2000, TimeSpan.FromSeconds(1), null);

    private static BankIndex Index()
    {
        var index = new BankIndex();
        index.Add(new BankMap("test.sabs", true, [C, A, B])); // unsorted on purpose
        return index;
    }

    private static readonly DateTime T0 = new(2026, 11, 5, 20, 0, 0);

    [Theory]
    [InlineData(0x800, 0)]
    [InlineData(0x37FF, 0)]
    [InlineData(0x3800, 1)]
    [InlineData(0x13922, 1)]
    [InlineData(0x14000, 2)]
    [InlineData(0x15FFF, 2)]
    public void Find_returns_the_sound_containing_the_offset(long offset, int expected) =>
        Assert.Equal(expected, Index().Get("test.sabs")!.Find(offset)!.Index);

    [Theory]
    [InlineData(0x0)]      // header
    [InlineData(0x7FF)]
    [InlineData(0x13923)]  // padding after B
    [InlineData(0x16000)]  // entry table
    public void Find_returns_null_outside_sounds(long offset) => Assert.Null(Index().Get("test.sabs")!.Find(offset));

    [Fact]
    public void Overlapping_lists_every_sound_a_read_touches()
    {
        var map = Index().Get("test.sabs")!;
        Assert.Equal([0, 1], map.Overlapping(0x3000, 0x1000).Select(e => e.Index));
        Assert.Equal([0, 1, 2], map.Overlapping(0, 0x20000).Select(e => e.Index));
        Assert.Empty(map.Overlapping(0x13923, 0x10));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Users\x\Plutonium\sound\TEST.sabs")]
    [InlineData(@"C:\games\sound\test.sabs")]
    [InlineData("test.sabs")]
    public void Banks_are_found_by_any_path_form(string path) => Assert.NotNull(Index().Get(path));

    [Fact]
    public void Read_at_a_sound_start_is_a_play()
    {
        var ev = new ReadInterpreter(Index()).Process("test.sabs", 0x3800, 0x8000, T0)!;
        Assert.Equal(SpotKind.Start, ev.Kind);
        Assert.Equal(1, ev.Entry!.Index);
    }

    [Fact]
    public void Sector_aligned_read_starting_in_the_previous_tail_names_the_next_sound()
    {
        // 0x3000 is 4 KB-aligned; only 0x800 bytes of it belong to A, the other 0xF800 to B.
        var ev = new ReadInterpreter(Index()).Process("test.sabs", 0x3000, 0x10000, T0)!;
        Assert.Equal(SpotKind.Start, ev.Kind);
        Assert.Equal(1, ev.Entry!.Index);
    }

    [Fact]
    public void Later_chunks_of_a_streaming_sound_are_continuations()
    {
        var r = new ReadInterpreter(Index());
        r.Process("test.sabs", 0x3800, 0x8000, T0);
        var next = r.Process("test.sabs", 0xB800, 0x8000, T0.AddMilliseconds(500))!;
        Assert.Equal(SpotKind.Continue, next.Kind);
        Assert.False(next.IsPlay);
    }

    [Fact]
    public void Midway_read_after_silence_is_reported_as_a_play()
    {
        var r = new ReadInterpreter(Index());
        r.Process("test.sabs", 0x3800, 0x8000, T0);
        var late = r.Process("test.sabs", 0xB800, 0x8000, T0.AddSeconds(30))!;
        Assert.Equal(SpotKind.StartMidway, late.Kind);
        Assert.Equal(SpotKind.StartMidway, new ReadInterpreter(Index()).Process("test.sabs", 0xB800, 0x100, T0)!.Kind);
    }

    [Fact]
    public void Replaying_a_sound_reports_it_again()
    {
        var r = new ReadInterpreter(Index());
        r.Process("test.sabs", 0x800, 0x3000, T0);
        Assert.Equal(SpotKind.Start, r.Process("test.sabs", 0x800, 0x3000, T0.AddMilliseconds(200))!.Kind);
    }

    [Fact]
    public void Whole_bank_reads_are_bulk_and_header_reads_are_metadata()
    {
        var r = new ReadInterpreter(Index()) { BulkThreshold = 2 };
        Assert.Equal(SpotKind.Bulk, r.Process("test.sabs", 0, 0x20000, T0)!.Kind);
        Assert.Equal(SpotKind.Metadata, r.Process("test.sabs", 0, 0x800, T0)!.Kind);
    }

    [Fact]
    public void Loaded_bank_reads_are_loads_not_plays()
    {
        // Observed in Plutonium: each .sabl sound is read once, one read per sound, at level load.
        var index = Index();
        index.Add(new BankMap("test.sabl", false, [A with { Bank = "test.sabl" }, B with { Bank = "test.sabl" }]));
        var ev = new ReadInterpreter(index).Process("test.sabl", 0x3800, 0x10123, T0)!;
        Assert.Equal(SpotKind.Load, ev.Kind);
        Assert.False(ev.IsPlay);
        Assert.Equal(1, ev.Entry!.Index);
    }

    [Fact]
    public void A_restart_right_after_the_last_chunk_is_a_loop()
    {
        // Observed: looping ambience reads its final short chunk, then its first chunk again in the same tick.
        var r = new ReadInterpreter(Index());
        r.Process("test.sabs", 0x3800, 0x8000, T0);
        r.Process("test.sabs", 0xB800, 0x8123, T0.AddSeconds(3));          // reaches B's end (0x13923)
        var again = r.Process("test.sabs", 0x3800, 0x8000, T0.AddSeconds(3.01))!;
        Assert.Equal(SpotKind.Loop, again.Kind);
        Assert.False(again.IsPlay);
    }

    [Fact]
    public void Streaming_pattern_from_the_real_game_reports_one_play()
    {
        // 128 KB chunks from the sound's first byte, two at once to prebuffer, then about one per second.
        var big = new SpotEntry("music.sabs", 0, 0x1, 0x800, 1_000_000, TimeSpan.FromSeconds(6), @"sound\mus\x.snd");
        var index = new BankIndex();
        index.Add(new BankMap("music.sabs", true, [big]));
        var r = new ReadInterpreter(index);
        var kinds = new List<SpotKind>();
        for (var chunk = 0; chunk * 131072 < big.Size; chunk++)
        {
            var at = T0.AddSeconds(chunk < 2 ? 0 : chunk - 1);
            kinds.Add(r.Process("music.sabs", big.Offset + chunk * 131072L, Math.Min(131072, big.Size - chunk * 131072L), at)!.Kind);
        }
        Assert.Equal(SpotKind.Start, kinds[0]);
        Assert.All(kinds.Skip(1), k => Assert.Equal(SpotKind.Continue, k));
    }

    [Fact]
    public void Reads_of_other_files_are_ignored()
    {
        var r = new ReadInterpreter(Index());
        Assert.Null(r.Process(@"C:\x\other.sabs", 0x800, 10, T0));
        Assert.Null(r.Process(@"C:\x\test.ff", 0x800, 10, T0));
        Assert.Null(r.Process(null, 0x800, 10, T0));
    }

    [Fact]
    public void Event_text_shows_the_sound_name()
    {
        var ev = new ReadInterpreter(Index()).Process("test.sabs", 0x800, 0x1000, T0)!;
        Assert.Contains("a.wav", ev.ToString());
        Assert.Contains(@"sound\a.wav", ev.ToString());
        Assert.Contains("0x0000000C", new ReadInterpreter(Index()).Process("test.sabs", 0x14000, 0x100, T0)!.ToString());
    }

    /// <summary>Against the real game banks: every sound's first and last byte map back to that sound.</summary>
    [SkippableFact]
    public void Real_banks_map_every_sound_to_itself()
    {
        var folder = Environment.GetEnvironmentVariable("SOUNDSTUDIO_BO2_SOUND")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"Plutonium\Black_Ops_II\pluto_t6_full_game\sound");
        Skip.IfNot(Directory.Exists(folder), "BO2 sound folder not found.");
        var index = BankIndex.Load(folder);
        Assert.NotEmpty(index.Banks);
        foreach (var bank in index.Banks)
            foreach (var e in bank.Entries)
            {
                Assert.Same(e, bank.Find(e.Offset));
                Assert.Same(e, bank.Find(e.End - 1));
            }
    }
}
