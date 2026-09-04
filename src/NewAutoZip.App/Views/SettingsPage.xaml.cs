using System.IO;
using Microsoft.Win32;
using NewAutoZip.App.ViewModels;

namespace NewAutoZip.App.Views;

/// <summary>
/// 设置页。
///
/// 界面改的是 <see cref="SettingsViewModel"/> 这份副本，只有按“保存”才写回配置文件，
/// 所以“放弃改动”是免费的，也不会出现改了一半的配置被引擎读走。
/// </summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage() => InitializeComponent();

    /// <summary>DataContext 是 <see cref="MainViewModel"/>，设置项都挂在它的 Settings 下面。</summary>
    private SettingsViewModel? Model => (DataContext as MainViewModel)?.Settings;

    private void OnBrowseMonitor(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model is not { } model) { return; }

        if (Pick("选择要监控的目录", model.MonitorPath) is { } picked)
        {
            model.MonitorPath = picked;
        }
    }

    private void OnBrowseCloud(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model is not { } model) { return; }

        if (Pick($"选择{model.CloudPathLabel}", model.CloudPath) is { } picked)
        {
            model.CloudPath = picked;
        }
    }

    private void OnBrowseZipTemp(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model is not { } model) { return; }

        if (Pick("选择临时压缩包存放目录", model.ZipTempPath) is { } picked)
        {
            model.ZipTempPath = picked;
        }
    }

    /// <summary>把注册表里发现的账号目录填进输入框 —— 手抄这种路径很容易抄错。</summary>
    private void OnUseAccount(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model is not { } model) { return; }

        if (AccountPicker.SelectedItem is OneDriveChoice choice)
        {
            model.CloudPath = choice.Path;
        }
    }

    /// <summary>
    /// 选背景图。
    ///
    /// 筛选器第一项写全部支持的扩展名而不是 <c>*.*</c>：这个对话框里能选到的
    /// 每一种格式都是 WPF 的 <c>BitmapDecoder</c> 认得的，选完不会得到一句
    /// “认不出这个图片格式”。第二项仍留 <c>*.*</c>，因为扩展名不对但内容是图片
    /// 的文件确实存在，不该拦死。
    /// </summary>
    private void OnPickBackground(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model is not { } model) { return; }

        OpenFileDialog dialog = new()
        {
            Title = "选择背景图片",
            Multiselect = false,
            CheckFileExists = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
        };

        string current = model.BackgroundImagePath;

        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                string? folder = Path.GetDirectoryName(current);

                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    dialog.InitialDirectory = folder;
                }
            }
            catch (ArgumentException)
            {
                // 路径里有非法字符（用户手敲的）。开在默认位置就行，不该为此弹错误。
            }
        }

        if (dialog.ShowDialog() == true)
        {
            model.BackgroundImagePath = dialog.FileName;
        }
    }

    private static string? Pick(string title, string current)
    {
        OpenFolderDialog dialog = new()
        {
            Title = title,
            Multiselect = false,
        };

        // 已填的路径若还存在就从那里开始，省得每次从"此电脑"翻起。
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
