using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Xaml.Controls;

namespace Jellyfin.Core.Contract;

/// <summary>
/// Defines the native video playback bridge used by the injected shell script.
/// </summary>
public interface INativeVideoPlayerService
{
    /// <summary>
    /// Raised when the native player visibility or activity state changes.
    /// </summary>
    event EventHandler StateChanged;

    /// <summary>
    /// Raised when playback state should be forwarded back into the WebView.
    /// </summary>
    event EventHandler<NativePlaybackHostMessageEventArgs> HostMessageGenerated;

    /// <summary>
    /// Gets a value indicating whether the native player surface should currently be visible.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    /// Gets a value indicating whether native playback is active or starting.
    /// </summary>
    bool IsPlaybackActive { get; }

    /// <summary>
    /// Attaches the UI surface used to render native playback.
    /// </summary>
    /// <param name="playerElement">The media player element hosted by the shell.</param>
    void Attach(MediaPlayerElement playerElement);

    /// <summary>
    /// Starts native playback for a Jellyfin stream.
    /// </summary>
    /// <param name="args">The playback payload provided by the injected shell script.</param>
    /// <returns>A task that completes when the request has been dispatched to the media player.</returns>
    Task StartAsync(JsonObject args);

    /// <summary>
    /// Stops native playback.
    /// </summary>
    /// <param name="ended">A value indicating whether playback reached the end of the item.</param>
    /// <returns>A task that completes when playback has been stopped.</returns>
    Task StopAsync(bool ended = false);

    /// <summary>
    /// Pauses native playback.
    /// </summary>
    /// <returns>A task that completes when playback has been paused.</returns>
    Task PauseAsync();

    /// <summary>
    /// Resumes native playback.
    /// </summary>
    /// <returns>A task that completes when playback has been resumed.</returns>
    Task UnpauseAsync();

    /// <summary>
    /// Toggles the paused state for native playback.
    /// </summary>
    /// <returns>A task that completes when the pause state has been toggled.</returns>
    Task TogglePauseAsync();

    /// <summary>
    /// Seeks the current item.
    /// </summary>
    /// <param name="positionMilliseconds">The target position in milliseconds.</param>
    /// <returns>A task that completes when the seek has been applied.</returns>
    Task SeekAsync(double positionMilliseconds);

    /// <summary>
    /// Seeks the current item relative to the current playback position.
    /// </summary>
    /// <param name="offsetMilliseconds">The relative offset in milliseconds.</param>
    /// <returns>A task that completes when the seek has been applied.</returns>
    Task SeekRelativeAsync(double offsetMilliseconds);

    /// <summary>
    /// Sets the playback volume using Jellyfin's 0-100 scale.
    /// </summary>
    /// <param name="volume">The playback volume in the 0-100 range.</param>
    /// <returns>A task that completes when the volume has been updated.</returns>
    Task SetVolumeAsync(double volume);

    /// <summary>
    /// Sets the muted state for playback.
    /// </summary>
    /// <param name="isMuted">Whether playback should be muted.</param>
    /// <returns>A task that completes when the mute state has been updated.</returns>
    Task SetMutedAsync(bool isMuted);

    /// <summary>
    /// Sets the active playback rate.
    /// </summary>
    /// <param name="playbackRate">The playback rate multiplier.</param>
    /// <returns>A task that completes when the playback rate has been updated.</returns>
    Task SetPlaybackRateAsync(double playbackRate);

    /// <summary>
    /// Selects the active subtitle stream index for native playback.
    /// </summary>
    /// <param name="subtitleStreamIndex">The Jellyfin subtitle stream index, or -1 to disable subtitles.</param>
    /// <returns>A task that completes when the subtitle selection has been updated.</returns>
    Task SetSubtitleStreamIndexAsync(int subtitleStreamIndex);
}
