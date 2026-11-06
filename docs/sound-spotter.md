# Sound Spotter: which sound is the game playing right now?

Research spike, November 2026. The goal is a "Live: now playing" list in Sound Studio that shows sound names
while you play Black Ops II. The hard limit: nothing may inject into the game, read its memory, or change its
files, because Plutonium has anti-cheat.

Prototype: `tools/SoundSpotter` (console app). Tests: `tests/SoundSpotter.Tests`.

## Summary

| | File-read tracing (ETW) | Audio fingerprinting |
|---|---|---|
| What it sees | which bank entry the game reads from disk | the audio the game outputs |
| Coverage | `.sabs` only: **81% of sounds, 90% of audio time**: music, voice-over, announcer, ambience. Not weapons, footsteps or impacts, which load from `.sabl` | any sound, in principle |
| Accuracy | **exact name. Confirmed in a real run:** each play starts with a read at the sound's first byte | 98% for a clean window; **3% if the pitch is shifted 3%**; 72–88% in top 3 with 1–2 overlapping sounds |
| Timing | when the first chunk is read, which is just before you hear it; events arrived about 3 s late with default ETW buffers (since reduced, to be re-measured) | about 1.5 s after the sound starts |
| Admin | **yes** (kernel trace session) | no |
| Touches the game process | no | no |
| Cost | small: about 4 MB table index, loads in under a second | about 150 MB index, about 3 min build (single thread) |
| Prototype status | built and run against the game: `trace`, `selftest`, `replay`, `reinterpret` | offline benchmark only (`fpbench`) |

**Recommendation: build the panel on ETW file-read tracing.** A real game session confirmed that it gives
exact names for every streamed sound, with no guessing, and it is cheap.

The catch the run revealed: the moment-to-moment combat sounds are in `.sabl` banks. Weapon shots, dry
fire, footsteps, cloth, impacts and riot-shield hits are all read into memory at level load. So the panel
will name music, voice-over, announcer lines and ambience live, but not gunfire.

For those combat sounds, the tracer still reports exactly which `.sabl` sounds the current map loaded.
That is a few hundred to about 1,500 candidates, not 25,000. A fingerprint matcher limited to that set is
the only route to naming combat sounds. Pitch variance remains its weak point.

The real-run results are in [Results from a real session](#results-from-a-real-session).

## The library

`SoundSpotter stats`:

```
.sabs (streamed):  66 banks,  20700 sounds,   4264 MB,  21.2 h audio, named 100%
    size median 104 KB, p90 257 KB, offsets 0x800-aligned: 0%
.sabl (loaded)  :  50 banks,   4746 sounds,    957 MB,   2.4 h audio, named 98%
    size median 118 KB, p90 456 KB, offsets 0x800-aligned: 1%
total 25446 sounds; streamed = 81.3% of sounds, 89.9% of audio time
ids present in more than one bank: 670
```

Sound offsets are **not** sector-aligned. If the game rounds a read down to a 4 KB boundary, the read
starts in the tail of the previous sound. The interpreter handles this: it names the sound the read covers
most, not the sound under its first byte. A test covers this case.

## Approach 1: kernel file-I/O tracing (ETW)

### How it works

1. On start, `BankIndex.Load` reads each bank's entry table once (Offset, Size, Id, duration) and closes
   the files again. It never holds the files open while the game runs.
2. `FileReadTracer` starts a private real-time kernel ETW session (`SoundSpotter-FileIO`, using
   Microsoft.Diagnostics.Tracing.TraceEvent 3.1.16). It turns on these kernel event groups:
   - `FileIO`: every read, with file name, byte offset and size.
   - `FileIOInit` and `DiskFileIO`: map file objects to file names, including a rundown of files opened
     before the session started.
   - `MemoryHardFaults`: if the game memory-mapped the banks instead of reading them, this would show it.
   - `Process`: process names.
3. Each read of a `.sabs` or `.sabl` file goes through `ReadInterpreter`, which labels it:
   - `play`: a `.sabs` read starts at the sound's first byte (within 4 KB).
   - `play~`: the read lands mid-sound, and that sound has not been read recently.
   - `cont`: a later chunk of a sound that is already streaming. Hidden by default.
   - `loop`: the sound restarts right after a later chunk reached its end. Ambience and music do this.
     Hidden by default.
   - `load`: a `.sabl` sound being read into memory. Printed as one line per bank per load burst.
   - `bulk`: one read covers more than 8 sounds.
   - `meta`: a read of the header or tables.
4. The output shows the name from `SoundNameDatabase`, the full asset path, the duration and the bank.
   Real output from Nightclub (via `reinterpret`):

```
19:23:04.471  [play ] vox_isa_tdm_start_00.SN65.pc.snd                   1.38s  mpl_code_post_gfx.english.sabs #4438  (75 KB read)
               raw\english\sound\vox\scripted\isa\vox_isa_tdm_start_00.SN65.pc.snd
19:23:04.472  [play ] mus_mp_spawn_cia_10sec.SN65.pc.snd                 9.05s  mpl_common.all.sabs #84  (128 KB read)
               raw\sound\mus\mp\spawn\mus_mp_spawn_cia_10sec.SN65.pc.snd
```

The tracer only reads trace events that Windows already produces. It opens no handle to the game, reads
none of its memory, and loads nothing into it. It belongs to the same class of tool as Process Monitor,
Resource Monitor's disk tab, or Windows Performance Recorder.

### Coverage and limits

- **Streamed `.sabs` sounds (20,700 of 25,446; 81% of sounds, 90% of audio time):** read from disk at the
  moment they play, so each play is visible. This is music, voice-over, announcer and ambience.
- **Loaded `.sabl` sounds (4,746; 19%):** read into memory at level load. Their reads do not show when they
  play. They are few by count, but they are the frequent combat sounds: weapons, footsteps, impacts. The
  tool reports exactly which ones the current map loaded.
- **Repeat plays:** confirmed visible. The game re-reads a `.sabs` sound every time it plays: the PA
  announcement, the music track and the shore loops all showed up again.
- **Timing:** the first chunk is read as the sound starts. Real-time ETW delivers events in batches,
  though. The selftest measured **3.2 s** from read to event with the default 64 KB buffers. The buffers
  are now 8 KB, which should cut this; re-run `selftest` to measure.
- **Unnamed sounds:** a few streamed sounds have no entry in the name database and show as an id, e.g.
  `0x72C3992A` in `cmn_root.all.sabs` (66 s long) and `0x2C5578BA` in `mpl_code_post_gfx.all.sabs`
  (72 s long).
- **Sounds in several banks:** 670 ids appear in more than one bank. The tracer reports the bank actually
  read, so there is no ambiguity.

### Admin and anti-cheat risk

- **Admin is required.** A kernel ETW session cannot be started without elevation. Sound Studio itself
  should **not** run elevated (it writes game files and runs the sync server). See [Integrating it into the app](#integrating-it-into-the-app).
- **Anti-cheat risk: low, but not zero, and nobody can promise it.** Nothing here does what anti-cheats
  look for: no handle to the game process, no memory reads, no injected DLL, no driver, no change to game
  files. Windows itself, Resource Monitor and WPR use the same kernel trace facility. Plutonium does not
  publish what its client checks, so I can't guarantee it never flags tools that run alongside the game.
  A careful first test is a private match or a solo zombies game.

### Results from a real session

The run was a trace from the main menu into a Nightclub match, about 3.5 minutes in total, with the trace
started before the game.

- `selftest`: **PASS**. It traced and named `air_dank_heavy.SL55.pc.snd`. The event arrived 3,159 ms after
  the read.
- **Process:** every bank access came from `plutonium-bootstrapper-win32`. Use `--process plutonium` to
  ignore everything else.
- **Opening a bank:** the game opens each bank, reads its 2 KB header, then reads the entry table at the
  end of the file. This shows as `meta` lines.
- **`.sabs` streaming (per sound, lazy, exact):**
  - Every play began with a 128 KB read at **exactly** the sound's first byte (`+0`). Sounds under 128 KB
    are read in one go.
  - Long sounds read two chunks at once (a 256 KB prebuffer), then one 128 KB chunk about every
    0.8–1.3 s, ending with a short final chunk.
  - There were 21 start reads. They split into 16 plays and 5 loop restarts: shore wind left/right, plus
    the PA announcement restarting.
  - The plays were menu music, two licensed tracks on the club speakers, the PA voice-over, "TDM start"
    and "boost" announcer lines, a spawn music sting, a strike-force music stinger and ambience.
- **`.sabl` loading:** 1,463 reads, **one read per sound**, in bursts during level load (19:21:13–14 and
  19:22:41–42). They included `wpn_peacekeeper_shot_plr`, `dry_fire_00`, `shot_00`–`_02`, footstep
  surfaces (`wood_`, `water_`, `dirt_`, `metal_`, `snow_` …), `fly_cloth_*` and `riot_shield_impact*`.
  None of these is read again when it plays.
- **No memory mapping:** there were no hard-fault lines.
- **Summary missing:** with `> spotter-raw.txt`, PowerShell closed the output pipe on Ctrl+C before the
  summary was printed. Use `--log` instead (below).

These findings changed the interpreter:

- `.sabl` reads are now `load` events, grouped per bank and burst, instead of being counted as plays.
- Loop restarts are labelled `loop`, so ambience doesn't flood the list.
- `reinterpret <raw.txt>` re-runs the rules over a saved raw log, so tuning doesn't need another play
  session.

### How to run it

In an **elevated** terminal (Run as administrator), from the repo folder. First check that tracing works,
without the game:

```bash
dotnet run -c Release --project tools/SoundSpotter -- selftest
```

Then live, ideally started before launching the game. Ctrl+C stops it, and the summary goes into the log:

```bash
dotnet run -c Release --project tools/SoundSpotter -- trace --process plutonium --log spotter.txt
```

Add `--raw` to log every read with its offset and size, or `--verbose` to also show loops, metadata reads
and each loaded `.sabl` sound.

If you would rather not run the tool itself while playing, use Windows' built-in recorder instead (also
needs admin) and replay the trace afterwards:

```bash
wpr -start FileIO -filemode
```

```bash
wpr -stop bo2.etl
```

```bash
dotnet run --project tools/SoundSpotter -- replay bo2.etl
```

## Approach 2: audio fingerprinting

### Capture: not built

Windows 10 2004 and later can capture one process's audio output ("process loopback") through
`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`. It needs no admin and
captures only the game's mix: no Discord, no music. NAudio 2.2.1 does not wrap this, so it would take
about 150 lines of COM interop (activation params, completion handler, an `IAudioClient` in event mode).
Like ETW, it never touches the game process. I did not build it, because the matcher it would feed tested
too weak (below).

### Matcher: built and benchmarked offline

`Fingerprinter` is a minimal Shazam-style landmark matcher:

- Audio is turned into mono at 11,025 Hz.
- It computes a 1024-point FFT every 256 samples (about 23 ms).
- It keeps one spectral peak per band per frame, when that peak stands out from its surroundings.
- It pairs each peak with up to 5 later ones within about 1.5 s, giving hashes of (f1, f2, dt).
- At query time, it finds the sound with the most hashes that agree on one time offset.

`SoundSpotter fpbench --sounds 4000 --queries 200`: random real sounds, each query a 1.5 s window
damaged the way a game mix would damage it:

```
indexed 4000 sounds, 222.0 min audio: 2543170 landmarks (191/s of audio)
  decode 13.3s, fingerprint 11.0s (single thread)
  extrapolated to whole library (23.6 h): ~155 MB compact index, ~3 min single-thread build
  clean window                             top-1   98%   in top-3   99%   1.6 ms/query
  half volume + noise (SNR 10 dB)          top-1   96%   in top-3   98%   1.6 ms/query
  +1 other sound, equal level              top-1   28%   in top-3   88%   1.5 ms/query
  +2 other sounds, equal level             top-1   18%   in top-3   72%   1.5 ms/query
  +3 others, target 6 dB quieter           top-1    4%   in top-3   24%   1.6 ms/query
  pitch/speed +3% (alias pitch variance)   top-1    3%   in top-3    4%   1.3 ms/query
  lowpass (distance / occlusion)           top-1   99%   in top-3   99%   1.7 ms/query
```

How to read the overlap rows: the other sounds in the mix are also in the index, so "top-1 is a different
sound" is often still a correct answer. "In top-3" is the fairer measure there.

### What this means in the game

- **Pitch variance is the big problem.** BO2 sound aliases randomize pitch on many sounds: weapons,
  footsteps, impacts. A 3% shift breaks landmark hashes almost completely. The usual fixes both cost a lot:
  - Index several pitch-shifted copies of every sound (about 5× the index and build time).
  - Switch to pitch-invariant features (constant-Q or chroma ratios), which is a research project of its
    own.
- **Overlap is normal, not an edge case.** In a firefight, 3–4 sounds at once is common, and that row
  falls to 24% in top 3.
- **Size and speed are fine.** About 155 MB as a compact on-disk index; the in-memory dictionary is
  several times larger and would need a sorted-array layout. A build takes about 3 min on one thread,
  about 1 min in parallel, and only once (then again only when banks change). A query takes about 2 ms.
- **Delay:** a match needs about 1–1.5 s of audio, so the panel always runs behind what you hear.
- **What it is good for:** long, clean, unpitched sounds such as music, ambience and announcer lines.
  Those mostly live in `.sabs`, so ETW already covers them better.

## Integrating it into the app

The plan for a "Live: now playing" panel on ETW. Effort is about 2–3 days.

1. **Elevated helper (0.5–1 day).** Move `FileReadTracer` into a small helper exe (or reuse
   SoundSpotter) that the app starts with `runas` (one UAC prompt) when the user opens the panel. It
   streams `SpotEvent`s as JSON lines over a named pipe that only the current user can access. The main
   app stays unelevated. The helper only needs the bank folder path and exits when the pipe closes.
2. **Core types (0.5 day).** Move `BankIndex`, `BankMap`, `ReadInterpreter` and `SpotEvent` (already free
   of ETW) into `SoundStudio.Core` with their tests. `BankIndex` can be built from the app's
   `GameLibrary` instead of re-reading the banks.
3. **Panel (1 day).** A scrolling list with time, name, bank and duration, plus pause/clear and a filter
   box. Clicking a row selects the sound in the library, so you can play or replace it. Pin and "export
   session" are cheap extras, and useful for mapping in-game moments to sounds.
4. **Edge cases (0.5 day).**
   - Show the "map loaded N combat sounds" line from the `.sabl` load bursts, as the tracer already does.
   - Hide `loop` restarts, and treat a sound as "playing" until its last chunk is read.
   - Handle the helper being declined at the UAC prompt, and the trace session already existing after a
     crash (the session has a fixed name, so restarting replaces it).
5. Fingerprinting: not now. Combat sounds (weapons, footsteps, impacts) are `.sabl`, so they need it.
   Process-loopback capture plus this matcher, limited to the `.sabl` sounds the tracer saw the current
   map load (about 1,500 in the Nightclub run), is a far smaller and more accurate index than 25,000
   sounds. BO2's pitch randomization on weapon sounds remains the open problem: expect about 3 extra
   days, with uncertain accuracy.

## Files

- `tools/SoundSpotter/BankIndex.cs`: offset → sound lookup per bank (binary search).
- `tools/SoundSpotter/ReadInterpreter.cs`: turns reads into play, continue, bulk and metadata events.
  Pure logic.
- `tools/SoundSpotter/FileReadTracer.cs`: the ETW session, `.etl` replay, and the summary statistics.
- `tools/SoundSpotter/Fingerprinter.cs`: the landmark fingerprinter and index used by `fpbench`.
- `tools/SoundSpotter/Program.cs`: the `stats`, `trace`, `selftest`, `replay`, `reinterpret` and `fpbench`
  commands.
- `tests/SoundSpotter.Tests`: 30 tests, including a check that every sound in every real bank maps back to
  itself. That check is skipped when the game folder is missing.
