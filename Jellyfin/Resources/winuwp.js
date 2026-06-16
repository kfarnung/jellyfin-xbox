(function (
    appName,
    appVersion,
    deviceName,
    supportsHdr10,
    supportsDolbyVision,
    nativeVideoPlaybackEnabled,
    nativeAudioPassthroughEnabled,
    nativeAllowAc3,
    nativeAllowEac3,
    nativeAllowDts,
    nativeAllowTrueHd
) {
    'use strict';

    if (window.__winUwpShellLoaded) {
        return;
    }

    window.__winUwpShellLoaded = true;
    console.log('Windows UWP adapter');

    const xbox = deviceName.toLowerCase().indexOf('xbox') !== -1;
    const xboxSeries = deviceName.toLowerCase().indexOf('xbox series') !== -1;
    const mobile = deviceName.toLowerCase().indexOf('mobile') !== -1;
    let deviceProfileBuilder = null;

    const nativePlaybackConfig = {
        enabled: !!nativeVideoPlaybackEnabled,
        enablePassthrough: !!nativeAudioPassthroughEnabled,
        allowAc3: !!nativeAllowAc3,
        allowEac3: !!nativeAllowEac3,
        allowDts: !!nativeAllowDts,
        allowTrueHd: !!nativeAllowTrueHd
    };

    console.info('[native-player] config', nativePlaybackConfig);

    const nativePlaybackBridge = {
        activePlayer: null,
        hostListenerRegistered: false,

        clearActivePlayer(player) {
            if (!player || this.activePlayer === player) {
                this.activePlayer = null;
            }
        },

        setActivePlayer(player) {
            this.activePlayer = player;
            this.registerHostListener();
        },

        registerHostListener() {
            if (this.hostListenerRegistered || !window.chrome || !window.chrome.webview) {
                return;
            }

            this.hostListenerRegistered = true;
            window.chrome.webview.addEventListener('message', (event) => {
                let message = event.data;
                if (typeof message === 'string') {
                    try {
                        message = JSON.parse(message);
                    } catch (error) {
                        return;
                    }
                }

                if (!message || typeof message.type !== 'string' || message.type.indexOf('nativePlayback') !== 0) {
                    return;
                }

                if (this.activePlayer && typeof this.activePlayer.handleHostMessage === 'function') {
                    this.activePlayer.handleHostMessage(message);
                }
            });
        }
    };

    function postMessage(type, args = {}) {
        console.debug(`AppHost.${type}`, args);
        if (!window.chrome || !window.chrome.webview) {
            console.warn(`Unable to post ${type}; WebView host is not available.`, args);
            return;
        }

        window.chrome.webview.postMessage(JSON.stringify({
            type: type,
            args: args
        }));
    }

    function getBaseDeviceProfileOptions() {
        const options = {};
        if (supportsHdr10 != null) {
            options.supportsHdr10 = supportsHdr10;
        }

        if (supportsDolbyVision != null) {
            options.supportsDolbyVision = supportsDolbyVision;
        }

        if (xboxSeries) {
            options.maxVideoWidth = 3840;
        }

        return options;
    }

    function buildDeviceProfile(overrides = {}) {
        if (!deviceProfileBuilder) {
            return null;
        }

        const options = Object.assign(getBaseDeviceProfileOptions(), overrides);
        if (Array.isArray(options.disableVideoAudioCodecs) && options.disableVideoAudioCodecs.length === 0) {
            delete options.disableVideoAudioCodecs;
        }

        if (Array.isArray(options.disableHlsVideoAudioCodecs) && options.disableHlsVideoAudioCodecs.length === 0) {
            delete options.disableHlsVideoAudioCodecs;
        }

        return deviceProfileBuilder(options);
    }

    function stripContainerTokens(containerValue, disallowedContainers) {
        if (!containerValue) {
            return containerValue;
        }

        const remainingContainers = containerValue
            .split(',')
            .map((container) => container.trim())
            .filter((container) => container.length > 0 && !disallowedContainers.includes(container.toLowerCase()));

        return remainingContainers.join(',');
    }

    function sanitizeNativeDeviceProfile(profile) {
        if (!profile || !Array.isArray(profile.DirectPlayProfiles)) {
            return profile;
        }

        const disallowedContainers = ['flv'];
        profile.DirectPlayProfiles = profile.DirectPlayProfiles
            .map((directPlayProfile) => {
                const sanitizedProfile = Object.assign({}, directPlayProfile);
                sanitizedProfile.Container = stripContainerTokens(sanitizedProfile.Container, disallowedContainers);
                return sanitizedProfile;
            })
            .filter((directPlayProfile) => !!directPlayProfile.Container);

        return profile;
    }

    function getNativeDeviceProfileOverrides() {
        const disableVideoAudioCodecs = [];
        const disableHlsVideoAudioCodecs = [];

        if (!nativePlaybackConfig.allowAc3) {
            disableVideoAudioCodecs.push('ac3');
            disableHlsVideoAudioCodecs.push('ac3');
        }

        if (!nativePlaybackConfig.allowEac3) {
            disableVideoAudioCodecs.push('eac3');
            disableHlsVideoAudioCodecs.push('eac3');
        }

        if (!nativePlaybackConfig.allowDts) {
            disableVideoAudioCodecs.push('dca', 'dts');
            disableHlsVideoAudioCodecs.push('dca', 'dts');
        }

        if (!nativePlaybackConfig.allowTrueHd) {
            disableVideoAudioCodecs.push('truehd');
            disableHlsVideoAudioCodecs.push('truehd');
        }

        return {
            disableVideoAudioCodecs: disableVideoAudioCodecs,
            disableHlsVideoAudioCodecs: disableHlsVideoAudioCodecs,
            supportsDts: nativePlaybackConfig.enablePassthrough && nativePlaybackConfig.allowDts,
            supportsTrueHd: nativePlaybackConfig.enablePassthrough && nativePlaybackConfig.allowTrueHd
        };
    }

    function canUseNativeVideoPlayback() {
        return nativePlaybackConfig.enabled;
    }

    function getMediaSource(streamInfo) {
        return streamInfo && (streamInfo.mediaSource || streamInfo.MediaSource) || null;
    }

    function getVideoStream(streamInfo) {
        const mediaSource = getMediaSource(streamInfo);
        const mediaStreams = mediaSource && mediaSource.MediaStreams ? mediaSource.MediaStreams : [];
        return mediaStreams.find((stream) => stream.Type === 'Video');
    }

    function getDurationMilliseconds(streamInfo) {
        const mediaSource = getMediaSource(streamInfo);
        return mediaSource && mediaSource.RunTimeTicks ? mediaSource.RunTimeTicks / 10000 : 0;
    }

    function buildDisplayInfo(streamInfo) {
        const videoStream = getVideoStream(streamInfo);
        if (!videoStream) {
            return {};
        }

        return {
            videoWidth: videoStream.Width || 0,
            videoHeight: videoStream.Height || 0,
            videoFrameRate: videoStream.AverageFrameRate || videoStream.RealFrameRate || 0,
            videoRangeType: videoStream.VideoRangeType || null
        };
    }

    function buildNativePlaybackPayload(streamInfo) {
        const mediaSource = getMediaSource(streamInfo);
        const item = streamInfo && streamInfo.item ? streamInfo.item : null;
        const playbackToken = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
        const textTracks = Array.isArray(streamInfo.textTracks) ? streamInfo.textTracks.map((track) => ({
            url: track.url,
            language: track.language || 'und',
            isDefault: !!track.isDefault,
            index: typeof track.index === 'number' ? track.index : -1,
            format: track.format || null
        })) : [];
        const textTrackUrlsByIndex = new Map(textTracks.map((track) => [track.index, track.url]));
        const subtitleTracks = mediaSource && Array.isArray(mediaSource.MediaStreams)
            ? mediaSource.MediaStreams
                .filter((stream) => stream.Type === 'Subtitle' && (stream.DeliveryMethod === 'External' || stream.DeliveryMethod === 'Embed'))
                .map((stream) => ({
                    url: textTrackUrlsByIndex.get(stream.Index) || null,
                    language: stream.Language || 'und',
                    isDefault: stream.Index === mediaSource.DefaultSubtitleStreamIndex,
                    index: typeof stream.Index === 'number' ? stream.Index : -1,
                    format: stream.Codec || null,
                    deliveryMethod: stream.DeliveryMethod || null
                }))
            : textTracks;
        const audioTracks = mediaSource && Array.isArray(mediaSource.MediaStreams)
            ? mediaSource.MediaStreams
                .filter((stream) => stream.Type === 'Audio')
                .map((stream) => ({
                    index: typeof stream.Index === 'number' ? stream.Index : -1,
                    language: stream.Language || 'und',
                    title: stream.DisplayTitle || stream.Title || stream.Codec || null,
                    isDefault: stream.Index === mediaSource.DefaultAudioStreamIndex
                }))
            : [];
        const selectedAudioStreamIndex = mediaSource && typeof mediaSource.DefaultAudioStreamIndex === 'number'
            ? mediaSource.DefaultAudioStreamIndex
            : (audioTracks.find((track) => track.isDefault)?.index ?? -1);
        const selectedSubtitleStreamIndex = mediaSource && typeof mediaSource.DefaultSubtitleStreamIndex === 'number'
            ? mediaSource.DefaultSubtitleStreamIndex
            : (subtitleTracks.find((track) => track.isDefault)?.index ?? -1);

        return {
            streamInfo: {
                url: streamInfo.url,
                playbackToken: playbackToken,
                playerStartPositionTicks: streamInfo.playerStartPositionTicks || 0,
                runtimeTicks: mediaSource && mediaSource.RunTimeTicks ? mediaSource.RunTimeTicks : 0,
                playMethod: streamInfo.playMethod || null,
                itemId: item && item.Id ? item.Id : null,
                mediaSourceId: mediaSource && mediaSource.Id ? mediaSource.Id : (streamInfo.mediaSourceId || null),
                serverId: item && item.ServerId ? item.ServerId : null,
                selectedAudioStreamIndex: selectedAudioStreamIndex,
                audioTracks: audioTracks,
                selectedSubtitleStreamIndex: selectedSubtitleStreamIndex,
                subtitleTracks: subtitleTracks
            },
            displayInfo: buildDisplayInfo(streamInfo)
        };
    }

    function triggerPluginEvent(plugin, eventName, args = []) {
        plugin.PluginOptions.events.trigger(plugin, eventName, args);
    }

    function createPlaybackError(type, message, streamInfo, hresult) {
        return {
            type: type || 'nativePlaybackFailed',
            message: message || 'Native playback failed.',
            hresult: hresult || null,
            streamInfo: streamInfo || null
        };
    }

    const AppInfo = {
        deviceName: deviceName,
        appName: appName,
        appVersion: appVersion
    };

    const SupportedFeatures = [
        'clientsettings',
        'displaylanguage',
        'displaymode',
        'exit',
        'exitmenu',
        'externallinkdisplay',
        'externallinks',
        'htmlaudioautoplay',
        'htmlvideoautoplay',
        'multiserver',
        'otherapppromotions',
        'screensaver',
        'subtitleappearancesettings',
        'subtitleburnsettings',
        'targetblank'
    ];

    if (xbox || mobile) {
        SupportedFeatures.push('physicalvolumecontrol');
    }

    console.debug('SupportedFeatures', SupportedFeatures);

    window.NativeShell = {
        AppHost: {
            init: function () {
                console.debug('AppHost.init', AppInfo);
                return Promise.resolve(AppInfo);
            },

            appName: function () {
                console.debug('AppHost.appName', AppInfo.appName);
                return AppInfo.appName;
            },

            appVersion: function () {
                console.debug('AppHost.appVersion', AppInfo.appVersion);
                return AppInfo.appVersion;
            },

            deviceName: function () {
                console.debug('AppHost.deviceName', AppInfo.deviceName);
                return AppInfo.deviceName;
            },

            exit: function () {
                postMessage('exit');
            },

            getDefaultLayout: function () {
                let layout;
                if (xbox) {
                    layout = 'tv';
                } else if (mobile) {
                    layout = 'mobile';
                } else {
                    layout = 'desktop';
                }

                console.debug('AppHost.getDefaultLayout', layout);
                return layout;
            },

            getDeviceProfile: function (profileBuilder) {
                deviceProfileBuilder = profileBuilder;
                const profile = canUseNativeVideoPlayback()
                    ? sanitizeNativeDeviceProfile(buildDeviceProfile(getNativeDeviceProfileOverrides()))
                    : buildDeviceProfile();
                console.debug('AppHost.getDeviceProfile', {
                    nativePlaybackEnabled: nativePlaybackConfig.enabled,
                    passthroughEnabled: nativePlaybackConfig.enablePassthrough
                });
                return profile;
            },

            getNativePlaybackConfig: function () {
                return Object.assign({}, nativePlaybackConfig);
            },

            supports: function (command) {
                const isSupported = command && SupportedFeatures.indexOf(command.toLowerCase()) !== -1;
                console.debug('AppHost.supports', {
                    command: command,
                    isSupported: isSupported
                });
                return isSupported;
            }
        },

        enableFullscreen: function () {
        },

        disableFullscreen: function () {
            postMessage('disableFullscreen');
        },

        getPlugins: function () {
            console.debug('getPlugins');
            postMessage('loaded');
            return ['UwpNativeVideoPlayerPlugin', 'UwpXboxHdmiSetupPlugin'];
        },

        selectServer: function () {
            postMessage('selectServer');
        },

        openClientSettings: function () {
            postMessage('openClientSettings');
        }
    };

    class UwpNativeVideoPlayerPlugin {
        constructor(pluginOptions) {
            this.name = 'UwpNativeVideoPlayerPlugin';
            this.id = 'UwpNativeVideoPlayerPlugin';
            this.type = 'mediaplayer';
            this.priority = -100;
            this.isLocalPlayer = true;
            this.PluginOptions = pluginOptions;
            this.resetState();
            nativePlaybackBridge.registerHostListener();
        }

        resetState() {
            this._currentTime = 0;
            this._duration = 0;
            this._hasStartedPlayback = false;
            this._paused = false;
            this._volume = 100;
            this._muted = false;
            this._playbackRate = 1;
            this._playbackToken = null;
            this._pendingStop = null;
            this._subtitleStreamIndex = -1;
            this._streamInfo = null;
            this._pendingPlay = null;
        }

        canPlayMediaType(mediaType) {
            return canUseNativeVideoPlayback() && mediaType === 'Video';
        }

        canPlayItem(item) {
            const canPlay = canUseNativeVideoPlayback() && item && item.MediaType === 'Video';
            console.info('[native-player] canPlayItem', {
                enabled: nativePlaybackConfig.enabled,
                itemId: item && item.Id ? item.Id : null,
                mediaType: item && item.MediaType ? item.MediaType : null,
                canPlay: canPlay
            });
            return canPlay;
        }

        getDeviceProfile() {
            return Promise.resolve(sanitizeNativeDeviceProfile(buildDeviceProfile(getNativeDeviceProfileOverrides())));
        }

        play(streamInfo) {
            if (!canUseNativeVideoPlayback()) {
                return Promise.reject(new Error('Native video playback is disabled.'));
            }

            if (!streamInfo || !streamInfo.url) {
                return Promise.reject(new Error('Native playback requires a stream url.'));
            }

            const payload = buildNativePlaybackPayload(streamInfo);
            this._streamInfo = streamInfo;
            this._currentTime = Math.max(streamInfo.playerStartPositionTicks || 0, 0) / 10000;
            this._duration = getDurationMilliseconds(streamInfo);
            this._paused = false;
            this._playbackToken = payload.streamInfo.playbackToken;
            console.info('[native-player] play', {
                mediaSourceId: payload.streamInfo.mediaSourceId,
                playMethod: payload.streamInfo.playMethod,
                url: payload.streamInfo.url
            });
            nativePlaybackBridge.setActivePlayer(this);
            postMessage('startNativePlayback', payload);

            return new Promise((resolve, reject) => {
                this._pendingPlay = {
                    resolve: resolve,
                    reject: reject
                };
            });
        }

        stop() {
            if (!this._streamInfo && !this._playbackToken) {
                return Promise.resolve();
            }

            if (this._pendingStop) {
                return this._pendingStop.promise;
            }

            this._pendingStop = {};
            this._pendingStop.promise = new Promise((resolve) => {
                this._pendingStop.resolve = resolve;
            });

            postMessage('stopNativePlayback');
            return this._pendingStop.promise;
        }

        pause() {
            postMessage('pauseNativePlayback');
        }

        unpause() {
            postMessage('unpauseNativePlayback');
        }

        paused() {
            return this._paused;
        }

        currentSrc() {
            return this._streamInfo ? this._streamInfo.url : null;
        }

        isPlaying() {
            return !!this._streamInfo && !this._paused;
        }

        supports(feature) {
            return feature === 'PlaybackRate';
        }

        canSetAudioStreamIndex() {
            return false;
        }

        canSetSubtitleStreamIndex() {
            return false;
        }

        setSubtitleStreamIndex(index) {
            this._subtitleStreamIndex = index;
            postMessage('setNativeSubtitleStream', {
                subtitleStreamIndex: index
            });
        }

        setSecondarySubtitleStreamIndex() {
        }

        currentTime(val) {
            if (val != null) {
                this._currentTime = Math.max(0, val);
                postMessage('seekNativePlayback', {
                    positionMilliseconds: this._currentTime
                });
                return;
            }

            return this._currentTime;
        }

        duration() {
            return this._duration;
        }

        setVolume(val) {
            this._volume = Math.max(0, Math.min(100, val));
            postMessage('setNativePlaybackVolume', {
                volume: this._volume
            });
        }

        getVolume() {
            return this._volume;
        }

        setMute(mute) {
            this._muted = !!mute;
            postMessage('setNativePlaybackMute', {
                isMuted: this._muted
            });
        }

        isMuted() {
            return this._muted;
        }

        setPlaybackRate(value) {
            this._playbackRate = value;
            postMessage('setNativePlaybackRate', {
                playbackRate: value
            });
        }

        getPlaybackRate() {
            return this._playbackRate;
        }

        getSupportedPlaybackRates() {
            return [{
                name: '0.5x',
                id: 0.5
            }, {
                name: '0.75x',
                id: 0.75
            }, {
                name: '1x',
                id: 1.0
            }, {
                name: '1.25x',
                id: 1.25
            }, {
                name: '1.5x',
                id: 1.5
            }, {
                name: '1.75x',
                id: 1.75
            }, {
                name: '2x',
                id: 2.0
            }, {
                name: '2.5x',
                id: 2.5
            }, {
                name: '3x',
                id: 3.0
            }, {
                name: '3.5x',
                id: 3.5
            }, {
                name: '4.0x',
                id: 4.0
            }];
        }

        handleHostMessage(message) {
            const args = message.args || {};
            if (args.playbackToken && this._playbackToken && args.playbackToken !== this._playbackToken) {
                return;
            }

            this.updateState(args);

            switch (message.type) {
                case 'nativePlaybackAccepted':
                    if (this._pendingPlay) {
                        this._pendingPlay.resolve();
                        this._pendingPlay = null;
                    }
                    break;
                case 'nativePlaybackStarted':
                    this._hasStartedPlayback = true;
                    break;
                case 'nativePlaybackCancelled':
                    this._hasStartedPlayback = false;
                    this._paused = false;
                    if (this._pendingStop) {
                        this._pendingStop.resolve();
                        this._pendingStop = null;
                    }
                    this._pendingPlay = null;
                    this._playbackToken = null;
                    this._streamInfo = null;
                    nativePlaybackBridge.clearActivePlayer(this);
                    triggerPluginEvent(this, 'stopped');
                    break;
                case 'nativePlaybackTimeUpdate':
                    triggerPluginEvent(this, 'timeupdate');
                    break;
                case 'nativePlaybackPause':
                    this._paused = true;
                    triggerPluginEvent(this, 'pause');
                    break;
                case 'nativePlaybackUnpause':
                    this._paused = false;
                    triggerPluginEvent(this, 'unpause');
                    break;
                case 'nativePlaybackVolumeChange':
                    triggerPluginEvent(this, 'volumechange');
                    break;
                case 'nativePlaybackStopped':
                    if (args.ended) {
                        this._currentTime = this._duration;
                    }

                    this._hasStartedPlayback = false;
                    this._paused = false;
                    if (this._pendingStop) {
                        this._pendingStop.resolve();
                        this._pendingStop = null;
                    }
                    this._playbackToken = null;
                    this._streamInfo = null;
                    nativePlaybackBridge.clearActivePlayer(this);
                    triggerPluginEvent(this, 'stopped');
                    break;
                case 'nativePlaybackError': {
                    const error = createPlaybackError(args.type, args.message, this._streamInfo, args.hresult);
                    this._hasStartedPlayback = false;
                    if (this._pendingStop) {
                        this._pendingStop.resolve();
                        this._pendingStop = null;
                    }
                    this._playbackToken = null;
                    this._streamInfo = null;
                    nativePlaybackBridge.clearActivePlayer(this);

                    if (this._pendingPlay) {
                        this._pendingPlay.reject(error);
                        this._pendingPlay = null;
                    } else {
                        triggerPluginEvent(this, 'error', [error]);
                    }

                    break;
                }
                default:
                    break;
            }
        }

        updateState(args) {
            if (typeof args.currentTime === 'number') {
                this._currentTime = args.currentTime;
            }

            if (typeof args.duration === 'number') {
                this._duration = args.duration;
            }

            if (typeof args.volume === 'number') {
                this._volume = args.volume;
            }

            if (typeof args.isMuted === 'boolean') {
                this._muted = args.isMuted;
            }

            if (typeof args.paused === 'boolean') {
                this._paused = args.paused;
            }

            if (typeof args.playbackRate === 'number') {
                this._playbackRate = args.playbackRate;
            }
        }

        destroy() {
            if (this._streamInfo || this._playbackToken) {
                postMessage('stopNativePlayback');
            }

            if (this._pendingStop) {
                this._pendingStop.resolve();
            }

            nativePlaybackBridge.clearActivePlayer(this);
            this.resetState();
        }
    }

    class UwpXboxHdmiSetupPlugin {
        constructor(pluginOptions) {
            this.name = 'UwpXboxHdmiSetupPlugin';
            this.id = 'UwpXboxHdmiSetupPlugin';
            this.type = 'preplayintercept';
            this.priority = 0;
            this.PluginOptions = pluginOptions;
        }

        async intercept(options) {
            if (canUseNativeVideoPlayback()) {
                return;
            }

            const item = options.item;
            if (!item) {
                return;
            }

            if ('mediaSourceId' in options) {
                const mediaSourceId = options.mediaSourceId;
                let mediaStreams = null;
                let mediaSource = null;

                if (item.MediaSources == null) {
                    const apiClient = this.PluginOptions.ServerConnections.getApiClient(item.ServerId);
                    const isLiveTv = ['TvChannel', 'LiveTvChannel'].includes(item.Type);
                    mediaStreams = isLiveTv ? null : await apiClient.getItem(apiClient.getCurrentUserId(), mediaSourceId || item.Id)
                        .then((fullItem) => {
                            mediaSource = fullItem;
                            return fullItem.MediaStreams;
                        });
                } else {
                    mediaSource = item.MediaSources.find((entry) => entry.Id === mediaSourceId);
                    if (mediaSource == null) {
                        return;
                    }

                    mediaStreams = mediaSource.MediaStreams;
                }

                if (mediaStreams == null || mediaStreams.length === 0) {
                    return;
                }

                const stream = mediaStreams.find((entry) => entry.Type === 'Video');
                if (stream == null) {
                    return;
                }

                postMessage('enableFullscreen', {
                    videoWidth: stream.Width,
                    videoHeight: stream.Height,
                    videoFrameRate: stream.AverageFrameRate || stream.RealFrameRate,
                    videoRangeType: stream.VideoRangeType
                });

                await new Promise((resolve) => setTimeout(resolve, 3000));
            }
        }
    }

    window.UwpNativeVideoPlayerPlugin = async () => UwpNativeVideoPlayerPlugin;
    window.UwpXboxHdmiSetupPlugin = async () => UwpXboxHdmiSetupPlugin;

    if (!window.consoleXboxOverride) {
        window.consoleXboxOverride = true;
        const logOverride = function (logLevel) {
            const oldLogLevel = console[logLevel];
            console[logLevel] = function () {
                oldLogLevel.apply(console, arguments);
                const argsArray = Array.from(arguments);
                postMessage('log', {
                    level: logLevel,
                    messages: argsArray
                });
            };
        };

        // debug is intentionally commented out as it can overwhelm the interop layer. Uncomment for troubleshooting if needed.
        // logOverride('debug');
        logOverride('error');
        logOverride('log');
        logOverride('warn');
        logOverride('info');
    }
})(
    APP_NAME,
    APP_VERSION,
    DEVICE_NAME,
    SUPPORTS_HDR,
    SUPPORTS_DOVI,
    NATIVE_VIDEO_PLAYBACK_ENABLED,
    NATIVE_AUDIO_PASSTHROUGH_ENABLED,
    NATIVE_ALLOW_AC3,
    NATIVE_ALLOW_EAC3,
    NATIVE_ALLOW_DTS,
    NATIVE_ALLOW_TRUEHD
);
