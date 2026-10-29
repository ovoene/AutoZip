using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Startup;

namespace NewAutoZip.App.Services;

/// <summary>注册 / 取消开机自启的结局。</summary>
internal enum BootTaskOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

/// <summary>结局 + 一句给用户看的话。</summary>
internal sealed record BootTaskResult(BootTaskOutcome Outcome, string Message)
{
    public bool Ok => Outcome == BootTaskOutcome.Succeeded;
}

/// <summary>
/// 设置页上「注册开机自启」「取消开机自启」两颗按钮背后的事：提权、问密码、调任务计划程序。
///
/// <b>为什么要提权</b>：开机触发器只有管理员能建（普通用户能建的触发器都得等他登录）。
/// 程序本身仍然以普通权限运行（app.manifest 是 asInvoker），只在这一步临时提权：
/// 已经是管理员就当场弹密码框；不是的话，以「以管理员身份运行」拉起本程序的另一个进程
/// （<c>--boot-task register</c>），在那个进程里问密码、注册，完了把结果用退出码带回来。
///
/// 密码不经过进程命令行，也不经过任何文件 —— 它只在弹出密码框的那个进程里走一趟，
/// 然后交给任务计划程序。
///
/// <b>账户身份要显式传过去</b>：标准用户提权时，UAC 弹出的是"请输入管理员的用户名和密码"，
/// 提权后的进程是<b>那个管理员</b>，不是当前用户。任务必须以当前用户的身份跑
/// （DPAPI、OneDrive 目录、数据目录都是他的），所以账户与 SID 由这边定好再交过去，
/// 提权进程不自己去取 <c>WindowsIdentity.GetCurrent()</c>。
/// </summary>
internal static class BootTaskRegistrar
{
    /// <summary>提权子进程的命令行开关。App.OnStartup 认到它就走 <see cref="RunElevatedCommand"/>。</summary>
    internal const string Argument = "--boot-task";

    private const string RegisterVerb = "register";
    private const string UnregisterVerb = "unregister";

    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitCancelled = 2;
    private const int ExitBadArguments = 3;

    /// <summary>ERROR_CANCELLED：用户在 UAC 弹窗上点了「否」。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>当前进程是否已经提权（内置 Administrator 账户、或者用户右键"以管理员身份运行"）。</summary>
    internal static bool IsElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>当前用户：<c>机器名\用户名</c> 与 SID。</summary>
    internal static (string Account, string Sid) CurrentAccount()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return (identity.Name, identity.User?.Value ?? string.Empty);
    }

    /// <summary>本程序的完整路径 —— 开机时要运行的就是它。</summary>
    internal static string? ExecutablePath()
    {
        string? path = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    // ==================================================================
    //  主界面调用
    // ==================================================================

    /// <summary>注册（或重新注册）开机自启。</summary>
    /// <param name="owner">已提权时密码框的所有者窗口。</param>
    /// <param name="isDark">当前是否深色主题 —— 提权进程里的密码框要跟主界面一个样子。</param>
    /// <param name="accent">当前强调色（可空）。</param>
    internal static async Task<BootTaskResult> RegisterAsync(
        Window? owner,
        bool isDark,
        string accent,
        IAppLogger log)
    {
        if (ExecutablePath() is not { } exe)
        {
            return new BootTaskResult(BootTaskOutcome.Failed, "拿不到程序自身的路径，无法注册开机自启。");
        }

        (string account, string sid) = CurrentAccount();

        if (sid.Length == 0)
        {
            return new BootTaskResult(BootTaskOutcome.Failed, "拿不到当前账户的 SID，无法注册开机自启。");
        }

        if (IsElevated)
        {
            return ShowRegisterDialog(owner, exe, account, sid, log)
                ? Succeeded(exe)
                : Cancelled();
        }

        int code = await RunElevatedAsync(
            [RegisterVerb, "--account", account, "--sid", sid, "--theme", isDark ? "dark" : "light", "--accent", accent],
            log).ConfigureAwait(true);

        return code switch
        {
            ExitOk => Succeeded(exe),
            ExitCancelled => Cancelled(),
            _ => new BootTaskResult(
                BootTaskOutcome.Failed,
                $"注册没有完成（提权进程返回 {code}）。详情见日志。"),
        };
    }

    /// <summary>取消开机自启。</summary>
    internal static async Task<BootTaskResult> UnregisterAsync(IAppLogger log)
    {
        if (IsElevated)
        {
            return Unregister(log);
        }

        int code = await RunElevatedAsync([UnregisterVerb], log).ConfigureAwait(true);

        return code switch
        {
            ExitOk => new BootTaskResult(BootTaskOutcome.Succeeded, "已取消开机自启。"),
            ExitCancelled => Cancelled(),
            _ => new BootTaskResult(
                BootTaskOutcome.Failed,
                $"取消开机自启没有完成（提权进程返回 {code}）。详情见日志。"),
        };
    }

    // ==================================================================
    //  提权子进程（--boot-task …）
    // ==================================================================

    /// <summary>
    /// 提权子进程的入口。返回进程退出码。
    ///
    /// 不抢单实例锁、不启动引擎、不读写 settings.json —— 提权进程可能是<b>另一个账户</b>，
    /// 它要是去读配置，SettingsStore 会把明文秘密"顺手"用它自己的 DPAPI 重新加密，
    /// 当前用户就再也解不开了。
    /// </summary>
    internal static int RunElevatedCommand(string[] args, IAppLogger log)
    {
        string? verb = Value(args, Argument);

        if (string.Equals(verb, UnregisterVerb, StringComparison.OrdinalIgnoreCase))
        {
            BootTaskResult result = Unregister(log);
            return result.Ok ? ExitOk : ExitFailed;
        }

        if (!string.Equals(verb, RegisterVerb, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"看不懂的开机自启子命令：{verb ?? "（空）"}");
            return ExitBadArguments;
        }

        string? account = Value(args, "--account");
        string? sid = Value(args, "--sid");

        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(sid) || !IsSid(sid))
        {
            log.Warn("注册开机自启的提权进程缺少账户或 SID 参数。");
            return ExitBadArguments;
        }

        if (ExecutablePath() is not { } exe)
        {
            log.Warn("提权进程拿不到程序自身的路径。");
            return ExitFailed;
        }

        // 密码框跟主界面同一个主题。主题从参数来，不读 settings.json（理由见方法注释）。
        try
        {
            ThemeMode mode = string.Equals(Value(args, "--theme"), "light", StringComparison.OrdinalIgnoreCase)
                ? ThemeMode.Light
                : ThemeMode.Dark;

            new ThemeService(log).Apply(mode, Value(args, "--accent"));
        }
        catch (Exception ex)
        {
            log.Debug($"提权进程应用主题失败，按默认样子显示：{ex.Message}");
        }

        return ShowRegisterDialog(null, exe, account, sid, log) ? ExitOk : ExitCancelled;
    }

    // ==================================================================
    //  实现
    // ==================================================================

    private static bool ShowRegisterDialog(Window? owner, string exe, string account, string sid, IAppLogger log)
    {
        Views.BootTaskWindow dialog = new(account, exe, password =>
        {
            string? error = BootTaskScheduler.Register(exe, account, password, sid);

            if (error is not null)
            {
                log.Warn($"注册开机自启失败：{error}");
                return error;
            }

            // 回读一遍：注册调用返回成功，不等于任务真的指向了这个程序。
            BootTaskStatus status = BootTaskScheduler.Query();

            if (!status.Registered || !BootTaskDefinition.SameExecutable(status.Command, exe))
            {
                string why = status.QueryError ?? "回读到的任务和刚登记的不一致";
                log.Warn($"注册开机自启后回读校验失败：{why}");
                return $"任务计划程序说注册成功了，但回读校验没过：{why}。";
            }

            log.Info($"已注册开机自启：{exe} {BootTaskDefinition.BackgroundArgument}，运行账户 {account}。");
            return null;
        });

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true;
    }

    private static BootTaskResult Unregister(IAppLogger log)
    {
        string? error = BootTaskScheduler.Unregister();

        if (error is not null)
        {
            log.Warn($"取消开机自启失败：{error}");
            return new BootTaskResult(BootTaskOutcome.Failed, $"取消开机自启失败：{error}");
        }

        log.Info("已取消开机自启（删除了计划任务）。");
        return new BootTaskResult(BootTaskOutcome.Succeeded, "已取消开机自启。");
    }

    /// <summary>以管理员身份拉起本程序的另一个进程，等它结束，返回退出码。</summary>
    private static async Task<int> RunElevatedAsync(string[] verbAndArgs, IAppLogger log)
    {
        if (ExecutablePath() is not { } exe)
        {
            return ExitFailed;
        }

        ProcessStartInfo psi = new(exe)
        {
            // runas 只在 ShellExecute 下生效。
            UseShellExecute = true,
            Verb = "runas",
            Arguments = JoinArguments([Argument, .. verbAndArgs]),
            WorkingDirectory = AppPaths.BaseDirectory,
        };

        try
        {
            using Process? process = Process.Start(psi);

            if (process is null)
            {
                return ExitFailed;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.Info("用户在管理员确认窗口上点了「否」，开机自启未改动。");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            log.Warn("拉起提权进程失败。", ex);
            return ExitFailed;
        }
    }

    private static BootTaskResult Succeeded(string exe) =>
        new(BootTaskOutcome.Succeeded, $"已注册开机自启：开机后无需登录，就会在后台运行 {exe}。");

    private static BootTaskResult Cancelled() =>
        new(BootTaskOutcome.Cancelled, "已取消，开机自启没有改动。");

    /// <summary>
    /// 拼命令行。每个值都加引号；值里本来就不该有引号（账户名、SID、颜色都不可能带），
    /// 真有就整个丢掉 —— 拼坏的命令行比少一个参数危险得多。
    /// </summary>
    private static string JoinArguments(IEnumerable<string> values) =>
        string.Join(' ', values.Select(v => v.Contains('"') ? "\"\"" : $"\"{v}\""));

    /// <summary>取 <c>--name value</c> 里的 value。</summary>
    private static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool IsSid(string value)
    {
        try
        {
            _ = new SecurityIdentifier(value);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
