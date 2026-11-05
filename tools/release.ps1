<#
.SYNOPSIS
  Builds a Sound Studio release: tests, self-contained publish, bundled ffmpeg, and a Velopack installer
  with automatic updates.

.EXAMPLE
  ./tools/release.ps1 -Version 1.0.0
  ./tools/release.ps1 -Version 1.0.1 -UpdateSource https://github.com/you/sound-studio

.DESCRIPTION
  Output goes to artifacts/releases:
    SoundStudioApp-win-Setup.exe     give this to people (one-time install, no admin needed)
    SoundStudioApp-win-Portable.zip  run-from-a-folder alternative
    *.nupkg + releases.win.json      the update feed: upload these to your update location
  Keep artifacts/releases between versions so Velopack can build small delta updates.

  -UpdateSource is baked into the app: installed copies check it for updates on startup.
    GitHub:   https://github.com/<you>/<repo>   (upload with: vpk upload github ...)
    Any web folder that serves the files in artifacts/releases (e.g. https://sounds.example.com/updates)
  Use the same value for every release.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $UpdateSource = "",
    [string] $FfmpegPath = "$env:USERPROFILE\Downloads\v1.6.1-windows\ffmpeg\bin\ffmpeg.exe",
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
$releases = Join-Path $root "artifacts\releases"
$packId = "SoundStudioApp"   # install folder %LocalAppData%\SoundStudioApp (app data lives separately in %LocalAppData%\SoundStudio)

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $root "SoundStudio.sln") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not releasing." }
}

Step "Publishing $Version (self-contained, win-x64)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\SoundStudio.App\SoundStudio.App.csproj") -c Release -r win-x64 --self-contained `
    -p:Version=$Version -p:UpdateSource=$UpdateSource -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Step "Bundling ffmpeg (for ogg/opus/video inputs)"
if (Test-Path $FfmpegPath) {
    $ff = New-Item -ItemType Directory -Force (Join-Path $publish "ffmpeg")
    Copy-Item $FfmpegPath $ff
    $license = Join-Path (Split-Path (Split-Path $FfmpegPath)) "LICENSE.txt"
    if (Test-Path $license) { Copy-Item $license (Join-Path $ff "LICENSE.txt") }
} else {
    Write-Warning "ffmpeg not found at $FfmpegPath; WAV/MP3/FLAC/M4A still work, OGG and others won't."
}
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $publish

Step "Packing installer"
New-Item -ItemType Directory -Force $releases | Out-Null
vpk pack --packId $packId --packVersion $Version --packDir $publish --mainExe SoundStudio.exe `
    --packTitle "Sound Studio" --packAuthors "Sound Studio" --icon (Join-Path $root "src\SoundStudio.App\Assets\app.ico") `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Step "Done"
Get-ChildItem $releases | Sort-Object LastWriteTime -Descending | Select-Object -First 6 Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table
if ($UpdateSource) {
    Write-Host "Upload the new files in $releases to $UpdateSource so installed copies update." -ForegroundColor Yellow
} else {
    Write-Host "No -UpdateSource given: this build won't check for updates." -ForegroundColor Yellow
}
