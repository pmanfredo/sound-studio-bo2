# Sound Spotter: which sound is the game playing right now?

Research spike, November 2026. The goal is a "Live: now playing" list in Sound Studio that shows sound names
while you play Black Ops II. The hard limit: nothing may inject into the game, read its memory, or change its
files, because Plutonium has anti-cheat.

Prototype: `tools/SoundSpotter` (console app). Tests: `tests/SoundSpotter.Tests`.

## Summary

| | File-read tracing (ETW) | Audio fingerprinting |
|---|---|---|
| What it sees | which bank entry the game reads from disk | the audio the game outputs |
| Coverage | `.sabs` only: **81% of sounds, 90% of audio time** | any sound, in principle |
| Accuracy | exact name, as long as the game reads per sound (to be confirmed in a real run) | 98% for a clean window; **3% if the pitch is shifted 3%**; 72–88% in top 3 with 1–2 overlapping sounds |
| Timing | when the read happens, which is probably slightly before you hear it | about 1.5 s after the sound starts |
| Admin | **yes** (kernel trace session) | no |
| Touches the game process | no | no |
| Cost | small: about 4 MB table index, loads in under a second | about 150 MB index, about 3 min build (single thread) |
| Prototype status | built: `trace`, `selftest`, `replay` | offline benchmark only (`fpbench`) |

**Recommendation: build the panel on ETW file-read tracing.** It gives exact names with no guessing, and
it is cheap. Fingerprinting is clearly weaker in real gameplay. Its two failure modes, overlapping sounds
and pitch variance, are exactly what BO2 combat sounds like. It is only worth adding later, as a fallback
for the 19% of sounds that live in `.sabl` banks.

**Not yet verified:** the live trace has not run against the game. The tracer is unit-tested and its
offset mapping passes against all 116 real banks. But whether Plutonium reads `.sabs` once per sound, and
how early it reads, can only be seen in a real run. See [What the first real run should tell us](#what-the-first-real-run-should-tell-us).

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
   - `play`: the read starts at the sound's first byte (within 4 KB).
   - `play~`: the read lands mid-sound, and that sound has not been read recently.
   - `cont`: a later chunk of a sound that is already streaming. Hidden by default.
   - `bulk`: one read covers more than 8 sounds, e.g. a whole `.sabl` loading with the level.
   - `meta`: a read of the header or tables.
4. The output shows the name from `SoundNameDatabase`, the full asset path, the duration and the bank.
   Illustrative format (not captured from a real run):

```
20:14:03.512  [play ] wpn_ar_m27_fire_plr_01.wav                       0.62s  wpn_mp.sabs #1734  (64 KB read)
               devraw\sound\wpn\ar\m27\wpn_ar_m27_fire_plr_01.wav
```

The tracer only reads trace events that Windows already produces. It opens no handle to the game, reads
none of its memory, and loads nothing into it. It belongs to the same class of tool as Process Monitor,
Resource Monitor's disk tab, or Windows Performance Recorder.

### Coverage and limits

- **Streamed `.sabs` sounds (20,700 of 25,446; 81% of sounds, 90% of audio time):** a streamed sound has
  to be read from the file when it plays, so the read shows which sound it is. Most voice-over,
  announcer, music and ambience lives here.
- **Loaded `.sabl` sounds (4,746; 19%):** these are read into memory in bulk when a map or mode loads, so
  their reads do not show when they play. The tool still reports which `.sabl` banks loaded, which narrows
  "what could be playing" down to that map's set.
- **Repeat plays:** ETW's FileIo events are logged above the file cache, so cached re-reads should still
  appear. That only holds if the game re-reads each time. If its streaming engine keeps recently played
  sounds in its own RAM cache, a second play within a short time could be invisible. The run will show
  whether this happens.
- **Timing:** streaming engines read ahead, so a `play` line probably appears a little before the sound is
  heard. That suits a "now playing" list. Real-time ETW delivers events in batches, roughly once a second
  at worst, so the panel will lag by up to about 1 s. `selftest` prints the measured delay.
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

### What the first real run should tell us

This is what the prototype cannot know until it runs next to the game. Run these in an **elevated**
terminal (Run as administrator), from the repo folder:

```bash
dotnet run -c Release --project tools/SoundSpotter -- selftest
```

Expected output: `PASS: traced and named …` plus the event delay in ms. This proves tracing works on the
PC without the game.

```bash
dotnet run -c Release --project tools/SoundSpotter -- trace --raw > spotter-raw.txt
```

Play for a few minutes, press Ctrl+C, and send the summary at the end of the file. It answers:

1. **Per-sound reads?** If `play` lines appear when sounds play, yes. If only `bulk` lines appear for
   `.sabs`, the game reads large ranges, and the tracer will need a time-and-position heuristic.
2. **Memory-mapped?** A non-zero `hard faults on banks` count means the game maps the files. The tracer
   already turns each faulted page into a read, but those events only fire on the first touch.
3. **Read size:** the read-size histogram shows the streaming chunk size, for tuning the `StartSlack` and
   `IdleGap` settings.
4. **Which process:** the `process:` lines give the exact exe name to pass to `--process`. It is expected
   to be `plutonium-bootstrapper-win32`.

Start the trace **before** launching the game if you can. It works either way, but a session that is
already running sees the game's file opens directly instead of relying on the rundown.

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
   - Show `.sabl` bulk loads as one line ("zm_tomb.sabl loaded: 412 sounds").
   - Tune `StartSlack` and `IdleGap` from the first real run.
   - Handle the helper being declined at the UAC prompt, and the trace session already existing after a
     crash (the session has a fixed name, so restarting replaces it).
5. Fingerprinting: not now. If the `.sabl` gap matters later, process-loopback capture plus this matcher
   could tag only `.sabl` candidates for the current map. That is a far smaller index (one map's
   `.sabl` set, a few hundred sounds), which makes the matcher much more accurate.

## Files

- `tools/SoundSpotter/BankIndex.cs`: offset → sound lookup per bank (binary search).
- `tools/SoundSpotter/ReadInterpreter.cs`: turns reads into play, continue, bulk and metadata events.
  Pure logic.
- `tools/SoundSpotter/FileReadTracer.cs`: the ETW session, `.etl` replay, and the summary statistics.
- `tools/SoundSpotter/Fingerprinter.cs`: the landmark fingerprinter and index used by `fpbench`.
- `tools/SoundSpotter/Program.cs`: the `stats`, `trace`, `selftest`, `replay` and `fpbench` commands.
- `tests/SoundSpotter.Tests`: 27 tests, including a check that every sound in every real bank maps back to
  itself. That check is skipped when the game folder is missing.
