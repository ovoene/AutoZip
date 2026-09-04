using System.Windows;

using NewAutoZip.App.ViewModels;

namespace NewAutoZip.App.Views;

/// <summary>
/// 关于窗口。
///
/// 独立、非模态、无 Owner —— 点"关于"不该把主窗口拽出来。
/// 单实例：再点一次是把已经开着的这个拉到前台，而不是叠第二个。
/// </summary>
public partial class AboutWindow : Wpf.Ui.Controls.FluentWindow
{
    private static AboutWindow? _open;

    private AboutWindow(MainViewModel model)
    {
        InitializeComponent();

        DataContext = model;
    }

    /// <summary>
    /// 显示关于窗口。已经开着就把它激活。
    /// </summary>
    internal static void ShowSingleton(MainViewModel model)
    {
        if (_open is { } existing)
        {
            existing.Restore();
            return;
        }

        AboutWindow window = new(model);
        _open = window;

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, window))
            {
                _open = null;
            }
        };

        window.Show();
        window.Restore();
    }

    /// <summary>
    /// 拉到前台。无 Owner 的窗口在别的程序有焦点时不一定跑到最上面，
    /// Topmost 弹一下是让它确实可见的标准做法。
    /// </summary>
    private void Restore()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
