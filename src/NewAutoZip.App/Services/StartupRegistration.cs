using System.IO;
using Microsoft.Win32;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.App.Services;

/// <summary>
/// 开机自启动：往 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 写一条记录。
///
/// <b>为什么是 HKCU 而不是 HKLM 或计划任务</b>
/// 这个程序备份的是当前用户的东西、配置和状态也都写在 <c>%LOCALAPPDATA%</c> 下
/// （见 <see cref="AppPaths.DataRoot"/>），所以"随这个用户登录而启动"才是对的语义。
/// HKLM 和计划任务都要管理员权限：装一次要提权、以后每次改开关也要提权，
/// 而 HKCU 下的这一条谁都能写，普通用户双击 exe 就能用。
/// 代价是它只在<b>用户登录后</b>才跑 —— 对一个要往 OneDrive 目录搬文件的程序来说
/// 这恰好是必须的，没登录的时候用户的 OneDrive 根本没在同步。
///
/// <b>自愈</b>
/// 注册表里存的是绝对路径。程序被挪走、或者换了个目录覆盖升级，那条记录就指向
/// 一个不存在的 exe，开机时静默失败，用户只会觉得"自启动坏了"。所以每次启动都
/// 调一次 <see cref="Sync"/>：想要自启动、而记录和当前 exe 不一致，就顺手改正。
///
/// 这里所有方法都不抛异常 —— 注册表被组策略锁掉、被安全软件拦掉都是现实情况，
/// 自启动登记失败不该把程序本身带走。失败时返回一句能直接给用户看的话。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名。用产品名，方便用户在任务管理器「启动」页里认出它。</summary>
    private const string ValueName = AppPaths.ProductName;

    /// <summary>
    /// 把自启动登记调成 <paramref name="wanted"/> 说的样子。
    ///
    /// 幂等：已经是想要的样子就一个字都不写。这一点有用 —— 保存配置是个高频动作，
    /// 没必要每次都去动注册表（写了就会有安全软件弹窗）。
    /// </summary>
    /// <param name="wanted">true = 登记开机自启动；false = 撤销登记。</param>
    /// <param name="log">写日志用。</param>
    /// <returns>出错时返回一句可以直接显示给用户的说明；一切正常返回 <c>null</c>。</returns>
    internal static string? Sync(bool wanted, IAppLogger log)
    {
        if (!wanted)
        {
            return Remove(log);
        }

        if (ExecutablePath() is not { } exe)
        {
            const string reason = "拿不到程序自身的路径，无法登记开机自启动。";
            log.Warn(reason);
            return reason;
        }

        string command = Quote(exe);

        try
        {
            using RegistryKey key =
                Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException($"打不开注册表项 HKCU\\{RunKeyPath}。");

            if (key.GetValue(ValueName) as string == command)
            {
                return null;        // 已经登记好了，别去动它
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            log.Info($"已登记开机自启动：{command}");
            return null;
        }
        catch (Exception ex)
        {
            log.Warn("登记开机自启动失败。", ex);
            return $"开机自启动登记失败（{ex.Message}）。可以手动把程序快捷方式放进「启动」文件夹。";
        }
    }

    private static string? Remove(IAppLogger log)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key?.GetValue(ValueName) is null)
            {
                return null;        // 本来就没登记
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            log.Info("已撤销开机自启动登记。");
            return null;
        }
        catch (Exception ex)
        {
            log.Warn("撤销开机自启动登记失败。", ex);
            return $"撤销开机自启动失败（{ex.Message}）。可以在任务管理器的「启动」页里手动禁用。";
        }
    }

    /// <summary>
    /// 当前登记的命令行，没登记则为 <c>null</c>。给「关于」和排查用。
    /// </summary>
    internal static string? Current()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 自己这个 exe 的完整路径。
    ///
    /// <see cref="Environment.ProcessPath"/> 在单文件发布下给的就是那个 exe，
    /// 正是要写进注册表的东西。<c>Assembly.Location</c> 不行 —— 单文件下它是空串。
    /// </summary>
    private static string? ExecutablePath()
    {
        string? path = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        // 极少数情况（宿主进程被改名等）退回到程序目录里同名的 exe。
        string beside = Path.Combine(AppPaths.BaseDirectory, AppPaths.ProductName + ".exe");

        return File.Exists(beside) ? beside : null;
    }

    /// <summary>
    /// 加引号。路径里有空格（<c>C:\Program Files\…</c>）时不加引号，
    /// Windows 会把它拆成 <c>C:\Program</c> 和一个参数，自启动直接失败。
    /// </summary>
    private static string Quote(string path) => $"\"{path}\"";
}
