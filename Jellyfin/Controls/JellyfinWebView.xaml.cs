using System.ComponentModel;
using System.Threading.Tasks;
using Jellyfin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Jellyfin.Controls;

/// <summary>
/// Represents a custom web view control for interacting with a Jellyfin server.
/// </summary>
public sealed partial class JellyfinWebView
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinWebView"/> class.
    /// </summary>
    public JellyfinWebView()
    {
        InitializeComponent();
        DataContext = App.Current.Services.GetRequiredService<JellyfinWebViewModel>();
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        Unloaded += OnUnloaded;
    }

    private void NativePlaybackOverlayHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel && jellyfinWebViewModel.TryHandleNativePlaybackInput(e.Key))
        {
            e.Handled = true;
        }
    }

    private void NativePlaybackPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.RequestNativePlaybackPlayPause();
        }
    }

    private void NativePlaybackFocusProxy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.RequestNativePlaybackOverlay();
        }
    }

    private void NativePlaybackRewindButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.RequestNativePlaybackSeek(-10000);
        }
    }

    private void NativePlaybackFastForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.RequestNativePlaybackSeek(10000);
        }
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JellyfinWebViewModel.IsNativePlaybackOverlayVisible)
            && e.PropertyName != nameof(JellyfinWebViewModel.IsNativePlayerVisible))
        {
            return;
        }

        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
        {
            await Task.Yield();

            if (DataContext is not JellyfinWebViewModel jellyfinWebViewModel || !jellyfinWebViewModel.IsNativePlayerVisible)
            {
                return;
            }

            if (jellyfinWebViewModel.IsNativePlaybackOverlayVisible && NativePlaybackPlayPauseButton.IsEnabled)
            {
                NativePlaybackPlayPauseButton.Focus(FocusState.Programmatic);
            }
            else
            {
                NativePlaybackFocusProxy.Focus(FocusState.Programmatic);
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        Unloaded -= OnUnloaded;
    }
}
