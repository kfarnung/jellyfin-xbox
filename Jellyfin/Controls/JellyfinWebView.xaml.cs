using System.ComponentModel;
using System.Threading.Tasks;
using Jellyfin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Jellyfin.Controls;

/// <summary>
/// Represents a custom web view control for interacting with a Jellyfin server.
/// </summary>
public sealed partial class JellyfinWebView
{
    // Suppresses the ValueChanged→seek loop when the player (not the user) updates the scrubber.
    private bool _suppressSliderSeek;
    private bool _isPointerScrubbing;

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

    private void NativePlaybackScrubber_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderSeek || _isPointerScrubbing)
        {
            return;
        }

        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.RequestNativePlaybackSeekAbsolute(e.NewValue);
        }
    }

    private void NativePlaybackScrubber_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPointerScrubbing = true;
    }

    private void NativePlaybackScrubber_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerScrubbing)
        {
            return;
        }

        _isPointerScrubbing = false;
        if (DataContext is JellyfinWebViewModel jellyfinWebViewModel)
        {
            jellyfinWebViewModel.RequestNativePlaybackSeekAbsolute(NativePlaybackScrubber.Value);
        }
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JellyfinWebViewModel.NativePlaybackProgressValue)
            || e.PropertyName == nameof(JellyfinWebViewModel.NativePlaybackProgressMaximum))
        {
            if (!_isPointerScrubbing && DataContext is JellyfinWebViewModel vm)
            {
                _suppressSliderSeek = true;
                NativePlaybackScrubber.Maximum = vm.NativePlaybackProgressMaximum;
                NativePlaybackScrubber.Value = vm.NativePlaybackProgressValue;
                _suppressSliderSeek = false;
            }

            return;
        }

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
                FocusOrDeferAfterLayout(NativePlaybackPlayPauseButton);
            }
            else
            {
                NativePlaybackFocusProxy.Focus(FocusState.Programmatic);
            }
        });
    }

    // When the overlay transitions from Collapsed to Visible, buttons haven't gone through a
    // layout pass yet and Focus() silently returns false. Subscribe to LayoutUpdated to retry
    // once layout has completed.
    private static void FocusOrDeferAfterLayout(Control control)
    {
        if (!control.Focus(FocusState.Programmatic))
        {
            void OnLayoutUpdated(object s, object args)
            {
                control.LayoutUpdated -= OnLayoutUpdated;
                control.Focus(FocusState.Programmatic);
            }

            control.LayoutUpdated += OnLayoutUpdated;
        }
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
