## 1.1.0

- Broadcast over the internet through the Beamcast server: no ports to open, works behind CGNAT.
- Video and audio end-to-end encrypted; not even the server can see them.
- Invite code generated per session; direct mode stays available for the same network.
- Settings: server address and app key.

## 1.0.0

- First study build (GPU pipeline): broadcast a monitor or window to anyone with the invite code.
- H.264/HEVC hardware-encoded video (VP8 on the CPU as fallback) over TCP with per-viewer back-pressure and keyframe recovery.
- Password-protected rooms (the password never travels in clear text).
- Quality presets, fps, bitrate and cursor toggle; live preview; pause and resume.
- Viewer fullscreen and stats on both ends.
- Automatic updates from GitHub Releases.
