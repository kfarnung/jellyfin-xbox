# Native Xbox Player Architecture

## Goal

The Xbox app keeps Jellyfin Web for navigation, metadata, authentication, and most UI, but routes Xbox video playback through a native UWP `MediaPlayer` / `MediaPlayerElement` path when native playback is enabled.

The core constraint for this work was to keep the solution app-local when possible. Instead of changing Jellyfin Server or jellyfin-web, the Xbox shell intercepts playback requests, shapes the reported device profile, and hosts a native player surface inside the existing WebView-based app.

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
| `Jellyfin\Core\NativeVideoPlayerService.cs` | Owns native playback lifecycle, stream startup, subtitle sources, track selection, and state reporting |
| `Jellyfin\Core\MessageHandler.cs` | Dispatches playback commands from injected JS into native services |
| `Jellyfin\Controls\JellyfinWebView.xaml` | Hosts WebView, native media surface, input-capture layer, and custom overlay |
| `Jellyfin\Controls\JellyfinWebView.xaml.cs` | Wires shell input and view behavior around the hosted controls |
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

## Key implementation decisions

### 1. Native playback is Xbox-only and takes over all Xbox video playback

This implementation does not special-case only AC3, DTS, or subtitle problem titles. When native playback is enabled, the intent is for Xbox video playback to stay on one consistent native path.

That keeps the takeover logic simpler and avoids splitting Xbox behavior by codec or by server response.

### 2. Native playback defaults on for Xbox when the setting is unset

Existing installs were not automatically using the native path because the setting had never been saved. The app now treats "unset" as enabled on Xbox, while still preserving an explicit opt-out.

### 3. `MediaPlayerElement` is treated as a rendering surface, not the UI

The native UWP transport controls were not used as the long-term controller UX. The app disables native transport controls and does not move focus onto the media element itself.

This was done for two reasons:

- controller interaction on Xbox was hitting a crash path
- shell-owned controls are more predictable than trying to mix WebView focus and native transport UI

### 4. The shell owns controller input and the visible overlay

The app layers a transparent input-capture element and a custom XAML overlay above the player surface. The overlay handles:

- play/pause
- seek backward/forward
- progress display
- position and duration text
- Back behavior
- auto-show / auto-hide behavior

This follows the same architecture used by modern TV players more closely than relying on platform-native media controls.

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

### 8. Subtitle-driven transcodes should preserve compatible audio when possible

An early version of the subtitle-transcode handling used an overly conservative transcode-only profile, which encouraged AAC fallback more often than necessary.

The current design keeps the normal direct-play analysis for subtitle-driven cases and prefers AC3 or E-AC3 as fallback transcode audio codecs when video/subtitle conversion is required. That gives the server a better chance to preserve compatible multichannel audio instead of downgrading everything to AAC.

### 9. Device-profile generation must survive early startup timing

The browser-side device-profile builder is not guaranteed to be initialized before the native player is queried. Because of that, the native shell caches the last host-built profile and can synthesize a conservative fallback profile when needed.

Without this, the first playback decision on a fresh page load could be based on incomplete browser state.

### 10. Fullscreen and display-request work must run on the UI thread

`FullScreenManager` already handled Xbox display-mode switching, so the native player reuses it instead of inventing a separate path.

During implementation, `DisplayRequest.RequestActive()` and related fullscreen work hit an `InvalidCastException` when execution resumed off the UI thread. The fullscreen path now marshals that work through the dispatcher.

### 11. The settings page had to be fixed to expose the real options

The native codec checkboxes were present in the app but clipped by layout. The middle settings column used equal-height rows, which hid most of the native audio controls.

The settings layout now uses auto-sized rows so the complete native playback option stack is visible on Xbox.

## Why the device profile work matters

The native-vs-web player choice is a client decision. Direct play, direct stream, and transcode are still server decisions.

Because of that, the injected native plugin has to shape the reported device profile carefully enough that Jellyfin Server chooses a stream the native UWP player can actually start. Most of the codec and subtitle work in `winuwp.js` exists to make that server decision line up with real Xbox behavior.

Important examples:

- strip disabled passthrough codecs from direct-play profiles
- suppress direct stream when passthrough-only audio must be transcoded
- override direct-play protocols for transcode-only cases
- reject unsupported subtitle selections early enough for retry logic to choose a better stream

## Current limitations and follow-up areas

1. Native subtitle rendering is intentionally limited to external VTT in this architecture.
2. Passthrough behavior still depends on the user's actual HDMI path and must be validated on real hardware.
3. Server-reported transcode reasons can still be generic in some edge cases because Jellyfin Server has its own `DirectPlayError` fallback when no more specific reason survives profile matching.
4. The current overlay is intentionally transport-first; secondary panels for audio, subtitle, info, or overflow actions are future work.

## Summary

The native Xbox player approach is an app-hosted playback shell built around four ideas:

1. intercept playback in `winuwp.js`
2. keep Jellyfin Web as the app's main UI
3. use `NativeVideoPlayerService` plus `MediaPlayerElement` for real playback
4. let the UWP shell, not the WebView, own controller UX and device capability reporting

That combination makes Xbox playback behavior much more controllable without requiring coordinated server or web-client changes for every codec and subtitle edge case.
