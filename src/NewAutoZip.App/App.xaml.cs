using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NewAutoZip.App.Services;
using NewAutoZip.App.ViewModels;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.App;

/// <summary>
/// 应用入口。
///
/// 这里只解决三件旧版没解决的基础问题：
///   1. <b>单实例</b> —— 旧版可以随便开好几个，多个 Worker 同时往 ZipTemp 扔包、
///      同时写 checkpoint.txt，互相覆盖。
///   2. <b>全局异常兜底</b> —— 三个入口（UI 线程、后台线程、未观察的 Task）全部接住并写日志，
///      而不是弹一个系统崩溃框然后进程消失、日志里什么都没有。
///   3. <b>日志先于一切</b> —— 日志在任何业务代码之前就绪，所以启动阶段的失败也有记录。
/// </summary>
public partial class App : Application
{
    /// <summary>第二个实例用它唤起已经在运行的那个窗口。</summary>
    private const string ActivateEventSuffix = "-activate";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateSignal;
    private static RollingFileLogger? _fileLogger;

    private EngineHost? _host;
    private MainViewModel? _model;
    private ThemeService? _theme;

    /// <summary>进程级日志。UI 侧会再挂一个内存接收端上去。</summary>
    internal static IAppLogger Log { get; private set; } = NullLogger.Instance;

    /// <summary>文件日志的当前文件路径，供界面上的"打开日志"按钮使用。</summary>
    internal static string LogFilePath { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 异常兜底必须在最前面 —— 连启动过程本身都要被它保护。
        InstallGlobalExceptionHandlers();

        // 界面自检：不抢单实例锁（要允许在程序正常运行时随时自检），不启动引擎，
        // 跑完就带着退出码结束。详见 SelfTest。
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            StartLogging();
            base.OnStartup(e);
            Shutdown(SelfTest.Run(Log));
            return;
        }

        if (!TryAcquireSingleInstance())
        {
            // 已经有一个在跑：让那个窗口显示出来，自己安静退出。
            SignalExistingInstance();
            Shutdown(0);
            return;
        }

        StartLogging();
        base.OnStartup(e);

        // 界面日志接收端。文件日志与界面日志是两条独立的路：
        // 界面按用户选的级别过滤、有条数上限；文件日志始终完整，供事后排查。
        UiLogSink sink = new();
        IAppLogger logger = new CompositeLogger(Log, sink);

        _host = new EngineHost(logger);

        // 主题必须在<b>建窗口之前</b>应用。
        // 反过来的话窗口会先按 App.xaml 里的初值（深色）画一遍，然后当场刷成浅色 ——
        // 用户看到的就是启动时闪一下黑。
        _theme = new ThemeService(logger);
        _theme.Apply(_host.Settings);

        _model = new MainViewModel(_host, sink, logger, _theme);

        Views.MainWindow window = new(_model);
        MainWindow = window;

        // 挂窗口是为了"跟随系统"能实时生效。要在窗口建好之后 ——
        // 监听需要窗口句柄，ThemeService 会自己处理句柄还没有的情况（最小化启动）。
        _theme.Attach(window);

        // 「启动时最小化到托盘」= 随系统启动 + 启动后收在托盘里，一个开关管两件事。
        //
        // 自启动登记每次启动都同步一遍，不是只在保存配置时写：程序被挪走或换目录
        // 覆盖升级之后，注册表里那条路径就指向一个不存在的 exe，开机时静默失败。
        // 这里顺手改正（详见 StartupRegistration）。
        StartupRegistration.Sync(_host.Settings.StartMinimized, Log);

        if (_host.Settings.StartMinimized)
        {
            // 不能只 Hide() —— 托盘图标是主窗口可视树上的元素，窗口从没渲染过
            // 就没有图标，用户会得到一个"没有窗口也没有托盘图标"的活进程。
            // 详见 MainWindow.StartHiddenInTray。
            if (window.StartHiddenInTray())
            {
                Log.Info("已按配置最小化到托盘启动。");
            }
            else
            {
                // 托盘图标没登记上就等于没有入口。宁可把窗口摆出来，
                // 也不要让程序变成一个只能去任务管理器结束的隐形进程。
                Log.Warn("托盘图标登记失败，改为正常显示主窗口。");
                window.Restore();
            }
        }
        else
        {
            window.Show();
        }

        // 自动启动引擎放在窗口构建之后：校验不通过时提示条已经能显示了。
        if (_host.Settings.AutoStartEngine)
        {
            _model.StartCommand.Execute(null);
        }

        ListenForActivationRequests();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 顺序要紧：先停引擎（它还在往日志里写东西），再关日志文件。
        // 反过来的话最后那几条最有价值的停止记录会丢掉。
        try
        {
            _model?.Dispose();
            _theme?.Dispose();

            if (_host is { } host)
            {
                // 退出路径上不能无限期等待。给 30 秒够 7za 收到 Kill 并退出；
                // 真的卡住了也必须让进程结束，否则用户会看到一个关不掉的程序。
                if (!host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30)))
                {
                    Log.Warn("等待引擎停止超过 30 秒，强制退出。");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("退出时停止引擎出错。", ex);
        }

        Log.Info("程序退出。");

        _fileLogger?.Dispose();
        _activateSignal?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 不是本线程持有的，忽略
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    // ==================================================================
    //  单实例
    // ==================================================================

    /// <summary>
    /// 互斥锁的名字按<b>数据目录</b>派生，而不是写死一个常量。
    ///
    /// 需要拦的是"两个引擎写同一份 state.json / 同一个 ZipTemp"，而这份数据是跟着
    /// 数据目录走的。因此：同一用户的两个会话（控制台 + 远程桌面）会被拦住（数据目录相同），
    /// 而不同用户各自的 %LOCALAPPDATA% 互不干扰，可以同时运行 —— 后者用一个写死的
    /// Global 名字反而会被错误地挡掉。
    /// </summary>
    private static string InstanceKey()
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(AppPaths.DataRoot.ToUpperInvariant()));

        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// 单实例互斥锁与唤起事件的内核对象名。三处用到，写一次。
    /// </summary>
    private static string SyncObjectName(string suffix = "") =>
        $"Global\\{AppPaths.ProductName}-{InstanceKey()}{suffix}";

    private static bool TryAcquireSingleInstance()
    {
        string name = SyncObjectName();

        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name, out bool createdNew);

            if (createdNew)
            {
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // 锁存在但当前账户打不开它 —— 依然说明"有一个正在跑"，如实按已运行处理。
            _singleInstanceMutex = null;
            return false;
        }
        catch (Exception)
        {
            // 拿不到锁的原因如果不是"已存在"，宁可放行也不要因为一个辅助机制拒绝启动。
            _singleInstanceMutex = null;
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(
                    SyncObjectName(ActivateEventSuffix),
                    out EventWaitHandle? signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
            else
            {
                MessageBox.Show(
                    $"{AppPaths.ProductName} 已经在运行。请在任务栏右下角的托盘图标里打开它。",
                    AppPaths.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception)
        {
            // 唤起失败不影响"不重复启动"这个主要目的。
        }
    }

    /// <summary>后台等待第二个实例的唤起信号，收到就把主窗口拉到前台。</summary>
    private void ListenForActivationRequests()
    {
        try
        {
            _activateSignal = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                SyncObjectName(ActivateEventSuffix));
        }
        catch (Exception ex)
        {
            Log.Debug($"无法创建唤起事件，第二次启动将只提示而不能自动打开窗口：{ex.Message}");
            return;
        }

        EventWaitHandle signal = _activateSignal;

        Thread listener = new(() =>
        {
            while (true)
            {
                try
                {
                    signal.WaitOne();
                }
                catch (Exception)
                {
                    return;     // 句柄已释放，说明程序正在退出
                }

                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is { } window)
                        {
                            window.Show();
                            window.WindowState = WindowState.Normal;
                            window.Activate();
                        }
                    });
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "AutoZip-Activate",
        };

        listener.Start();
    }

    // ==================================================================
    //  日志
    // ==================================================================

    private static void StartLogging()
    {
        try
        {
            AppPaths.EnsureCreated();

            _fileLogger = new RollingFileLogger(AppPaths.LogDirectory, SecretRedactor.Shared);
            LogFilePath = _fileLogger.CurrentFile;
            Log = _fileLogger;

            Log.Info("================================================");
            Log.Info($"{AppPaths.ProductName} {AppVersion()} 启动");
            Log.Info($"数据目录：{AppPaths.DataRoot}");
            Log.Info($"程序目录：{AppPaths.BaseDirectory}");

            if (AppPaths.DataRootFallbackReason is { } reason)
            {
                Log.Warn(reason);
            }

            Log.Info($"压缩程序：{AppPaths.SevenZipExe}");
        }
        catch (Exception ex)
        {
            // 日志都起不来（目录不可写等）时不能直接崩 —— 换成无日志模式继续跑，并明确告知用户。
            Log = NullLogger.Instance;

            MessageBox.Show(
                $"无法初始化日志（{ex.Message}）。{Environment.NewLine}" +
                "程序仍会运行，但这次不会留下日志文件。",
                AppPaths.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 程序名，取自程序集元数据（<c>Directory.Build.props</c> 的 Product）。
    /// </summary>
    internal static string AppName => AppPaths.ProductName;

    internal static string AppVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "6.6.6";

    // ==================================================================
    //  全局异常兜底
    // ==================================================================

    private void InstallGlobalExceptionHandlers()
    {
        // 1) UI 线程。能接住就接住，界面继续可用。
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("界面线程未处理的异常。", args.Exception);
            args.Handled = true;

            ShowCrashNotice(args.Exception, fatal: false);
        };

        // 2) 任意后台线程。到这里已经无法挽回，只能保证留下记录。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Error("后台线程未处理的异常，进程即将结束。", ex);
            }
            else
            {
                Log.Error($"后台线程未处理的异常（非 Exception 对象）：{args.ExceptionObject}");
            }

            _fileLogger?.Flush();
        };

        // 3) 没有被 await 的 Task 抛出的异常。默认会被静默吞掉 —— 那正是旧版"什么都没发生
        //    但程序不干活了"的一类来源。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("有一个后台任务的异常没有被观察。", args.Exception);
            args.SetObserved();
        };
    }

    private static void ShowCrashNotice(Exception ex, bool fatal)
    {
        try
        {
            string title = fatal
                ? $"{AppPaths.ProductName} 遇到无法恢复的错误"
                : $"{AppPaths.ProductName} 遇到一个错误";

            MessageBox.Show(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}{1}{1}{2}{1}{1}详细信息已写入日志：{3}",
                    ex.Message,
                    Environment.NewLine,
                    ex.GetType().Name,
                    string.IsNullOrEmpty(LogFilePath) ? "（本次未启用日志）" : LogFilePath),
                title,
                MessageBoxButton.OK,
                fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            // 连提示都弹不出来就算了，日志已经写下了。
        }
    }
}
