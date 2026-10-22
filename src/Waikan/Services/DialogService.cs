using System.Diagnostics;
using System.Windows;
using Waikan.Infrastructure;

namespace Waikan.Services;

public interface IDialogService
{
    bool Confirm(string title, string message, string confirmText = "确定");
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    string? PickSaveFile(string title, string filter, string defaultFileName);
    string? PickColor(string initialColor);
    void OpenFolder(string folderPath);
    void OpenFile(string filePath);
}

public sealed class DialogService : IDialogService
{
    private readonly LogService _log;

    public DialogService(LogService log)
    {
        _log = log;
    }

    public bool Confirm(string title, string message, string confirmText = "确定")
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void ShowInfo(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickColor(string initialColor)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            AnyColor = true,
            Color = ToDrawingColor(initialColor),
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return null;
        }

        return ColorUtilities.ToHex(
            System.Windows.Media.Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B));
    }

    public void OpenFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开文件夹失败。", ex);
            ShowError("无法打开文件夹", ex.Message);
        }
    }

    public void OpenFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                ShowInfo("文件不存在", filePath);
                return;
            }

            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开文件失败。", ex);
            ShowError("无法打开文件", ex.Message);
        }
    }

    private static System.Drawing.Color ToDrawingColor(string value)
    {
        var color = ColorUtilities.ParseOrFallback(value, System.Windows.Media.Color.FromArgb(255, 255, 246, 242));
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
