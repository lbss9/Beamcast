## 2.1.2

- The yellow border around the shared screen or window is gone (Windows 11; older Windows 10 builds draw it themselves and cannot remove it).
- The stray "Esc" tooltip shown when hovering the window is gone.

## 2.1.1

- Automatic updates fixed: releases now publish the update feed, so the app receives new versions on its own.
- The update window shows download progress and resumes an already downloaded update.

## 2.1.0

- New Rooms screen: favorite hosts, the host's public rooms, favorite rooms and join by code or invite.
- Public or private rooms, permanent or temporary, optional password, member limit and owner-only broadcasting.
- Room owner: edit, change the password, create invites with expiry and uses, kick and delete the room.
- Automatic reconnection: when the internet drops, the app comes back on its own, republishes your stream and resumes what you were watching.
- Everything stays end-to-end encrypted, including rooms without a password (members hand the key over).

## 2.0.1

- Members that vanish without leaving are dropped within 30 s even behind a tunnel; their streams end for everyone.

## 2.0.0

- Self-hosted lounge: run the server with `docker compose up -d --build`, type its address in the app, create a lounge with a password or join with code and password.
- In a lounge anyone can broadcast (several at once) and everyone picks what to watch; stop watching without leaving.
- Everything end-to-end encrypted with the lounge password; the server never sees the password, names, video or audio.
- Discord-style audio: sharing the screen sends app audio minus voice calls; sharing a window sends only that app.
- Viewer volume and mute; stream title.

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
