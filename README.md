<p align="center">
  <img src="src/Beamcast/Assets/Beamcast.png" width="120" alt="Beamcast" />
</p>

<h1 align="center">Beamcast</h1>

<p align="center">
  <strong>A study project on screen capture, video codecs and real-time streaming on Windows.</strong><br />
  Not a product. Built to learn, to measure, and to serve as a case study for a separate enterprise application.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-study%20project-FF4D6D?style=flat-square" alt="Study project" />
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?style=flat-square&logo=windows&logoColor=white" alt="Windows 10 / 11" />
  <img src="https://img.shields.io/badge/WinUI-3-59C8C8?style=flat-square" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 8" />
</p>

---

## Read this first

**Beamcast exists for study only.** It was written to explore how screen capture, video
encoding (VP8 today, H.264 next) and a small streaming protocol fit together on Windows, and
to produce findings and reference code for a separate, private, enterprise application. Every
design decision here optimises for learning and measurement, not for production use.

- **No warranty, no liability.** The software is provided "as is". The author takes no
  responsibility for anything that happens from running, distributing or building on it.
- **Any other use is your own responsibility.** If you use Beamcast for anything beyond
  studying the code, you do so at your own risk and you are responsible for complying with
  whatever laws, policies and consent requirements apply to you. Screen sharing exposes
  whatever is on your screen; think before you go live.
- **No support, no roadmap promises.** Issues may go unanswered; APIs, the wire protocol and
  the file layout can change at any time.
- **Not hardened.** Traffic is plain TCP (the room password never travels in clear text, but
  the video does). Do not expose it to the internet unless you understand what that means.

The same notice is shown inside the app on first launch and on the About page.

## What the study covers

- Capturing a monitor or a window with the Windows Graphics Capture API and reading frames
  back from Direct3D 11.
- Scaling frames on the CPU and encoding them with VP8 (libvpx via `SIPSorceryMedia.Encoders`),
  with measurements of encode cost per resolution.
- A minimal length-prefixed TCP protocol with a challenge/response password, keyframe
  recovery and per-viewer back-pressure, so one slow viewer never stalls the others.
- Presenting decoded frames through a `WriteableBitmap` in WinUI 3, including fullscreen.
- Packaging and auto-update with Velopack and GitHub Releases.

Findings so far (Ryzen-class desktop, software VP8):

| Output size | Encode time per frame |
| ----------- | --------------------- |
| 1280×720    | ~16 ms                |
| 2560×1080   | ~35 ms                |

## How it works

```
Windows.Graphics.Capture ─► BGRA frame ─► scale to preset ─► VP8 (libvpx) ─► TCP fan-out
                                                                               │
                                        WriteableBitmap ◄─ BGRA ◄─ VP8 decode ◄┘ (each viewer)
```

The broadcaster hosts a small TCP server; viewers connect directly using an invite code that
carries address, port and password. There is no account and no relay server.

## Build from source

Requirements: Windows 10 1809+ (Windows 11 recommended), .NET 8 SDK.

```powershell
dotnet build Beamcast.sln -c Release
dotnet test tests/Beamcast.Tests
dotnet run --project src/Beamcast
```

`scripts/pack.ps1` publishes a self-contained build and wraps it with Velopack (`vpk`) into an
MSI under `artifacts/release`. The GitHub workflows tag `main` from the csproj version and
publish the release; the app checks GitHub Releases for updates on launch.

## Project layout

```
src/Beamcast
├── Capture/    Windows.Graphics.Capture + D3D11 readback, source enumeration
├── Codec/      VP8 wrapper, frame types, bilinear scaler
├── Net/        protocol, host server, viewer client, invite codes, auth
├── Services/   BroadcastService, WatchService, UpdateService
├── Pages/      Broadcast, Watch, Settings, About
└── Controls/   VideoView (WriteableBitmap surface)
tests/Beamcast.Tests   xunit tests for the pure-logic parts
```

## Next experiments

- System audio (WASAPI loopback + Opus).
- Hardware H.264 through Media Foundation, compared against software VP8.
- An optional relay so nobody needs to forward ports.
