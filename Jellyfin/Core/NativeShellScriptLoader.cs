using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Core.Contract;
using Jellyfin.Utils;
using Windows.Graphics.Display.Core;
using Windows.Storage;
using Windows.System.Profile;

namespace Jellyfin.Core;

/// <summary>
/// Injects the NativeShell javascript script to be able to interact with the UWP code.
/// </summary>
public class NativeShellScriptLoader : INativeShellScriptLoader
{
    private static readonly Uri StorageUri = new Uri("ms-appx:///Resources/winuwp.js");
    private readonly ISettingsManager _settingsManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeShellScriptLoader"/> class.
    /// </summary>
    /// <param name="settingsManager">The settings manager used to serialize shell configuration flags.</param>
    public NativeShellScriptLoader(ISettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
    }

    /// <summary>
    /// LoadNativeShellScript.
    /// </summary>
    /// <returns><see cref="Task"/>representing the asynchronous operation.</returns>
    public async Task<string> LoadNativeShellScript()
    {
        var storageFile = await StorageFile.GetFileFromApplicationUriAsync(StorageUri);
        var nativeShellScript = await FileIO.ReadTextAsync(storageFile);

        var assembly = Assembly.GetExecutingAssembly();
        nativeShellScript = nativeShellScript.Replace(
            "APP_NAME",
            Wrap(assembly.GetCustomAttribute<AssemblyTitleAttribute>().Title, '\''));
        nativeShellScript = nativeShellScript.Replace("APP_VERSION", Wrap(assembly.GetName().Version.ToString(), '\''));

        var deviceForm = AnalyticsInfo.DeviceForm;
        if (deviceForm == "Unknown")
        {
            deviceForm = AppUtils.GetDeviceFormFactorType().ToString();
        }

        deviceForm = DeviceFormFactorType.Xbox.ToString();

        nativeShellScript = nativeShellScript.Replace("DEVICE_NAME", Wrap(deviceForm, '\''));

        var hdmiDisplayInformation = HdmiDisplayInformation.GetForCurrentView();
        if (hdmiDisplayInformation != null)
        {
            var supportedDisplayModes = hdmiDisplayInformation.GetSupportedDisplayModes();
            var supportsHdr = supportedDisplayModes.Any(mode => mode.IsSmpte2084Supported);
            nativeShellScript = nativeShellScript.Replace("SUPPORTS_HDR", supportsHdr.ToString().ToLower());
            var supportsDovi = supportedDisplayModes.Any(mode => mode.IsDolbyVisionLowLatencySupported);
            nativeShellScript = nativeShellScript.Replace("SUPPORTS_DOVI", supportsDovi.ToString().ToLower());
        }
        else
        {
            nativeShellScript = nativeShellScript.Replace("SUPPORTS_HDR", "undefined");
            nativeShellScript = nativeShellScript.Replace("SUPPORTS_DOVI", "undefined");
        }

        nativeShellScript = nativeShellScript.Replace("NATIVE_VIDEO_PLAYBACK_ENABLED", _settingsManager.EnableNativeVideoPlayback.ToString().ToLowerInvariant());
        nativeShellScript = nativeShellScript.Replace("NATIVE_AUDIO_PASSTHROUGH_ENABLED", _settingsManager.EnableNativeAudioPassthrough.ToString().ToLowerInvariant());
        nativeShellScript = nativeShellScript.Replace("NATIVE_ALLOW_AC3", _settingsManager.AllowAc3DirectPlay.ToString().ToLowerInvariant());
        nativeShellScript = nativeShellScript.Replace("NATIVE_ALLOW_EAC3", _settingsManager.AllowEac3DirectPlay.ToString().ToLowerInvariant());
        nativeShellScript = nativeShellScript.Replace("NATIVE_ALLOW_DTS", _settingsManager.AllowDtsPassthrough.ToString().ToLowerInvariant());
        nativeShellScript = nativeShellScript.Replace("NATIVE_ALLOW_TRUEHD", _settingsManager.AllowTrueHdPassthrough.ToString().ToLowerInvariant());

        return nativeShellScript;
    }

    private static string Wrap(string text, char c)
    {
        return c + text + c;
    }
}
