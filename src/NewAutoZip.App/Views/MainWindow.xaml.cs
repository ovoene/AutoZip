using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NewAutoZip.App.ViewModels;
using Wpf.Ui.Tray.Controls;

namespace NewAutoZip.App.Views;

/// <summary>
/// 主窗口。
///
/// 关闭按钮 = 退到托盘继续工作（备份工具就该这样），真正退出只能从托盘菜单选"退出"。
/// 这一点必须让用户明确知道，所以第一次关闭时会把托盘提示文字改成带说明的版本。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _model;
    private bool _explicitExit;
    private bool _toldAboutTray;

    public MainWindow(MainViewModel model)
    {
        _model = model;

        InitializeComponent();

        DataContext = _model;

        FitToScreen();
    }

    /// <summary>
    /// 默认尺寸放不下时按可用工作区收一收。
    ///
    /// XAML 里的 1240×880 是照着"表格能一眼看到十几行"定的，1080p 及以上都装得下；
    /// 但小屏笔记本（1366×768）会让窗口下沿跑到屏幕外面，标题栏居中之后连拖都不好拖。
    /// 这里只在真的放不下时才改，正常情况下一行都不动。
    /// </summary>
    private void FitToScreen()
    {
        double maxWidth = SystemParameters.WorkArea.Width - 40;
        double maxHeight = SystemParameters.WorkArea.Height - 40;

        if (maxWidth > 0 && Width > maxWidth)
        {
            Width = Math.Max(MinWidth, maxWidth);
        }

        if (maxHeight > 0 && Height > maxHeight)
        {
            Height = Math.Max(MinHeight, maxHeight);
        }
    }

    /// <summary>托盘菜单里的"退出"。区别于关闭按钮 —— 只有这条路会真正结束进程。</summary>
    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _explicitExit = true;
        Close();
        Application.Current.Shutdown();
    }

    private void OnTrayShow(object sender, RoutedEventArgs e) => Restore();

    private void OnTrayDoubleClick(NotifyIcon sender, RoutedEventArgs e) => Restore();

    /// <summary>
    /// 托盘菜单里的"关于"。
    ///
    /// <b>不把主窗口叫回来</b>：用户点"关于"是想看版本号，不是想打开主界面。
    /// 弹出的是一个独立、非模态、无 Owner 的窗口（<see cref="AboutWindow"/>），
    /// 所以程序收在托盘里时也能单独看这一个小窗。
    ///
    /// 正文内容（版本、路径、运行环境、当前系统）由 ViewModel 拼好，这里只负责显示。
    /// </summary>
    private void OnTrayAbout(object sender, RoutedEventArgs e)
    {
        try
        {
            AboutWindow.ShowSingleton(_model);
        }
        catch (Exception)
        {
            // 只是个"关于"框，样式化窗口万一出问题也不该把程序带走，退回系统弹窗。
            // 这里也不能拿 this 当 Owner —— 托盘状态下主窗口是隐藏的。
            System.Windows.MessageBox.Show(
                _model.AboutText,
                _model.AboutTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>从托盘恢复。Topmost 弹一下是让窗口确实跑到前面来的标准做法。</summary>
    internal void Restore()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// 「启动时最小化到托盘」这条路：窗口不露面，但托盘图标必须出现。
    ///
    /// <b>为什么不能只 Hide()</b>
    /// 托盘图标是 XAML 里的 <c>tray:NotifyIcon</c>，它是主窗口可视树上的一个
    /// <c>FrameworkElement</c>，而 WPF-UI 是在它<b>第一次渲染</b>时（<c>OnRender</c>）
    /// 才去向 shell 登记这个图标的。从没 Show 过的窗口不会渲染，于是：
    /// 没有窗口、没有托盘图标、进程却还在（ShutdownMode 是 OnExplicitShutdown）——
    /// 用户看到的就是"勾了这个选项以后程序打不开了"，而且只能去任务管理器结束它。
    ///
    /// 所以这里真的 Show 一次，只是让用户看不见：
    /// 挪到屏幕外、不进任务栏、不抢焦点。WPF 照常建句柄、走布局、走渲染，
    /// 图标登记完再 Hide。这套「屏幕外 Show」的做法自检里也在用（见 SelfTest.Exercise）。
    /// </summary>
    /// <returns>托盘图标是否登记成功。false 说明这次启动用户找不到入口，调用方要说话。</returns>
    internal bool StartHiddenInTray()
    {
        WindowStartupLocation origin = WindowStartupLocation;

        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = -32000;
        Top = -32000;

        try
        {
            Show();
            UpdateLayout();

            // 渲染是异步排在 Render 优先级上的。把队列抽到比它更低的优先级，
            // 回来时 OnRender 一定已经跑过了 —— 图标也就登记过了。
            Drain();
        }
        finally
        {
            Hide();

            ShowInTaskbar = true;
            ShowActivated = true;
            WindowStartupLocation = origin;

            // 第一次 Show 已经把 CenterScreen 用掉了，之后 Show() 只认 Left/Top。
            // 不摆回来的话用户从托盘打开窗口，窗口还在 -32000 那个位置上。
            CenterOnWorkArea();
        }

        if (!Tray.IsRegistered)
        {
            // 渲染过了还没登记上：再明确要一次。图标位没了、Explorer 正在重启
            // 都会让第一次登记失败，而这两种情况往往下一刻就好了。
            try
            {
                Tray.Register();
            }
            catch (Exception)
            {
                // 登记不上就让调用方去把窗口摆出来 —— 那才是用户找得到的入口。
            }
        }

        return Tray.IsRegistered;
    }

    /// <summary>把 Dispatcher 队列抽干到 <c>ContextIdle</c>，含布局与渲染。</summary>
    private static void Drain()
    {
        DispatcherFrame frame = new();

        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));

        Dispatcher.PushFrame(frame);
    }

    /// <summary>按工作区居中，等价于 <c>WindowStartupLocation=CenterScreen</c> 的效果。</summary>
    private void CenterOnWorkArea()
    {
        Rect area = SystemParameters.WorkArea;

        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    /// <summary>
    /// 供 <c>--selftest</c> 使用：托盘右键菜单本体。
    /// 菜单是 <c>tray:NotifyIcon</c> 的一个属性，不在窗口的可视树里，
    /// 自检遍历可视树是找不到它的，只能这样递出来。
    /// </summary>
    internal ContextMenu? TrayMenu => Tray.Menu;

    /// <summary>
    /// 供 <c>--selftest</c> 使用：真正关掉窗口。
    /// 普通的 <see cref="Window.Close"/> 会被下面的 <see cref="OnClosing"/> 挡回去收进托盘，
    /// 自检跑完必须让窗口消失，否则进程退不出去。
    /// </summary>
    internal void CloseForSelfTest()
    {
        _explicitExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_explicitExit)
        {
            base.OnClosing(e);
            return;
        }

        // 收进托盘而不是退出。引擎还在跑，而用户以为已经关掉了 —— 那才是真的危险。
        e.Cancel = true;
        Hide();

        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            Tray.TooltipText = $"{_model.AppName}（仍在后台运行，右键托盘图标可退出）";
        }
    }
}
