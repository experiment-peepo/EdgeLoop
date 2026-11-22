using FlyleafLib.MediaPlayer;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlyleafLib.Controls.WPF;

public class FlyleafD3DImage : Image, IDisposable
{
    public Player Player
    {
        get => (Player)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register("Player", typeof(Player), typeof(FlyleafD3DImage), new PropertyMetadata(null, OnPlayerChanged));

    private D3DImage d3dImage;
    private bool disposed;
    private long lastFrameCount = -1;
    private int errorCount;

    public FlyleafD3DImage()
    {
        d3dImage = new D3DImage();
        this.Source = d3dImage;
        this.Stretch = Stretch.Uniform;

        this.Loaded += (s, e) =>
        {
            FlyleafLib.Logger.Log("[V] Control Loaded", LogLevel.Debug);
            StartRendering();
            d3dImage.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        };
        this.Unloaded += (s, e) =>
        {
            FlyleafLib.Logger.Log("[V] Control Unloaded", LogLevel.Debug);
            StopRendering();
            d3dImage.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        };
    }

    private void OnIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        FlyleafLib.Logger.Log($"[V] Front buffer {(d3dImage.IsFrontBufferAvailable ? "available" : "lost")}", LogLevel.Info);
        if (d3dImage.IsFrontBufferAvailable)
            Rebuild(); // Fork: WPF rebuilt its device, so ours may be stale too
        else
            StopRendering();
    }

    /// <summary>Fork: a display changed (resolution, a monitor switching HDR): the shared surface may no longer show.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(Rebuild, System.Windows.Threading.DispatcherPriority.Render);

    /// <summary>Fork: a new shared surface and D3D9 device, then rendering again from the next frame.</summary>
    private void Rebuild()
    {
        if (disposed || Player?.Renderer?.D3DImage is not { IsEnabled: true, Disposed: false } manager)
            return;

        var (width, height) = PixelSize();
        manager.Recreate(width, height);
        lastFrameCount = -1;
        StartRendering();
    }

    /// <summary>The control's size in the screen's real pixels, not WPF's scaled units.</summary>
    private (int Width, int Height) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (width > 0 && height > 0)
            return (width, height);

        var window = Window.GetWindow(this);
        if (window != null && window.ActualWidth > 0 && window.ActualHeight > 0)
            return ((int)Math.Round(window.ActualWidth * dpi.DpiScaleX), (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
        return (1920, 1080);
    }

    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FlyleafD3DImage)d;
        FlyleafLib.Logger.Log($"[V] Player Changed: {e.OldValue != null} -> {e.NewValue != null}", LogLevel.Debug);
        if (e.OldValue is Player oldPlayer)
        {
            oldPlayer.Renderer.D3DImage.IsEnabled = false;
        }

        if (e.NewValue is Player newPlayer)
        {
            newPlayer.Renderer.D3DImage.IsEnabled = true;
            control.StartRendering();
        }
    }

    private void StartRendering()
    {
        if (Player == null)
        {
            FlyleafLib.Logger.Log("[V] StartRendering skipped: Player is null", LogLevel.Debug);
            return;
        }

        FlyleafLib.Logger.Log("[V] StartRendering", LogLevel.Debug);
        CompositionTarget.Rendering -= OnRendering; // Ensure no double registration
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        CompositionTarget.Rendering -= OnRendering;
    }

    private int renderCount = 0;
    private void OnRendering(object? sender, EventArgs e)
    {
        if (disposed)
            return;
        var p = Player;
        if (p == null)
            return;

        try
        {
            var renderer = p.Renderer;
            if (renderer == null || renderer.Disposed)
                return;

            var manager = renderer.D3DImage;
            if (manager == null || !manager.IsEnabled || !d3dImage.IsFrontBufferAvailable)
                return;

            if (manager.FrameCount == lastFrameCount)
                return;
            lastFrameCount = manager.FrameCount;

            // Fork: in the screen's real pixels, not WPF's scaled units, so video on a 150% 4K screen is
            // drawn at 3840x2160 rather than drawn at 2560x1440 and stretched by WPF
            var (width, height) = PixelSize();
            manager.EnsureSize(width, height);

            var surface = manager.SharedSurface9;
            if (surface == null || surface.NativePointer == nint.Zero)
                return;

            d3dImage.Lock();
            try
            {
                manager.AcquireWPF();
                try
                {
                    d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer);
                    d3dImage.AddDirtyRect(new Int32Rect(0, 0, d3dImage.PixelWidth, d3dImage.PixelHeight));
                }
                finally
                {
                    manager.ReleaseWPF();
                }
            }
            finally
            {
                d3dImage.Unlock();
            }

            if (renderCount++ % 300 == 0)
                FlyleafLib.Logger.Log($"[V] OnRendering | {width}x{height} | Status:{p.Status}", LogLevel.Debug);
        }
        catch (ObjectDisposedException) { StopRendering(); }
        catch (Exception ex)
        {
            // Fork: the first error and every 60th after it (renderCount doesn't move while every frame fails)
            if (errorCount++ % 60 == 0)
                FlyleafLib.Logger.Log($"[V] Error in OnRendering ({errorCount}): {ex.Message}", LogLevel.Error);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        StopRendering();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (Player != null)
            Player.Renderer.D3DImage.IsEnabled = false;
        disposed = true;
    }
}
