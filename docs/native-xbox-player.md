# Native Xbox Player Architecture and Gap Analysis

## Goal

The Xbox app keeps Jellyfin Web for navigation, metadata, authentication, and most UI, but routes Xbox video playback through a native UWP `MediaPlayer` / `MediaPlayerElement` path when native playback is enabled.

The key constraint for this work is to keep the solution app-local when possible. Instead of changing Jellyfin Server or jellyfin-web, the Xbox shell intercepts playback requests, shapes the reported device profile, and hosts a native player surface inside the existing WebView-based app.

This document now serves two purposes:

1. describe the current prototype accurately
2. identify the gaps between the prototype and a full 10-foot Xbox player experience

## High-level design

1. Jellyfin Web chooses an item to play.
2. `Jellyfin\Resources\winuwp.js` intercepts the playback flow through the native shell bridge.
3. The injected script reports an Xbox-specific device profile based on current settings, selected audio/subtitle streams, and passthrough support.
4. Jellyfin Server still decides between direct play, direct stream, and transcode from that device profile.
5. When playback is handed off, the script sends a normalized payload into native code.
6. Native code starts playback through `NativeVideoPlayerService`, which owns `MediaPlayer`, source assignment, events, timed text, and teardown.
7. `JellyfinWebView` hosts both the WebView and the native video surface, while a shell-owned XAML overlay handles controller input and visible transport controls.

## Main components

| File | Responsibility |
| --- | --- |
| `Jellyfin\Resources\winuwp.js` | Native-player plugin, playback interception, device-profile shaping, retry/error behavior, and JS/native messaging |
| `Jellyfin\Core\NativeVideoPlayerService.cs` | Owns native playback lifecycle, stream startup, subtitle sources, track selection, state reporting, and basic seek/rate/volume commands |
| `Jellyfin\Core\MessageHandler.cs` | Dispatches playback commands from injected JS into native services |
| `Jellyfin\Resources\winuwp.js` `UwpXboxHdmiSetupPlugin` path | Handles the non-native fallback fullscreen / HDMI-setup path when native playback is disabled |
| `Jellyfin\Controls\JellyfinWebView.xaml` | Hosts WebView, native media surface, input-capture layer, and the current basic overlay |
| `Jellyfin\Controls\JellyfinWebView.xaml.cs` | Wires shell input and button behavior around the hosted controls |
| `Jellyfin\ViewModels\JellyfinWebViewModel.cs` | Coordinates WebView lifecycle, overlay state, reinjection/recreation, and native playback UI state |
| `Jellyfin\Core\FullScreenManager.cs` | Applies fullscreen, display mode switching, and display-request handling for native playback |
| `Jellyfin\Views\Settings.xaml` and settings classes | Expose native playback and passthrough options to the user |

## Playback flow

1. Jellyfin Web asks for a player and playback info.
2. The UWP native-player plugin in `winuwp.js` decides whether the item is eligible for native playback.
3. The plugin returns a device profile that reflects:
   - native audio support
   - passthrough settings
   - whether DTS/TrueHD must be transcoded
   - whether the selected subtitle path can be rendered natively
4. Jellyfin Server returns the best stream it can produce for that profile.
5. The plugin's `play()` method sends the selected stream details to native code.
6. `NativeVideoPlayerService` starts the `MediaPlayer`, attaches subtitles when supported, and reports state changes back to the shell and page.
7. The XAML overlay handles controller input, transport UI, and Back behavior without giving focus back to the WebView.
8. When native playback is disabled, the legacy `UwpXboxHdmiSetupPlugin` path is still responsible for fullscreen / HDMI setup behavior before web playback starts.

## Current prototype status

The prototype already proves the core architecture:

1. **Playback interception works.** `winuwp.js` can choose the native path, build a device profile, and hand a normalized playback payload to native code.
2. **Native playback works.** `NativeVideoPlayerService` can start, stop, pause, unpause, seek, change playback rate, and keep the native surface synchronized with the page.
3. **The player is shell-owned.** `MediaPlayerElement` is treated as a rendering surface, not the interactive UI.
4. **A basic overlay exists.** The current XAML overlay provides:
   - rewind 10 seconds
   - play/pause
   - forward 10 seconds
   - progress bar
   - current time / duration text
   - auto show / auto hide
   - Back behavior
5. **Basic controller input works.** `JellyfinWebViewModel.TryHandleNativePlaybackInput()` currently handles:
   - `GamepadA` / `Space`
   - D-pad left/right and keyboard left/right
   - shoulders for seek shortcuts
   - D-pad up/down to reveal the overlay
   - Back through the existing app back handler
6. **Subtitle startup plumbing exists.** The native payload includes subtitle track metadata, the service can attach timed text sources, and `setNativeSubtitleStream` exists on the bridge.
7. **Some advanced command plumbing already exists without UI.** The native service already has commands for:
   - playback rate
   - volume
   - mute
   - subtitle selection

## Important prototype constraints

The prototype is intentionally still transport-first. In its current form:

- the progress bar is display-only, not a focusable scrubber
- there is no title, subtitle, or "ends at" metadata in the overlay
- there are no secondary actions for audio, subtitles, playback speed, info, chapters, or quality
- there is no native queue/post-play UX like "Next Up" or "Still Watching"
- there is no media-segment experience like "Skip Intro"

That is consistent with the current code: the shell overlay is a minimal proof of concept, not the final player UX.

## Key implementation decisions

### 1. Native playback is Xbox-only and takes over all Xbox video playback

This implementation does not special-case only AC3, DTS, or subtitle-problem titles. When native playback is enabled, the intent is for Xbox video playback to stay on one consistent native path.

That keeps the takeover logic simpler and avoids splitting Xbox behavior by codec or by server response.

### 2. Native playback defaults on for Xbox when the setting is unset

Existing installs were not automatically using the native path because the setting had never been saved. The app now treats "unset" as enabled on Xbox, while still preserving an explicit opt-out.

### 3. `MediaPlayerElement` is treated as a rendering surface, not the UI

The native UWP transport controls were not used as the long-term controller UX. The app disables native transport controls and does not move focus onto the media element itself.

This remains the right direction. The Android TV app follows the same overall pattern: native playback engine underneath, app-owned playback overlay on top.

### 4. The shell owns controller input and the visible overlay

The app layers a transparent input-capture element and a custom XAML overlay above the player surface. This should remain the long-term model.

For Xbox, that is preferable to trying to mix:

- WebView focus
- `MediaPlayerElement` focus
- native transport controls
- controller and remote input

### 5. Passthrough is explicit and per codec

Passthrough cannot be inferred safely from the media file alone. It depends on the real HDMI sink or AVR behind the Xbox.

The settings therefore expose:

- a global native audio passthrough toggle
- per-codec toggles for AC3, E-AC3, DTS, and TrueHD

Disabled codecs are removed from the reported direct-play capability, not merely hidden in the UI.

### 6. DTS and TrueHD stay on the native path

The app does not intentionally fall back to the WebView player just because a title contains DTS or TrueHD. Instead, `winuwp.js` keeps the native player eligible and shapes the device profile so Jellyfin Server can choose a compatible stream.

For example, when passthrough is disabled, the native path should still be used while the server direct streams or transcodes the audio as needed.

### 7. Subtitle support is intentionally conservative

Current native subtitle support is limited to the path the app can render reliably with UWP timed text. The client therefore only advertises external VTT subtitle support to Jellyfin for native playback.

If the selected subtitle stream cannot be rendered natively, the native path intentionally rejects that selection so jellyfin-web can retry with a transcoded stream instead of silently playing without subtitles.

### 8. Device-profile generation must survive early startup timing

The browser-side device-profile builder is not guaranteed to be initialized before the native player is queried. Because of that, the native shell caches the last host-built profile and can synthesize a conservative fallback profile when needed.

Without this, the first playback decision on a fresh page load could be based on incomplete browser state.

### 9. Fullscreen and display-request work must run on the UI thread

`FullScreenManager` already handled Xbox display-mode switching, so the native player reuses it instead of inventing a separate path.

During implementation, `DisplayRequest.RequestActive()` and related fullscreen work hit an `InvalidCastException` when execution resumed off the UI thread. The fullscreen path now marshals that work through the dispatcher.

## Target UX direction

The target should stay close to the better parts of the Android TV and Android integrated-player experiences:

1. **Keep the shell-owned overlay.**
2. **Use the native player only as the playback surface.**
3. **Optimize for controller and IR remote first.**
4. **Pull rich playback metadata from Jellyfin, not from the player alone.**

The overlay should be designed for the lowest common denominator input model:

- D-pad
- OK / A
- Back / B
- Play/Pause media key

That means no core interaction should depend on:

- shoulder buttons
- face buttons other than confirm/back
- mouse-style hover behavior

### Recommended final overlay layout

| Zone | Content |
| --- | --- |
| Top-left | title, season/episode, subtitle |
| Top-right | current clock or "ends at" time |
| Bottom center | scrubber, elapsed time, remaining/total, back 10 / play-pause / forward 30 |
| Bottom-right | subtitles, audio, more/info |
| Contextual | skip intro, skip credits, next episode, still watching, live TV extras |

This is also a better fit for IR remotes than the current 3-button-only transport bar.

## Gaps between the prototype and a full player

### 1. Input normalization is still incomplete

The current prototype handles only a narrow subset of keys in `TryHandleNativePlaybackInput()`.

Missing or incomplete areas:

- explicit IR/media-key handling such as `MediaPlayPause`, `MediaNextTrack`, `MediaPreviousTrack`, and `MediaStop`
- a single action model that normalizes controller, remote, and keyboard input into the same commands
- seek acceleration and long-press behavior
- overlay navigation rules designed around D-pad + OK instead of only shortcut seeking

For a full implementation, input should become app actions such as:

- `ShowOverlay`
- `Back`
- `TogglePause`
- `SeekBackward`
- `SeekForward`
- `NavigateLeft/Right/Up/Down`
- `OpenMore`

### 2. The overlay is still prototype-level

The current overlay is only enough to prove that shell-owned transport works.

Missing UX:

- top metadata row
- end-time display
- focusable scrubber
- chapter markers
- secondary actions
- overflow / more panel
- playback info panel
- dedicated paused-state layout

### 3. Audio and subtitle selection are only partially implemented

The prototype already carries track metadata in the native payload:

- `audioTracks`
- `subtitleTracks`
- `selectedAudioStreamIndex`
- `selectedSubtitleStreamIndex`

But runtime support is uneven:

- **subtitle selection is partially wired**: the bridge and native service can apply it, but `winuwp.js` currently reports `canSetSubtitleStreamIndex() === false`, so the player path is not advertising subtitle switching as a real supported capability yet
- **subtitle track mapping needs cleanup before enabling it broadly**: the native service should only index against subtitle tracks that were actually added to `ExternalTimedTextSources`, otherwise startup/default subtitle selection can silently miss when unsupported embedded tracks are present ahead of supported external ones
- **audio switching is not wired end-to-end**: startup selection is applied, but there is no host command or native service API for changing the active audio stream at runtime
- there is no shell UI yet for audio/subtitle menus

This is a good early implementation target because a lot of the plumbing is already present.

### 4. Chapters are completely missing

Neither the bridge nor the shell player currently fetches or displays chapter data.

The Android clients show the direction to take:

- chapter data should come from **Jellyfin item metadata**
- not primarily from player-side chapter parsing

For Xbox, chapter support should include:

- previous chapter / next chapter actions
- chapter markers on the scrubber
- optional chapter list in a secondary panel

### 5. Skip intro / skip credits are completely missing

The current Xbox prototype does not fetch or use Jellyfin media segments.

The Android clients use `mediaSegmentsApi.getItemSegments(...)` and treat supported segment types such as:

- `INTRO`
- `OUTRO`
- `PREVIEW`
- `RECAP`
- `COMMERCIAL`

The Xbox player should do the same and support two behaviors:

1. **skip automatically**
2. **ask to skip**

The recommended UX is a small contextual prompt in the lower-right corner, shown only while the segment is active.

### 6. There is no Next Up / Still Watching flow

Today, playback end is basically a stop/teardown boundary. There is no shell-owned post-play experience.

The Android TV app shows the most useful model:

- a dedicated **Next Up** overlay
- optional auto-confirm timer
- **Still Watching** prompt before auto-continuing through long episode sessions

For Xbox, this should be native and shell-owned rather than handed back to jellyfin-web mid-flow.

Before implementing it, the player needs a clear ownership model for queue advancement: either the shell asks jellyfin-web for next-item behavior through the JS bridge, or native code starts owning enough queue/API logic to fetch and launch the next item directly.

### 7. There is no queue-aware player UX yet

A full player needs to know more than the currently playing stream.

Missing capabilities:

- current queue context
- next item awareness
- previous / next item actions
- post-play behavior based on queue state
- "play next automatically" settings

Without queue awareness, Next Up and chapter/segment behavior can only go so far.

This is also the point where the implementation needs to decide whether queue state remains web-owned or becomes partially native-owned.

### 8. There is no secondary settings / info layer yet

The prototype has transport only. A fuller player should expose:

- subtitles
- audio track
- playback speed
- playback info / stream details
- possibly zoom/aspect ratio
- eventually quality selection if the native path is going to own that interaction

Playback rate is a particularly good early addition because the native bridge and service already support it.

### 9. IR remote support needs explicit treatment

The current input path is controller-centric. It should be expanded to work well with Xbox media remotes and similar IR-style devices.

Practical implications:

- all core playback interaction must be usable with D-pad + OK + Back + Play/Pause
- no important feature should require LB/RB/X/Y
- volume should remain a system concern, not a required player-overlay action

### 10. SMTC/system integration is not yet a first-class feature

The prototype already exposes pause, seek, volume, mute, and playback rate at the service layer, but it does not yet clearly act like a fully integrated Xbox media app from the system's point of view.

Future integration should consider:

- `SystemMediaTransportControls`
- media remote play/pause handling
- possible media-key mappings for next/previous
- system-visible playback state polish

## Recommended implementation order

### Phase 1: solidify the input model

1. Normalize controller, remote, and keyboard input into shared player actions.
2. Add explicit media-key support.
3. Add baseline `SystemMediaTransportControls` integration for play/pause/stop and playback-state reporting so IR remotes work like real media devices.
4. Keep Back behavior predictable: hide overlay first, then stop/exit playback.

### Phase 2: upgrade the overlay shell

1. Add top metadata.
2. Replace the display-only progress bar with a real scrubber model.
3. Add a bottom transport row that works with D-pad focus.
4. Add a bottom-right secondary action cluster or overflow panel.

### Phase 3: finish track and speed controls

1. Add subtitle UI and enable runtime subtitle switching end-to-end.
2. Add runtime audio switching end-to-end.
3. Add playback speed UI.
4. Add playback info panel.

### Phase 4: add chapters

1. Fetch chapter data from Jellyfin item metadata.
2. Add previous/next chapter actions.
3. Add chapter markers on the scrubber.
4. Optionally add a chapter list panel.

### Phase 5: add media segments

1. Fetch segments from `mediaSegmentsApi`.
2. Add skip-intro / skip-credits prompt UI.
3. Support both auto-skip and ask-to-skip modes.

### Phase 6: add queue-aware post-play UX

1. Track next item / queue state.
2. Add Next Up overlay.
3. Add Still Watching prompt and settings.
4. Add previous/next item actions where appropriate.

### Phase 7: polish system integration

1. Expand SMTC/media-key behavior beyond the baseline integration from Phase 1.
2. Validate remote behavior on real hardware.
3. Revisit any Xbox-specific fullscreen or HDMI edge cases.

## Why the device profile work still matters

The native-vs-web player choice is a client decision. Direct play, direct stream, and transcode are still server decisions.

Because of that, the injected native plugin has to shape the reported device profile carefully enough that Jellyfin Server chooses a stream the native UWP player can actually start. Most of the codec and subtitle work in `winuwp.js` exists to make that server decision line up with real Xbox behavior.

Important examples:

- strip disabled passthrough codecs from direct-play profiles
- suppress direct stream when passthrough-only audio must be transcoded
- override direct-play protocols for transcode-only cases
- reject unsupported subtitle selections early enough for retry logic to choose a better stream

## Summary

The current prototype has already validated the hardest architectural decision:

1. intercept playback in `winuwp.js`
2. keep Jellyfin Web as the app's main UI
3. use `NativeVideoPlayerService` plus `MediaPlayerElement` for real playback
4. let the UWP shell, not the WebView, own playback UX

The remaining work is not about proving the takeover path anymore. It is mostly about turning the current transport-only prototype into a complete Xbox player:

1. better input normalization for controller and remote
2. a real 10-foot overlay
3. track/metadata/chapter support
4. media-segment features like skip intro
5. queue-aware post-play flows like Next Up and Still Watching
