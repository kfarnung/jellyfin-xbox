using System;
using Windows.Data.Json;

namespace Jellyfin.Core.Contract;

/// <summary>
/// Provides the message payload that should be forwarded from native playback back into the WebView.
/// </summary>
public sealed class NativePlaybackHostMessageEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NativePlaybackHostMessageEventArgs"/> class.
    /// </summary>
    /// <param name="type">The message type understood by the injected shell script.</param>
    /// <param name="args">The JSON payload associated with the message.</param>
    public NativePlaybackHostMessageEventArgs(string type, JsonObject args)
    {
        Type = type;
        Args = args ?? new JsonObject();
    }

    /// <summary>
    /// Gets the message type to forward to JavaScript.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the message payload to forward to JavaScript.
    /// </summary>
    public JsonObject Args { get; }
}
