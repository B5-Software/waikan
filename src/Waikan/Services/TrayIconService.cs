using System.Drawing;
using System.Windows.Forms;
using Waikan.Models;

namespace Waikan.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _overlayItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly Icon _icon;
    private readonly LogService _log;
    private bool _disposed;

    public TrayIconService(
        LogService log,
        Action openMainWindow,
        Action toggleOverlay,
        Action togglePause,
        Action toggleAutostart,
        Action exitApplication)
    {
        _log = log;
        _icon = LoadIcon();
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
        };

        var openItem = new ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => SafeInvoke(openMainWindow, "打开主界面");

        _overlayItem = new ToolStripMenuItem("显示悬浮窗")
        {
            CheckOnClick = false,
        };
        _overlayItem.Click += (_, _) => SafeInvoke(toggleOverlay, "切换悬浮窗");

        _pauseItem = new ToolStripMenuItem("暂停识别")
        {
            CheckOnClick = false,
        };
        _pauseItem.Click += (_, _) => SafeInvoke(togglePause, "切换识别");

        _autostartItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = false,
        };
        _autostartItem.Click += (_, _) => SafeInvoke(toggleAutostart, "切换开机自启");

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => SafeInvoke(exitApplication, "退出应用");

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_overlayItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => UpdateMenuChecks();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = AppPaths.AppDisplayName,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                SafeInvoke(openMainWindow, "打开主界面");
            }
        };
    }

    public Func<bool>? IsOverlayEnabled { get; set; }
    public Func<bool>? IsPaused { get; set; }
    public Func<bool>? IsAutostartEnabled { get; set; }

    public void UpdateStatus(EngineState state, int todayCount)
    {
        var stateText = state switch
        {
            EngineState.Listening => "聆听中",
            EngineState.Loading => "启动中",
            EngineState.Paused => "已暂停",
            EngineState.Error => "识别异常",
            _ => "已停止",
        };
        var text = $"外刊 · 今日 {todayCount} 次 · {stateText}";
        if (text.Length > 60)
        {
            text = text[..60];
        }

        _notifyIcon.Text = text;
        _pauseItem.Text = IsPaused?.Invoke() == true ? "继续识别" : "暂停识别";
    }

    public void Hide()
    {
        try
        {
            _notifyIcon.Visible = false;
        }
        catch
        {
            // Best effort.
        }
    }

    public void ShowMinimizeHint()
    {
        try
        {
            _notifyIcon.ShowBalloonTip(
                3200,
                AppPaths.AppDisplayName,
                "应用仍在后台识别。可双击托盘图标重新打开界面。",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Warn($"显示托盘提示失败：{ex.Message}");
        }
    }

    private void UpdateMenuChecks()
    {
        _overlayItem.Checked = IsOverlayEnabled?.Invoke() ?? false;
        _pauseItem.Text = IsPaused?.Invoke() == true ? "继续识别" : "暂停识别";
        _autostartItem.Checked = IsAutostartEnabled?.Invoke() ?? false;
    }

    private void SafeInvoke(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error($"托盘操作失败：{description}", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                using var associated = Icon.ExtractAssociatedIcon(exe);
                if (associated is not null)
                {
                    return (Icon)associated.Clone();
                }
            }
        }
        catch
        {
            // Fall through to a generated icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _menu.Dispose();
        _icon.Dispose();
    }
}
