# Third-party notices

## Black Ops II Sound Studio
Portions (file-format knowledge, sound-name database) derived from https://github.com/tovaru/Black-Ops-II-Sound-Studio-Extended

MIT License

Copyright (c) 2019 master131

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Bundled and used components
- **FFmpeg** (bundled in `ffmpeg/`, used only to read uncommon input formats such as OGG/Opus/video). Licensed under the GNU GPL v3; see `ffmpeg/LICENSE.txt`. Build from https://ffmpeg.org (Zeranoe 4.1 build, as shipped with Black Ops II Sound Studio).
- **NAudio** (MIT), **WPF-UI** (MIT), **CommunityToolkit.Mvvm** (MIT), **Velopack** (MIT), **Microsoft .NET / ASP.NET Core** (MIT).
- **cloudflared** is *not* bundled; if installed, Sound Studio runs it to create Cloudflare quick tunnels (Apache 2.0).
