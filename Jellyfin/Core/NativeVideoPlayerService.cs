using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Core.Contract;
using Microsoft.Extensions.Logging;
using Windows.Data.Json;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Jellyfin.Core;

/// <summary>
/// Hosts native video playback inside the app shell and mirrors state back into the WebView.
/// </summary>
public sealed class NativeVideoPlayerService : INativeVideoPlayerService, IDisposable
{
    private readonly CoreDispatcher _dispatcher;
    private readonly IFullScreenManager _fullScreenManager;
    private readonly ILogger<NativeVideoPlayerService> _logger;
    private readonly List<int> _audioTrackIndices = new();
    private readonly List<NativeSubtitleTrack> _subtitleTracks = new();
    private MediaPlayerElement _playerElement;
    private MediaPlayer _mediaPlayer;
    private MediaPlaybackItem _currentPlaybackItem;
    private DispatcherTimer _progressTimer;
    private long _currentRuntimeTicks;
    private long _pendingStartPositionTicks;
    private string _currentPlaybackToken = string.Empty;
    private long _startOperationId;
    private double? _pendingSeekPositionMilliseconds;
    private bool _pauseWhenReady;
    private int _selectedAudioStreamIndex = -1;
    private int _selectedSubtitleStreamIndex = -1;
    private bool _hasFullscreenSession;
    private bool _isPlaybackActive;
    private bool _isVisible;
    private MediaPlaybackState _lastPlaybackState = MediaPlaybackState.None;
    private string _itemName = string.Empty;
    private string _seriesName = string.Empty;
    private int? _seasonNumber;
    private int? _episodeNumber;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeVideoPlayerService"/> class.
    /// </summary>
    /// <param name="dispatcher">The UI dispatcher.</param>
    /// <param name="fullScreenManager">The fullscreen and HDMI display manager.</param>
    /// <param name="logger">The logger instance.</param>
    public NativeVideoPlayerService(CoreDispatcher dispatcher, IFullScreenManager fullScreenManager, ILogger<NativeVideoPlayerService> logger)
    {
        _dispatcher = dispatcher;
        _fullScreenManager = fullScreenManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler StateChanged;

    /// <inheritdoc />
    public event EventHandler<NativePlaybackHostMessageEventArgs> HostMessageGenerated;

    /// <inheritdoc />
    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public bool IsPlaybackActive
    {
        get => _isPlaybackActive;
        private set
        {
            if (_isPlaybackActive == value)
            {
                return;
            }

            _isPlaybackActive = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Attach(MediaPlayerElement playerElement)
    {
        if (playerElement == null)
        {
            throw new ArgumentNullException(nameof(playerElement));
        }

        _playerElement = playerElement;
        _playerElement.AreTransportControlsEnabled = false;
        _playerElement.IsTabStop = false;
        _playerElement.IsHitTestVisible = false;
        _playerElement.Visibility = Visibility.Collapsed;

        _ = RunOnUiThreadAsync(() =>
        {
            EnsureMediaPlayerAttachedOnUiThread();
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task StartAsync(JsonObject args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        var streamInfo = args.GetNamedObject("streamInfo");
        var playbackUrl = streamInfo.GetNamedString("url", string.Empty);
        if (string.IsNullOrWhiteSpace(playbackUrl))
        {
            throw new InvalidOperationException("Native playback requires a stream url.");
        }

        var displayInfo = args.GetNamedObject("displayInfo", new JsonObject());
        var playbackToken = streamInfo.GetNamedString("playbackToken", string.Empty);
        var runtimeTicks = GetLong(streamInfo, "runtimeTicks");
        var startPositionTicks = GetLong(streamInfo, "playerStartPositionTicks");
        var mediaSourceId = streamInfo.GetNamedString("mediaSourceId", string.Empty);
        var audioTrackIndices = GetTrackIndices(GetNamedArray(streamInfo, "audioTracks"));
        var selectedAudioStreamIndex = GetTrackStreamIndex(streamInfo, "selectedAudioStreamIndex", audioTrackIndices);
        var subtitleTracks = GetSubtitleTracks(GetNamedArray(streamInfo, "subtitleTracks") ?? GetNamedArray(streamInfo, "textTracks") ?? new JsonArray());
        var selectedSubtitleStreamIndex = GetTrackStreamIndex(streamInfo, "selectedSubtitleStreamIndex", subtitleTracks);
        var itemName = streamInfo.GetNamedString("itemName", string.Empty);
        var seriesName = streamInfo.GetNamedString("seriesName", string.Empty);
        var seasonNumber = streamInfo.ContainsKey("seasonNumber") ? (int?)streamInfo.GetNamedNumber("seasonNumber") : null;
        var episodeNumber = streamInfo.ContainsKey("episodeNumber") ? (int?)streamInfo.GetNamedNumber("episodeNumber") : null;

        _ = RunOnUiThreadAsync(async () =>
        {
            try
            {
                EnsureMediaPlayerAttachedOnUiThread();
                await StopInternalAsync(false, false, true).ConfigureAwait(true);
                RecreateMediaPlayerOnUiThread();
                var startOperationId = _startOperationId + 1;
                _startOperationId = startOperationId;
                _currentPlaybackToken = playbackToken;
                IsPlaybackActive = true;
                _currentRuntimeTicks = runtimeTicks;
                _pendingStartPositionTicks = startPositionTicks;
                _pendingSeekPositionMilliseconds = null;
                _pauseWhenReady = false;
                _selectedAudioStreamIndex = selectedAudioStreamIndex;
                _selectedSubtitleStreamIndex = selectedSubtitleStreamIndex;
                _itemName = itemName;
                _seriesName = seriesName;
                _seasonNumber = seasonNumber;
                _episodeNumber = episodeNumber;
                _audioTrackIndices.Clear();
                _audioTrackIndices.AddRange(audioTrackIndices);
                _subtitleTracks.Clear();
                SendHostMessage("nativePlaybackAccepted", CreatePlaybackStartArgs());

                if (displayInfo.Count > 0)
                {
                    await _fullScreenManager.EnableFullscreenAsync(displayInfo).ConfigureAwait(true);
                    _hasFullscreenSession = true;
                }

                if (startOperationId != _startOperationId || _currentPlaybackToken != playbackToken)
                {
                    return;
                }

                var mediaSource = MediaSource.CreateFromUri(new Uri(playbackUrl));
                foreach (var subtitleTrack in subtitleTracks)
                {
                    if (Uri.TryCreate(subtitleTrack.Url, UriKind.Absolute, out var subtitleUri))
                    {
                        mediaSource.ExternalTimedTextSources.Add(TimedTextSource.CreateFromUri(subtitleUri, subtitleTrack.Language));
                        _subtitleTracks.Add(subtitleTrack);
                    }
                }

                _currentPlaybackItem = new MediaPlaybackItem(mediaSource);
                _currentPlaybackItem.AudioTracksChanged += OnAudioTracksChanged;
                _currentPlaybackItem.TimedMetadataTracksChanged += OnTimedMetadataTracksChanged;
                _mediaPlayer.Source = _currentPlaybackItem;
                _mediaPlayer.PlaybackSession.PlaybackRate = 1.0;
                _mediaPlayer.Play();

                _logger.LogInformation("Starting native playback for media source {MediaSourceId}.", mediaSourceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize native playback for media source {MediaSourceId}.", mediaSourceId);
                if (!string.IsNullOrWhiteSpace(playbackToken))
                {
                    var errorArgs = new JsonObject
                    {
                        ["type"] = JsonValue.CreateStringValue("nativePlaybackFailed"),
                        ["message"] = JsonValue.CreateStringValue(ex.Message),
                        ["hresult"] = JsonValue.CreateStringValue($"0x{(uint)ex.HResult:X8}"),
                        ["playbackToken"] = JsonValue.CreateStringValue(playbackToken)
                    };
                    SendHostMessage("nativePlaybackError", errorArgs);
                }

                await StopInternalAsync(false, false, false).ConfigureAwait(true);
            }
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(bool ended = false)
    {
        return RunOnUiThreadAsync(() => StopInternalAsync(true, ended, false));
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        return RunOnUiThreadAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(_currentPlaybackToken) && _mediaPlayer?.Source == null)
            {
                _pauseWhenReady = true;
            }
            else if (_mediaPlayer?.Source != null)
            {
                _mediaPlayer.Pause();
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task UnpauseAsync()
    {
        return RunOnUiThreadAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(_currentPlaybackToken) && _mediaPlayer?.Source == null)
            {
                _pauseWhenReady = false;
            }
            else if (_mediaPlayer?.Source != null)
            {
                _mediaPlayer.Play();
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task TogglePauseAsync()
    {
        return RunOnUiThreadAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(_currentPlaybackToken) && _mediaPlayer?.Source == null)
            {
                _pauseWhenReady = !_pauseWhenReady;
            }
            else if (_mediaPlayer?.Source != null)
            {
                if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    _mediaPlayer.Pause();
                }
                else
                {
                    _mediaPlayer.Play();
                }
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task SeekAsync(double positionMilliseconds)
    {
        return RunOnUiThreadAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(_currentPlaybackToken) && _mediaPlayer?.Source == null)
            {
                _pendingSeekPositionMilliseconds = Math.Max(0, positionMilliseconds);
            }
            else if (_mediaPlayer?.Source != null)
            {
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Max(0, positionMilliseconds));
                SendHostMessage("nativePlaybackTimeUpdate", CreatePlaybackStateArgs());
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task SeekRelativeAsync(double offsetMilliseconds)
    {
        return RunOnUiThreadAsync(() =>
        {
            var targetPositionMilliseconds = ClampPositionMilliseconds(GetRequestedPositionMilliseconds() + offsetMilliseconds);
            if (!string.IsNullOrWhiteSpace(_currentPlaybackToken) && _mediaPlayer?.Source == null)
            {
                _pendingSeekPositionMilliseconds = targetPositionMilliseconds;
            }
            else if (_mediaPlayer?.Source != null)
            {
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromMilliseconds(targetPositionMilliseconds);
                SendHostMessage("nativePlaybackTimeUpdate", CreatePlaybackStateArgs());
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume)
    {
        return RunOnUiThreadAsync(() =>
        {
            if (_mediaPlayer != null)
            {
                var normalizedVolume = Math.Max(0, Math.Min(volume, 100));
                _mediaPlayer.Volume = Math.Pow(normalizedVolume / 100.0, 3.0);
                SendHostMessage("nativePlaybackVolumeChange", CreateVolumeArgs());
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task SetMutedAsync(bool isMuted)
    {
        return RunOnUiThreadAsync(() =>
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.IsMuted = isMuted;
                SendHostMessage("nativePlaybackVolumeChange", CreateVolumeArgs());
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task SetPlaybackRateAsync(double playbackRate)
    {
        return RunOnUiThreadAsync(() =>
        {
            if (_mediaPlayer?.PlaybackSession != null)
            {
                _mediaPlayer.PlaybackSession.PlaybackRate = Math.Max(0.5, Math.Min(playbackRate, 4.0));
                SendHostMessage("nativePlaybackTimeUpdate", CreatePlaybackStateArgs());
            }

            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public Task SetSubtitleStreamIndexAsync(int subtitleStreamIndex)
    {
        return RunOnUiThreadAsync(() =>
        {
            _selectedSubtitleStreamIndex = subtitleStreamIndex;
            ApplySubtitleSelectionOnUiThread();
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _progressTimer?.Stop();
        if (_progressTimer != null)
        {
            _progressTimer.Tick -= OnProgressTimerTick;
        }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaOpened -= OnMediaOpened;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.MediaFailed -= OnMediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            _mediaPlayer.SystemMediaTransportControls.ButtonPressed -= OnSmtcButtonPressed;
            _mediaPlayer.Dispose();
        }
    }

    private async Task StopInternalAsync(bool notifyWeb, bool ended, bool preservePlaybackSession)
    {
        if (_mediaPlayer == null)
        {
            if (!preservePlaybackSession)
            {
                IsVisible = false;
            }

            return;
        }

        var hadSource = _mediaPlayer.Source != null;
        var playbackToken = _currentPlaybackToken;
        var hadPendingStart = !string.IsNullOrWhiteSpace(playbackToken) && !hadSource;
        var stoppedArgs = notifyWeb && !string.IsNullOrWhiteSpace(playbackToken) ? CreateStoppedArgs(ended, playbackToken) : null;

        _progressTimer?.Stop();
        _lastPlaybackState = MediaPlaybackState.None;
        _startOperationId++;
        _currentPlaybackToken = string.Empty;
        _selectedAudioStreamIndex = -1;
        _selectedSubtitleStreamIndex = -1;
        _pendingSeekPositionMilliseconds = null;
        _pauseWhenReady = false;

        if (_mediaPlayer.Source != null)
        {
            _mediaPlayer.Pause();
        }

        if (_currentPlaybackItem != null)
        {
            _currentPlaybackItem.AudioTracksChanged -= OnAudioTracksChanged;
            _currentPlaybackItem.TimedMetadataTracksChanged -= OnTimedMetadataTracksChanged;
            _currentPlaybackItem = null;
        }

        _mediaPlayer.Source = null;
        _currentRuntimeTicks = 0;
        _pendingStartPositionTicks = 0;
        _audioTrackIndices.Clear();
        _subtitleTracks.Clear();

        if (!preservePlaybackSession && _playerElement != null)
        {
            _playerElement.Visibility = Visibility.Collapsed;
        }

        if (!preservePlaybackSession)
        {
            IsVisible = false;
            IsPlaybackActive = false;
            _mediaPlayer.SystemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _itemName = string.Empty;
            _seriesName = string.Empty;
            _seasonNumber = null;
            _episodeNumber = null;
            if (_hasFullscreenSession)
            {
                await _fullScreenManager.DisableFullScreen().ConfigureAwait(true);
                _hasFullscreenSession = false;
            }
        }

        if (notifyWeb && hadPendingStart)
        {
            SendHostMessage("nativePlaybackCancelled", stoppedArgs);
        }
        else if (notifyWeb && (hadSource || ended))
        {
            SendHostMessage("nativePlaybackStopped", stoppedArgs);
        }
    }

    private void EnsureMediaPlayerAttachedOnUiThread()
    {
        if (_playerElement == null)
        {
            throw new InvalidOperationException("Native playback surface has not been attached.");
        }

        if (_progressTimer == null)
        {
            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _progressTimer.Tick += OnProgressTimerTick;
        }

        if (_mediaPlayer == null)
        {
            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.Media,
                IsMuted = false,
                Volume = 1.0
            };

            _mediaPlayer.MediaOpened += OnMediaOpened;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.MediaFailed += OnMediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            _mediaPlayer.CommandManager.IsEnabled = false;
            var smtc = _mediaPlayer.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = true;
            smtc.ButtonPressed += OnSmtcButtonPressed;
        }

        if (_playerElement.MediaPlayer != _mediaPlayer)
        {
            _playerElement.SetMediaPlayer(_mediaPlayer);
        }
    }

    private void RecreateMediaPlayerOnUiThread()
    {
        var volume = _mediaPlayer?.Volume ?? 1.0;
        var isMuted = _mediaPlayer?.IsMuted ?? false;

        _progressTimer?.Stop();

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaOpened -= OnMediaOpened;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.MediaFailed -= OnMediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            _mediaPlayer.SystemMediaTransportControls.ButtonPressed -= OnSmtcButtonPressed;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        EnsureMediaPlayerAttachedOnUiThread();
        _mediaPlayer.Volume = volume;
        _mediaPlayer.IsMuted = isMuted;
    }

    private async void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (!ReferenceEquals(sender, _mediaPlayer))
        {
            return;
        }

        try
        {
            await RunOnUiThreadAsync(() => StopInternalAsync(true, true, false)).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to stop native playback after media end.");
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (!ReferenceEquals(sender, _mediaPlayer))
        {
            return;
        }

        _ = RunOnUiThreadAsync(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentPlaybackToken) || _mediaPlayer?.Source == null || _currentPlaybackItem == null)
                {
                    return Task.CompletedTask;
                }

                if (_pendingSeekPositionMilliseconds.HasValue)
                {
                    sender.PlaybackSession.Position = TimeSpan.FromMilliseconds(_pendingSeekPositionMilliseconds.Value);
                }
                else if (_pendingStartPositionTicks > 0)
                {
                    sender.PlaybackSession.Position = TimeSpan.FromTicks(_pendingStartPositionTicks);
                }

                if (_playerElement != null)
                {
                    _playerElement.Visibility = Visibility.Visible;
                }

                IsVisible = true;
                ApplyAudioSelectionOnUiThread();
                ApplySubtitleSelectionOnUiThread();
                if (_pauseWhenReady)
                {
                    sender.Pause();
                }

                _progressTimer?.Start();
                SendHostMessage("nativePlaybackStarted", CreatePlaybackStartArgs());
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to finalize native playback startup.");
            }

            return Task.CompletedTask;
        });
    }

    private async void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (!ReferenceEquals(sender, _mediaPlayer))
        {
            return;
        }

        try
        {
            await RunOnUiThreadAsync(async () =>
            {
                var playbackToken = _currentPlaybackToken;
                var errorArgs = CreateErrorArgs(args, playbackToken);
                var extendedErrorCode = args.ExtendedErrorCode?.HResult ?? 0;
                _logger.LogError(
                    "Native playback failed. Error={Error}, ExtendedErrorCode=0x{ExtendedErrorCode:X8}, Message={Message}",
                    args.Error,
                    (uint)extendedErrorCode,
                    args.ErrorMessage);

                _currentPlaybackToken = string.Empty;
                SendHostMessage("nativePlaybackError", errorArgs);
                await StopInternalAsync(false, false, false).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to teardown the native player after a playback error.");
        }
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (!ReferenceEquals(sender, _mediaPlayer?.PlaybackSession))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentPlaybackToken))
        {
            return;
        }

        var state = sender.PlaybackState;
        if (state == _lastPlaybackState)
        {
            return;
        }

        if (state == MediaPlaybackState.Paused)
        {
            SendHostMessage("nativePlaybackPause", CreatePlaybackStateArgs());
        }
        else if (state == MediaPlaybackState.Playing)
        {
            SendHostMessage("nativePlaybackUnpause", CreatePlaybackStateArgs());
        }

        _lastPlaybackState = state;
        _mediaPlayer.SystemMediaTransportControls.PlaybackStatus = state switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            _ => _mediaPlayer.SystemMediaTransportControls.PlaybackStatus
        };
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                _ = UnpauseAsync();
                break;
            case SystemMediaTransportControlsButton.Pause:
                _ = PauseAsync();
                break;
            case SystemMediaTransportControlsButton.Stop:
                _ = StopAsync();
                break;
        }
    }

    private void OnAudioTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
    {
        _ = RunOnUiThreadAsync(() =>
        {
            ApplyAudioSelectionOnUiThread();
            return Task.CompletedTask;
        });
    }

    private void OnTimedMetadataTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
    {
        _ = RunOnUiThreadAsync(() =>
        {
            ApplySubtitleSelectionOnUiThread();
            return Task.CompletedTask;
        });
    }

    private void OnProgressTimerTick(object sender, object e)
    {
        if (!IsVisible || _mediaPlayer?.Source == null)
        {
            return;
        }

        SendHostMessage("nativePlaybackTimeUpdate", CreatePlaybackStateArgs());
    }

    private JsonObject CreatePlaybackStartArgs()
    {
        var args = CreatePlaybackStateArgs();
        if (!string.IsNullOrEmpty(_itemName))
        {
            args["itemName"] = JsonValue.CreateStringValue(_itemName);
        }

        if (!string.IsNullOrEmpty(_seriesName))
        {
            args["seriesName"] = JsonValue.CreateStringValue(_seriesName);
        }

        if (_seasonNumber.HasValue)
        {
            args["seasonNumber"] = JsonValue.CreateNumberValue(_seasonNumber.Value);
        }

        if (_episodeNumber.HasValue)
        {
            args["episodeNumber"] = JsonValue.CreateNumberValue(_episodeNumber.Value);
        }

        return args;
    }

    private JsonObject CreatePlaybackStateArgs()
    {
        var args = CreateVolumeArgs();
        args["currentTime"] = JsonValue.CreateNumberValue(GetCurrentTimeMilliseconds());
        args["duration"] = JsonValue.CreateNumberValue(GetDurationMilliseconds());
        args["paused"] = JsonValue.CreateBooleanValue(_mediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Paused);
        args["playbackRate"] = JsonValue.CreateNumberValue(_mediaPlayer?.PlaybackSession?.PlaybackRate ?? 1.0);
        if (!string.IsNullOrWhiteSpace(_currentPlaybackToken))
        {
            args["playbackToken"] = JsonValue.CreateStringValue(_currentPlaybackToken);
        }

        return args;
    }

    private JsonObject CreateStoppedArgs(bool ended, string playbackToken)
    {
        var args = CreatePlaybackStateArgs();
        if (!string.IsNullOrWhiteSpace(playbackToken))
        {
            args["playbackToken"] = JsonValue.CreateStringValue(playbackToken);
        }

        args["ended"] = JsonValue.CreateBooleanValue(ended);
        return args;
    }

    private JsonObject CreateVolumeArgs()
    {
        var args = new JsonObject
        {
            ["volume"] = JsonValue.CreateNumberValue(GetVolumeLevel()),
            ["isMuted"] = JsonValue.CreateBooleanValue(_mediaPlayer?.IsMuted ?? false)
        };
        return args;
    }

    private JsonObject CreateErrorArgs(MediaPlayerFailedEventArgs args, string playbackToken)
    {
        var extendedErrorCode = args.ExtendedErrorCode?.HResult ?? 0;
        return new JsonObject
        {
            ["type"] = JsonValue.CreateStringValue("nativePlaybackFailed"),
            ["message"] = JsonValue.CreateStringValue(string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage),
            ["hresult"] = JsonValue.CreateStringValue($"0x{(uint)extendedErrorCode:X8}"),
            ["playbackToken"] = JsonValue.CreateStringValue(playbackToken ?? string.Empty)
        };
    }

    private double GetCurrentTimeMilliseconds()
    {
        return _mediaPlayer?.PlaybackSession?.Position.TotalMilliseconds ?? 0;
    }

    private double GetRequestedPositionMilliseconds()
    {
        if (_pendingSeekPositionMilliseconds.HasValue)
        {
            return _pendingSeekPositionMilliseconds.Value;
        }

        if (_mediaPlayer?.Source != null)
        {
            return GetCurrentTimeMilliseconds();
        }

        return _pendingStartPositionTicks > 0 ? TimeSpan.FromTicks(_pendingStartPositionTicks).TotalMilliseconds : 0;
    }

    private double GetDurationMilliseconds()
    {
        if (_mediaPlayer?.PlaybackSession?.NaturalDuration > TimeSpan.Zero)
        {
            return _mediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds;
        }

        return _currentRuntimeTicks > 0 ? TimeSpan.FromTicks(_currentRuntimeTicks).TotalMilliseconds : 0;
    }

    private double ClampPositionMilliseconds(double positionMilliseconds)
    {
        var durationMilliseconds = GetDurationMilliseconds();
        if (durationMilliseconds > 0)
        {
            return Math.Max(0, Math.Min(positionMilliseconds, durationMilliseconds));
        }

        return Math.Max(0, positionMilliseconds);
    }

    private double GetVolumeLevel()
    {
        if (_mediaPlayer == null)
        {
            return 100;
        }

        return Math.Min(Math.Round(Math.Pow(_mediaPlayer.Volume, 1.0 / 3.0) * 100.0), 100.0);
    }

    private void ApplyAudioSelectionOnUiThread()
    {
        if (_currentPlaybackItem == null || _selectedAudioStreamIndex < 0)
        {
            return;
        }

        var audioTracks = _currentPlaybackItem.AudioTracks;
        var matchingAudioTrackIndex = _audioTrackIndices.FindIndex(trackIndex => trackIndex == _selectedAudioStreamIndex);
        if (matchingAudioTrackIndex >= 0 && matchingAudioTrackIndex < audioTracks.Count)
        {
            audioTracks.SelectedIndex = matchingAudioTrackIndex;
        }
    }

    private void ApplySubtitleSelectionOnUiThread()
    {
        if (_currentPlaybackItem == null)
        {
            return;
        }

        var timedMetadataTracks = _currentPlaybackItem.TimedMetadataTracks;
        for (uint trackIndex = 0; trackIndex < timedMetadataTracks.Count; trackIndex++)
        {
            timedMetadataTracks.SetPresentationMode(trackIndex, TimedMetadataTrackPresentationMode.Disabled);
        }

        if (_selectedSubtitleStreamIndex < 0)
        {
            return;
        }

        var matchingTrackIndex = _subtitleTracks.FindIndex(track => track.Index == _selectedSubtitleStreamIndex);
        if (matchingTrackIndex >= 0 && matchingTrackIndex < timedMetadataTracks.Count)
        {
            timedMetadataTracks.SetPresentationMode((uint)matchingTrackIndex, TimedMetadataTrackPresentationMode.PlatformPresented);
        }
    }

    private void SendHostMessage(string type, JsonObject args)
    {
        if (type.StartsWith("nativePlayback", StringComparison.Ordinal)
            && (args == null
                || !args.ContainsKey("playbackToken")
                || string.IsNullOrWhiteSpace(args.GetNamedString("playbackToken", string.Empty))))
        {
            return;
        }

        HostMessageGenerated?.Invoke(this, new NativePlaybackHostMessageEventArgs(type, args));
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var completionSource = new TaskCompletionSource<bool>();
        _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
                completionSource.SetResult(true);
            }
            catch (Exception exception)
            {
                completionSource.SetException(exception);
            }
        });

        return completionSource.Task;
    }

    private static long GetLong(JsonObject jsonObject, string key)
    {
        if (jsonObject == null || !jsonObject.ContainsKey(key))
        {
            return 0;
        }

        return (long)jsonObject.GetNamedNumber(key, 0);
    }

    private static JsonArray GetNamedArray(JsonObject jsonObject, string key)
    {
        if (jsonObject == null || !jsonObject.ContainsKey(key))
        {
            return null;
        }

        return jsonObject.GetNamedArray(key);
    }

    private static IReadOnlyList<NativeSubtitleTrack> GetSubtitleTracks(JsonArray jsonArray)
    {
        var subtitleTracks = new List<NativeSubtitleTrack>();
        if (jsonArray == null)
        {
            return subtitleTracks;
        }

        foreach (var jsonValue in jsonArray)
        {
            if (jsonValue.ValueType != JsonValueType.Object)
            {
                continue;
            }

            var jsonObject = jsonValue.GetObject();
            var url = jsonObject.GetNamedString("url", string.Empty);
            var index = (int)jsonObject.GetNamedNumber("index", -1);
            if (string.IsNullOrWhiteSpace(url) && index < 0)
            {
                continue;
            }

            subtitleTracks.Add(new NativeSubtitleTrack
            {
                Index = index,
                Language = jsonObject.GetNamedString("language", "und"),
                IsDefault = jsonObject.GetNamedBoolean("isDefault", false),
                Url = url
            });
        }

        return subtitleTracks;
    }

    private static List<int> GetTrackIndices(JsonArray jsonArray)
    {
        var trackIndices = new List<int>();
        if (jsonArray == null)
        {
            return trackIndices;
        }

        foreach (var jsonValue in jsonArray)
        {
            if (jsonValue.ValueType != JsonValueType.Object)
            {
                continue;
            }

            var jsonObject = jsonValue.GetObject();
            var index = (int)jsonObject.GetNamedNumber("index", -1);
            if (index >= 0)
            {
                trackIndices.Add(index);
            }
        }

        return trackIndices;
    }

    private static int GetTrackStreamIndex(JsonObject streamInfo, string propertyName, IReadOnlyList<int> trackIndices)
    {
        if (streamInfo != null && streamInfo.ContainsKey(propertyName))
        {
            return (int)streamInfo.GetNamedNumber(propertyName, -1);
        }

        foreach (var trackIndex in trackIndices)
        {
            if (trackIndex >= 0)
            {
                return trackIndex;
            }
        }

        return -1;
    }

    private static int GetTrackStreamIndex(JsonObject streamInfo, string propertyName, IReadOnlyList<NativeSubtitleTrack> subtitleTracks)
    {
        if (streamInfo != null && streamInfo.ContainsKey(propertyName))
        {
            return (int)streamInfo.GetNamedNumber(propertyName, -1);
        }

        foreach (var subtitleTrack in subtitleTracks)
        {
            if (subtitleTrack.IsDefault)
            {
                return subtitleTrack.Index;
            }
        }

        return -1;
    }

    private sealed class NativeSubtitleTrack
    {
        public int Index { get; set; }

        public string Language { get; set; } = "und";

        public bool IsDefault { get; set; }

        public string Url { get; set; } = string.Empty;
    }
}
