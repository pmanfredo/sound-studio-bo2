# Sound Studio (Black Ops II)

A modern rebuild of master131's *Black Ops II Sound Studio* for browsing, replacing, recording
and sharing Black Ops II (PC) sounds.

- **Library**: every `.sabs`/`.sabl` bank in one window, grouped by map, searchable, with modified sounds flagged.
- **Listen**: instant playback, waveform, A/B against the original or a staged replacement, WAV export.
- **Replace**: drag a file onto a sound, or a whole folder (matched by name). Changes are staged, then applied.
  Long replacements are flagged (they can make levels load forever) with one-click trimming.
- **Record**: record your own version with your microphone, auto-trimmed and normalized.
- **Undo**: originals are saved before the first change; revert any sound at any time.
- **Share**: host or join a friend; applied changes sync over your home network, Tailscale, or the internet
  (Cloudflare quick tunnel or your own address).

## Install
Download `SoundStudioApp-win-Setup.exe` from [Releases](../../releases/latest) and run it (no admin needed).
Windows may show "Windows protected your PC" because the installer isn't code-signed: choose **More info → Run anyway**.

Sound Studio then updates itself: when a new version is out it downloads in the background and offers
**Restart to update**. Your data (saved originals, history, recordings) lives in `%LocalAppData%\SoundStudio`
and is kept across updates and uninstalls.

Close Black Ops II before applying changes; the game locks its sound files while running.

## Build and release
Requires the .NET 8 SDK and `vpk` (`dotnet tool install -g vpk`).

```powershell
dotnet test                                   # real-bank tests use your game folder (SOUNDSTUDIO_BO2_SOUND to override)
./tools/release.ps1 -Version 1.0.1 -UpdateSource https://github.com/<you>/sound-studio-bo2
vpk upload github --repoUrl https://github.com/<you>/sound-studio-bo2 --token <token> --outputDir artifacts/releases --publish --releaseName "Sound Studio 1.0.1" --tag v1.0.1
```

Keep `artifacts/releases` between versions so updates are small deltas.

## Layout
- `src/SoundStudio.Core`: BO2 bank format (read, replace, safe save, modification detection), FLAC codec,
  originals store, change history, library scanner.
- `src/SoundStudio.Audio`: decoding, playback, conversion, recording (Windows).
- `src/SoundStudio.Sync`: Host-mode server, sync client, invite codes, Tailscale/Cloudflare reachability.
- `src/SoundStudio.App`: the WPF app.
- `tools/SabInspect`: read-only survey of a sound folder (`--mismatches` lists modified sounds).
- `tests/SoundStudio.Core.Tests`: includes a byte-for-byte round-trip of every bank in the game folder.

## How "modified" is detected
Each bank stores an MD5 per sound. Saving keeps the *original* MD5, so any sound whose audio no longer matches
its stored MD5 has been replaced, even without a backup or this app's history.

## Credits
Based on Black Ops II Sound Studio by master131 (MIT) and the Extended fork by tovaru.
The sound-name database (`Resources/Names.deflate`) comes from that project. See `THIRD-PARTY-NOTICES.md`.
Not affiliated with Activision or Treyarch.
