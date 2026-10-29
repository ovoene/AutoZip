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
using NewAutoZip.Core.Startup;

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

    /// <summary>
    /// 后台实例（开机自启、无界面）等的那个信号：界面版打开时置位它，后台实例停引擎、退出，
    /// 把单实例锁让出来。这个事件只有后台实例会建，所以"它存在"本身就说明锁的主人是后台实例。
    /// </summary>
    private const string TakeoverEventSuffix = "-takeover";

    /// <summary>界面版等后台实例让出单实例锁最多等多久。引擎停止最长要 30 秒（见 OnExit）。</summary>
    private static readonly TimeSpan TakeoverWait = TimeSpan.FromSeconds(60);

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateSignal;
    private static EventWaitHandle? _takeoverSignal;
    private static RollingFileLogger? _fileLogger;

    /// <summary>
    /// 本进程是开机自启拉起来的后台实例（<c>--background</c>）。
    /// 这种进程跑在没人看得见的会话里 —— <b>绝不能弹任何对话框</b>，
    /// 弹了就是一个永远没人点的模态框，把整个进程卡死在那里。
    /// </summary>
    private static bool _background;

    private EngineHost? _host;
    private MainViewModel? _model;
    private ThemeService? _theme;
    private BackgroundSupervisor? _supervisor;

    /// <summary>退出时把备份交还给后台实例（开机自启已注册时）。见 <see cref="OnExit"/>。</summary>
    private bool _handBackToBackground;

    /// <summary>进程级日志。UI 侧会再挂一个内存接收端上去。</summary>
    internal static IAppLogger Log { get; private set; } = NullLogger.Instance;

    /// <summary>
    /// 设置里的日志级别同步到日志文件。
    ///
    /// 文件日志的底线是「普通」—— 它是事后排查的唯一依据，用户把界面调成"只看警告"
    /// 不该让文件里也跟着少了记录；但选了「调试」就得真的把调试信息记进文件。
    /// 原先文件这一侧从没接上这个设置：选「调试」只是界面上多几行，文件里一条都没有，
    /// 而后台实例没有界面，调试信息就彻底无处可看。
    /// </summary>
    internal static void ApplyLogLevel(LogLevel level)
    {
        if (_fileLogger is { } file)
        {
            file.MinimumLevel = level < LogLevel.Info ? level : LogLevel.Info;
        }
    }

    /// <summary>文件日志的当前文件路径，供界面上的"打开日志"按钮使用。</summary>
    internal static string LogFilePath { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 异常兜底必须在最前面 —— 连启动过程本身都要被它保护。
        InstallGlobalExceptionHandlers();

        // 界面自检：不抢单实例锁（要允许在程序正常运行时随时自检），不启动引擎，
        // 跑完就带着退出码结束。详见 SelfTest。
        if (HasArgument(e.Args, "--selftest"))
        {
            StartLogging();
            base.OnStartup(e);
            Shutdown(SelfTest.Run(Log));
            return;
        }

        // 注册 / 取消开机自启的提权子进程。同样不抢锁、不启动引擎，做完就走。
        if (HasArgument(e.Args, BootTaskRegistrar.Argument))
        {
            StartLogging("开机自启登记（提权进程）");
            base.OnStartup(e);
            Shutdown(BootTaskRegistrar.RunElevatedCommand(e.Args, Log));
            return;
        }

        // 开机自启任务拉起来的后台实例：无界面，只跑引擎。
        if (HasArgument(e.Args, BootTaskDefinition.BackgroundArgument))
        {
            RunInBackground(e);
            return;
        }

        bool tookOver = false;

        if (!TryAcquireSingleInstance(TimeSpan.Zero) && !TryTakeOverFromBackground(out tookOver))
        {
            // 已经有一个界面在跑（它的窗口已被唤起），或者用户不想接管后台实例：自己安静退出。
            Shutdown(0);
            return;
        }

        StartLogging();
        base.OnStartup(e);

        if (tookOver)
        {
            Log.Info("已从后台实例（开机自启）接管：引擎改由界面运行，退出界面时交还给后台。");
        }

        // 界面日志接收端。文件日志与界面日志是两条独立的路：
        // 界面按用户选的级别过滤、有条数上限；文件日志始终完整，供事后排查。
        UiLogSink sink = new();
        IAppLogger logger = new CompositeLogger(Log, sink);

        _host = new EngineHost(logger);
        ApplyLogLevel(_host.Settings.LogLevel);

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

        // 「启动时最小化到托盘」= 随登录启动 + 启动后收在托盘里，一个开关管两件事。
        // 这是改版前的自启方式，现在由「开机自启（无需登录）」取代，只给老配置留着。
        //
        // 自启动登记每次启动都同步一遍，不是只在保存配置时写：程序被挪走或换目录
        // 覆盖升级之后，注册表里那条路径就指向一个不存在的 exe，开机时静默失败。
        // 这里顺手改正（详见 StartupRegistration）。
        //
        // 开机自启已注册时一律撤掉登录自启：两个都在的话，每次登录都会弹出界面来接管后台实例。
        StartupRegistration.Sync(_host.Settings.StartMinimized && !_model.BootTaskRegistered, Log);

        // 接管来的界面一定要露面：用户双击 exe 正是为了看它。
        if (_host.Settings.StartMinimized && !tookOver)
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
        // 从后台实例接管来的，不看这个开关也要接着跑 —— 接管前备份本来就在跑，
        // 打开个界面就把它停了，等于"看一眼"会让备份中断。
        if (_host.Settings.AutoStartEngine || tookOver)
        {
            _model.StartCommand.Execute(null);
        }

        ListenForActivationRequests();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 交不交还给后台，在 ViewModel 被释放之前问清楚。
        // 用户从托盘退出、注销、关机都会走到这里：开机自启已注册的话，这三种情况都该让后台接上。
        _handBackToBackground = !_background && _model?.BootTaskRegistered == true;

        // 顺序要紧：先停引擎（它还在往日志里写东西），再关日志文件。
        // 反过来的话最后那几条最有价值的停止记录会丢掉。
        try
        {
            _supervisor?.Dispose();
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

        if (_handBackToBackground)
        {
            Log.Info("界面退出：开机自启已注册，备份交还给后台实例。");
        }

        Log.Info(_background ? "后台实例退出。" : "程序退出。");

        _fileLogger?.Dispose();
        _activateSignal?.Dispose();
        _takeoverSignal?.Dispose();

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

        // 必须在放开单实例锁<b>之后</b>：先拉起来的话，后台实例一看锁还被占着，就当"已有实例"退出了。
        // 拉不起来也不要紧，看门狗最迟几分钟后会补上（见 BootTaskDefinition.WatchdogInterval）。
        if (_handBackToBackground)
        {
            BootTaskScheduler.TryRunNow(TimeSpan.FromSeconds(5));
        }

        base.OnExit(e);
    }

    private static bool HasArgument(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    // ==================================================================
    //  后台模式（开机自启）
    // ==================================================================

    /// <summary>
    /// 开机自启任务拉起来的进程：不建窗口、不建托盘图标，只把引擎跑起来。
    ///
    /// 跑在没人登录的会话里，所以这条路上<b>不弹任何对话框</b>（见 <see cref="_background"/>），
    /// 有问题一律写日志。
    /// </summary>
    private void RunInBackground(StartupEventArgs e)
    {
        _background = true;

        // 看门狗每隔几分钟就拉一次：已经有实例在跑（界面也好、后台也好）是常态。
        // 这时一声不响地退出 —— 连日志都不写，否则一天几百行"已在运行"。
        // 等的这几秒是给"界面刚退出、正在放锁"那一刻留的余量。
        if (!TryAcquireSingleInstance(TimeSpan.FromSeconds(5)))
        {
            Shutdown(0);
            return;
        }

        StartLogging("后台模式（开机自启，无界面）");
        base.OnStartup(e);

        Log.Info(
            $"后台实例启动：账户 {Environment.UserDomainName}\\{Environment.UserName}，" +
            $"会话 {CurrentSessionId()}。" +
            "打开界面时会接管这个实例，退出界面后再交还回来。");

        _host = new EngineHost(Log);
        ApplyLogLevel(_host.Settings.LogLevel);

        ListenForTakeoverRequest();

        _supervisor = new BackgroundSupervisor(_host, Log);
        _supervisor.Start();
    }

    /// <summary>会话号。0 = 没人登录时任务计划程序给的那个会话；排查"后台到底跑没跑"时有用。</summary>
    private static int CurrentSessionId()
    {
        try
        {
            using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>后台实例：等界面版的接管信号，收到就退出（引擎在 OnExit 里停）。</summary>
    private void ListenForTakeoverRequest()
    {
        try
        {
            _takeoverSignal = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                SyncObjectName(TakeoverEventSuffix));
        }
        catch (Exception ex)
        {
            Log.Warn("无法创建接管事件：打开界面时将无法接管这个后台实例，只能等它自己结束。", ex);
            return;
        }

        EventWaitHandle signal = _takeoverSignal;

        Thread listener = new(() =>
        {
            try
            {
                signal.WaitOne();
            }
            catch (Exception)
            {
                return;     // 句柄已释放，说明进程正在退出
            }

            Log.Info("界面版请求接管：后台实例停止引擎并退出。");

            try
            {
                Dispatcher.BeginInvoke(() => Shutdown(0));
            }
            catch (Exception)
            {
                // Dispatcher 已经关了 —— 进程本来就在退出。
            }
        })
        {
            IsBackground = true,
            Name = "AutoZip-Takeover",
        };

        listener.Start();
    }

    /// <summary>
    /// 单实例锁被占着时：锁的主人如果是后台实例，问用户要不要接管；是界面版就唤起它的窗口。
    /// 返回 true 表示现在锁归我了（<paramref name="tookOver"/> 同时为 true）。
    /// </summary>
    private static bool TryTakeOverFromBackground(out bool tookOver)
    {
        tookOver = false;

        EventWaitHandle? takeover = null;

        try
        {
            EventWaitHandle.TryOpenExisting(SyncObjectName(TakeoverEventSuffix), out takeover);
        }
        catch (Exception)
        {
            takeover = null;
        }

        if (takeover is null)
        {
            // 锁的主人是另一个界面版：让那个窗口显示出来。
            SignalExistingInstance();
            return false;
        }

        using (takeover)
        {
            MessageBoxResult answer = MessageBox.Show(
                $"{AppPaths.ProductName} 正在后台运行（开机自启，没有界面），备份照常在进行。{Environment.NewLine}{Environment.NewLine}" +
                "打开界面需要先让后台实例退出，由界面接着运行备份：" + Environment.NewLine +
                "  · 如果它正在压缩，这一包会中止，稍后自动重做，不会漏文件；" + Environment.NewLine +
                "  · 已经压好、正在验证或等着移动的包会接着验证、移动，不重做；" + Environment.NewLine +
                "  · 等待上传确认的压缩包会接着跟踪。" + Environment.NewLine +
                $"从托盘退出界面时，备份会交还给后台继续运行。{Environment.NewLine}{Environment.NewLine}" +
                "现在打开界面吗？",
                AppPaths.ProductName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }

            takeover.Set();
        }

        if (TryAcquireSingleInstance(TakeoverWait))
        {
            tookOver = true;
            return true;
        }

        MessageBox.Show(
            $"后台实例没有在 {TakeoverWait.TotalSeconds:0} 秒内退出，界面没有打开。{Environment.NewLine}" +
            "它可能正卡在一次很慢的磁盘操作上。请稍后再试；也可以在任务管理器里结束后台的 AutoZip 进程。",
            AppPaths.ProductName,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        return false;
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

    /// <summary>
    /// 拿单实例锁。<paramref name="wait"/> 为零时只试一次；大于零时最多等这么久，
    /// 等的是锁的主人（另一个进程）把它放开 —— 接管后台实例、后台实例等界面放锁，都靠这个。
    /// </summary>
    private static bool TryAcquireSingleInstance(TimeSpan wait)
    {
        string name = SyncObjectName();
        DateTime deadline = DateTime.UtcNow + wait;

        while (true)
        {
            Mutex? mutex = null;

            try
            {
                mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);

                if (createdNew)
                {
                    _singleInstanceMutex = mutex;
                    return true;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;

                if (remaining > TimeSpan.Zero && mutex.WaitOne(remaining))
                {
                    _singleInstanceMutex = mutex;
                    return true;
                }

                mutex.Dispose();
                return false;
            }
            catch (AbandonedMutexException)
            {
                // 上一个主人没放锁就死了（被结束进程、崩溃）。锁照样归我们，这不是错误。
                _singleInstanceMutex = mutex;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 锁存在但当前账户打不开它 —— 依然说明"有一个正在跑"。
                // 要等的话就隔一会儿再试：主人一退出，这个内核对象就没了，下一次能新建。
                mutex?.Dispose();

                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                Thread.Sleep(500);
            }
            catch (Exception)
            {
                // 拿不到锁的原因如果不是"已存在"，宁可放行也不要因为一个辅助机制拒绝启动。
                mutex?.Dispose();
                _singleInstanceMutex = null;
                return true;
            }
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

    /// <param name="role">这一次是以什么身份起来的（后台模式、提权进程）。界面版留空。</param>
    private static void StartLogging(string? role = null)
    {
        try
        {
            AppPaths.EnsureCreated();

            _fileLogger = new RollingFileLogger(AppPaths.LogDirectory, SecretRedactor.Shared);
            LogFilePath = _fileLogger.CurrentFile;
            Log = _fileLogger;

            Log.Info("================================================");
            Log.Info(role is null
                ? $"{AppPaths.ProductName} {AppVersion()} 启动"
                : $"{AppPaths.ProductName} {AppVersion()} 启动 · {role}");
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

            if (_background)
            {
                return;     // 没人看得见对话框（见 _background）
            }

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
        if (_background)
        {
            return;     // 没人看得见对话框（见 _background），日志已经写下了
        }

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
