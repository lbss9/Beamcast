## 2.1.8

### Fixes

- The host's "⋯" button on the Rooms screen did not open its menu: the click also selected the host and the list was rebuilt before the menu could show. The menu opens normally now.

## 2.1.7

### New

- The app key now belongs to each host. In the host's "⋯" menu (or right-clicking it), "App key…" lets you enter the key that server requires. A key icon shows next to the host when a key is saved.
- When a host refuses the app for lack of a key, the Rooms screen shows an "Enter app key" button right under the notice. Adding a host that requires a key asks for it right away.
- The "Server" section is gone from Settings: hosts and keys live only on the Rooms screen.

### Fixes

- A key typed in Settings was not used for a host already in the list, so the host kept refusing the app. Each host keeps its own key now.

### Security

- The app key is protected on disk for your Windows account (DPAPI), like remembered room passwords. Keys saved by earlier versions are converted on first launch.

## 2.1.6

### Improvements

- New update window: shows the current and the new version, what changes by category, the download size and progress, with a single "Install and restart" button.
- Release notes in Portuguese and English, following the app language.

## 2.1.5

### Fixes

- On Windows 10, picking a window to broadcast failed with "interface marshalled for a different thread" and the broadcast ended. Window capture is now set up the right way for that Windows.

### Improvements

- Capture failures log the exact step to `diag.log`, which speeds up diagnosis.

## 2.1.4

### Fixes

- Picking a window, resizing it or switching from a screen to a window while live no longer ends the broadcast. The stream keeps the resolution chosen at start; frames of another shape get black bars.

## 2.1.3

### Fixes

- The yellow border around the shared screen no longer appears on any Windows 10 or 11 version: whole-screen capture now uses a different Windows technology (Desktop Duplication) that draws no such border.
- Stopping the broadcast ends the capture immediately. Nothing of the screen is read after "Stop".

## 2.1.2

### Fixes

- Removed the "Esc" tooltip that showed up when the mouse rested anywhere on the window.
- First attempt at removing the yellow border (Windows 11); completed in 2.1.3.

## 2.1.1

### Fixes

- Automatic updates fixed. Earlier versions published only the installer, without the feed the app checks, so no installation ever received updates. From here on new versions arrive on their own.

### Improvements

- The update window shows download progress and resumes an already downloaded update.

## 2.1.0

### New

- Rooms screen: favorite hosts, the host's public rooms, favorite rooms and joining by code or invite.
- Public or private rooms, permanent or temporary, with optional password, member limit and owner-only broadcasting.
- Room owner: edit the room, change or remove the password, create invites with expiry and uses, revoke invites, kick members and delete the room.
- Automatic reconnection: when the internet drops, the app comes back on its own, republishes your stream and resumes what you were watching.

### Security

- Everything stays end-to-end encrypted, including rooms without a password: members hand the key to each other and the server never sees it.
- Wrong password, invite or code attempts are rate limited per address and per room.

## 2.0.1

### Fixes

- Members that vanish without leaving are dropped within 30 seconds even behind a tunnel, and their streams end for everyone.

## 2.0.0

### New

- Self-hosted lounge: run the server with `docker compose up -d --build`, type its address in the app, create a lounge with a password or join with code and password.
- In a lounge anyone can broadcast (several at once) and everyone picks what to watch; stop watching without leaving.
- Discord-style audio: sharing the screen sends app audio minus voice calls; sharing a window sends only that app.
- Viewer volume and mute; stream title.

### Security

- Everything end-to-end encrypted with the lounge password; the server never sees the password, names, video or audio.

## 1.1.0

### New

- Broadcast over the internet through the Beamcast server: no ports to open, works behind CGNAT.
- Video and audio end-to-end encrypted; not even the server can see them.
- Invite code generated per session; direct mode stays available for the same network.
- Settings: server address and app key.

## 1.0.0

### New

- First study build (GPU pipeline): broadcast a monitor or window to anyone with the invite code.
- H.264/HEVC hardware-encoded video (VP8 on the CPU as fallback) with per-viewer back-pressure and keyframe recovery.
- Password-protected rooms (the password never travels in clear text).
- Quality presets, fps, bitrate and cursor controls; live preview; pause and resume.
- Viewer fullscreen and stats on both ends.
- Automatic updates through GitHub Releases.
