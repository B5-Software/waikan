using System.Windows;
using Waikan.Interop;
using Waikan.Services;
using Waikan.ViewModels;

namespace Waikan;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _hintShown;

    public MainWindow(MainViewModel viewModel, ThemeService themeService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => themeService.ApplyWindowTheme(this);
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var app = App.CurrentApp;
        if (app?.IsExiting == true)
        {
            return;
        }

        if (_viewModel.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                app?.ShowMinimizeHintOnce();
            }
        }
        else
        {
            // Finish the Closing event before asking the application to shut
            // down, otherwise Close() can be re-entered from this handler.
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => app?.RequestExit(), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
