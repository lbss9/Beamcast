## 2.9.4

### New

- **A sound when someone starts a broadcast in the room.** A short sound plays the moment another person goes live, even if you are on another screen of the app. Entering a room that already has streams stays quiet. You can turn it off, and hear a preview, in Settings under Appearance and behaviour.

## 2.9.3

### Fixes

- **The stream you are watching no longer closes on its own.** When the broadcaster's connection drops, the host ends the old stream and the person comes back with a new one. Your app used to close the tile at once and you had to click watch again. Now it holds the last picture, says "so-and-so lost connection, waiting for the stream to come back" and follows the new stream by itself, waiting up to 20 seconds.
- **Nobody shows up broadcasting twice.** When someone dropped and came back, the host still listed the old connection for up to 30 seconds and the same person appeared with two streams. Now the app identifies itself when it joins again and the host drops the stale connection at once. Needs the host on 2.7.0; on older hosts the duplicate still lasts those 30 seconds, but the stream you watch no longer closes.

## 2.9.2

### Improvements

- The Settings and What's new pages are centered in the window instead of hugging the left edge.

## 2.9.1

### Improvements

- **Complete diagnostic log.** With the log on (Settings → Diagnostics), `diag.log` now records everything the app does: every action of yours (entering and leaving a room, going live, pausing, stopping, watching and stopping watching, fullscreen, tabs, navigation, theme, language, window hidden), every state change of the lounge, the broadcast and the viewers, every reconnect with attempt and outcome, every member and stream that comes or goes, every subscription and keyframe request, the socket closing with the reason the host sent, the network pulse (round trip and clock offset) and a summary of the broadcast and per-viewer stats every 5 s. It is the groundwork for the open bugs "the stream closes only for me" and "the broadcaster shows up twice". Nothing changes with the log off.

## 2.9.0

### New

- **Broadcast profile** on the Broadcast tab: Manual, Recommended, Performance, Balanced and Quality. Picking a profile **measures** before adjusting: it encodes your screen for a moment at every candidate size to learn how long your GPU's encoder takes per frame and how many frames it sustains, and times an upload to the host. From those numbers it picks resolution, fps, bitrate and codec, shows the result with the measurements and fills the controls. Touching any control by hand goes back to Manual.
- **Recommended** picks among the three by itself, from what the card and the connection sustain: Quality when the machine holds 1080p60 (or 1440p) and the upload carries the bitrate; Balanced when it holds 1080p; Performance otherwise, and always without a hardware encoder.
- The upload measurement needs the host on 2.6.0; on older hosts the profile uses the encoder measurements only.

## 2.8.0

### New

- **Viewers report when they fall behind.** When the measured delay passes 0.4 s, the viewer's app tells the broadcaster every 2 s, and adaptive quality lowers the bitrate from that real number, not only from the upload queue. The broadcaster sees "viewer behind by X ms" in the stats.
- **A large delay turns into action.** Above 0.9 s the viewer asks the host to drop what is queued for it and send a full frame, at most every 3 s. The picture jumps to the present instead of staying behind.

Both need the host on 2.5.0; on older hosts nothing changes.

## 2.7.0

### New

- **Broadcast only with viewers** (Broadcast tab, on by default). While nobody watches, capture and preview keep running but the encoder and the upload stay idle: no video GPU or upload spent for nothing. When someone joins, the stream resumes at once with a full frame. A "WAITING FOR A VIEWER" badge shows next to LIVE meanwhile. Same behaviour as Discord; only engages with hosts 2.3.0 or newer.

## 2.6.2

### Fixes

- The GPU optimizations from 2.4.0 are rolled back: broadcasting got worse in real use. Capture, encoder and preview behave exactly as in 2.3.2 again (frames repeated every half second, every mouse move encoded, one-frame encoder buffer, preview always drawn). Everything that came later (listed rooms, the Settings screen, What's new, crash fixes) stays.

## 2.6.1

### Improvements

- The "About" screen became "What's new": it shows what changed in each version, and nothing else. Checking for updates now lives in Settings only; the "See what's new" link on the version card goes to the new screen. The study project notice stays reachable through a link at the end.

## 2.6.0

### New

- Settings screen redone in the Windows settings style: a header with the app, a description and links to the documentation, the repository and bug reports; a version card with "Check for updates", last check and release notes; rows with icon, title and description for name, language and theme.
- Diagnostics and feedback section: turn the diagnostic log on and off without restarting, open the log folder and generate a bug report package (a .zip on the Desktop with diag.log, crash.log, a machine summary and the settings without keys or passwords).

## 2.5.1

### Fixes

- While watching a stream, going to About or Settings and back to the room closed the app. The video tile stayed attached to the previous screen and Windows refused to place it on the new one.

### Improvements

- More useful crash reporting: `crash.log` keeps the latest crashes with the app version and the full stack, including errors outside the UI, and `diag.log` notes the version, the OS, screen navigation and any earlier crash.

## 2.5.0

### New

- Every room on the host shows in the list. Rooms with a password show a lock and ask for it on entry; rooms without one open right away. The "private" option is gone from room creation and editing: use a password to control who gets in. Old rooms marked private now show in the list too (needs the host on 2.4.0).
- The "Public rooms" card is now "Rooms on this host".

## 2.4.0

### Improvements

- Less GPU and network work while the screen is still. Frames where only the mouse moved reach the encoder at most 10 times a second (never with the cursor hidden), and with nothing changing the last frame is repeated once a second. Someone joining the stream gets a frame right away.
- Window capture on Windows 11 24H2 reports what changed in the window; frames with no change at all are skipped.
- With the app minimized or hidden behind a game, the preview and the viewers' video stop being drawn (decoding continues, so the picture is back the moment the window returns).

### Fixes

- On AMD cards the encoder slowed down at low bitrates (at 4 Mbps it dropped half the frames) because of a rate-control buffer that was too small. The buffer now follows the vendors' recommendation, and adaptive quality no longer trips on it.

## 2.3.2

### Fixes

- While broadcasting a monitor, a few minutes of a static screen could make the capture fail with "DXGI_ERROR_INVALID_CALL" and end the broadcast (seen on AMD GPUs). The app now releases the stuck frame and retries; if it persists, the capture is rebuilt without ending the broadcast.
- A failure in the broadcaster's preview no longer ends the capture or the broadcast.

## 2.3.1

### Improvements

- The badge next to the app name now reads "BETA" instead of "STUDY PROJECT", and the red "use at your own risk" note left the Broadcast tab. The first-run notice and the About page are unchanged.

## 2.3.0

### New

- **Adaptive quality** (Broadcast tab, on by default). When your upload cannot keep up, the bitrate steps down to as low as 20% of the chosen value and climbs back on its own when the connection frees up. The bitrate you pick stays the ceiling. Stats show "adapted" while it happens.
- **Visible delay.** Every stream shows the real delay between the broadcaster's screen and yours, plus the ping to the host. Needs the host on 2.3.0; on older hosts the delay is not shown.
- **Watch several streams at once.** The Watch tab is now a grid: one full, two side by side, four in 2×2. Each tile has title, stats, full screen and stop; "Stop all" clears the grid.
- **A sound when someone starts or stops watching your stream**, with a viewer counter next to LIVE and a note with the name. The sound can be turned off in the Broadcast tab. Needs host 2.3.0.

## 2.2.2

### Fixes

- Maximizing or resizing the window with no stream on screen left the video area stuck at its old size in a corner. It now follows the window right away.

## 2.2.1

### Fixes

- The Rooms screen had an empty gap between the host header and "Public rooms". It was the error message slot, which took a line even with no text. It now shows only when there is something to say.

## 2.2.0

### New

- **Settings** tab inside the room. The owner adjusts name, visibility, lifetime, password, who broadcasts and member limit there, creates invites with an expiry, revokes invites and deletes the room. Everyone else sees a summary of the current values. The "Manage" menu in the header is gone: everything lives in the tab.
- Every room in the list has the "⋯" menu (right-click works too): copy invite and favorite on any room; edit and delete on yours.
- Old rooms without an owner (created before 2.1) get one: the first person to enter keeps the room and can edit or delete it. Requires the host on version 2.2.0.

## 2.1.10

### Fixes

- In the update window, the download size text was cut off by the buttons. It now takes the whole line, with the buttons right below.

## 2.1.9

### New

- Edit and delete rooms right from the Rooms screen, without entering them. Rooms you created get a "⋯" menu (right-click works too) with "Edit room…" and "Delete room"; the app joins as the owner for a moment, applies the change and leaves.
- New "Your rooms" card on the host, listing every room you created there, private ones included, which used to be reachable only from inside the room.
- A star next to the name marks the rooms you own.

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
