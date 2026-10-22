using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Waikan.Interop;
using Waikan.Models;
using Waikan.ViewModels;

namespace Waikan;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _viewModel = new();
    private readonly DispatcherTimer _topmostTimer;
    private bool _isEnabled;

    public OverlayWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => PositionWindow();
        Loaded += (_, _) => PositionWindow();
        Closed += OnClosed;

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _topmostTimer.Tick += (_, _) => KeepOnTop();
        _topmostTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void SetCount(int count)
    {
        _viewModel.Count = count;
        RefreshOverlayLayout();
    }

    public void ApplySettings(AppSettings settings)
    {
        _viewModel.ApplySettings(settings);
        _isEnabled = settings.OverlayEnabled;

        if (_isEnabled)
        {
            if (!IsVisible)
            {
                Show();
            }

            RefreshOverlayLayout();
            PositionWindow();
            KeepOnTop();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private void RefreshOverlayLayout()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        InvalidateMeasure();
        UpdateLayout();
        PositionWindow();
    }

    public void Shutdown()
    {
        _topmostTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.AddExtendedStyle(
            hwnd,
            NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent);
        KeepOnTop();
    }

    private void PositionWindow()
    {
        if (!_isEnabled || !IsLoaded)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var targetLeft = workArea.Right - ActualWidth - 8;
        var targetTop = workArea.Top + 8;

        if (double.IsNaN(targetLeft) || double.IsInfinity(targetLeft))
        {
            return;
        }

        Left = Math.Max(workArea.Left, targetLeft);
        Top = Math.Max(workArea.Top, targetTop);
    }

    private void KeepOnTop()
    {
        if (!_isEnabled || !IsVisible)
        {
            return;
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.AssertTopmost(hwnd);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(PositionWindow, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _topmostTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
