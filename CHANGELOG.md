# Changelog

Beamcast is a study project. See the README for the full notice.

## 1.0.0

- First study build: broadcast a monitor or window to anyone with the invite code.
- GPU pipeline: Windows.Graphics.Capture stays on the GPU, the D3D11 video processor converts to
  NV12, and the hardware H.264/HEVC encoder (Media Foundation: AMD, NVIDIA, Intel, D3D12) encodes
  with a low-latency profile. Viewers decode with DXVA straight into a SwapChainPanel.
- VP8 on the CPU as a fallback for machines without a hardware encoder.
- TCP transport with per-viewer back-pressure and keyframe recovery; encoder is recreated when a
  driver ignores the keyframe request.
- Password-protected rooms with challenge/response (the password never leaves the machine).
- Quality presets up to Source/2160p, 15–120 fps, bitrate and cursor controls, codec selector.
- Live preview, pause/resume, viewer list, and stream stats on both ends.
- Viewer fullscreen (double-click or the button, Esc to leave).
- Study-only notice on first launch and on the About page.
- Automatic updates through Velopack and GitHub Releases.
- pt-BR and English UI.
