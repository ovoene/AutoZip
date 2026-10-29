using System.Diagnostics;

namespace NewAutoZip.Core.Startup;

/// <summary>
/// 和 Windows 任务计划程序打交道的那一半：查、注册、删除、立即运行。
///
/// 走 COM（<c>Schedule.Service</c>）而不是 <c>schtasks.exe</c>：
/// <list type="bullet">
/// <item>注册要带 Windows 密码。<c>schtasks /RP 密码</c> 会把密码摆在进程命令行上，
///       同机任何能列进程命令行的东西、以及开了命令行审计的事件日志都看得见；
///       COM 调用里密码只在本进程内存里走一趟。</item>
/// <item>查状态要的是结构化字段（路径、账户、上次结果码），
///       <c>schtasks /query</c> 的输出随系统语言变，解析中文/英文两套是自找麻烦。</item>
/// </list>
///
/// 这里的方法都不抛异常：失败时返回一句能直接给用户看的话。
/// 注册与删除需要管理员权限（开机触发器只有管理员能建），由调用方负责提权。
/// </summary>
public static class BootTaskScheduler
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonPassword = 1;

    /// <summary>TASK_STATE_DISABLED / TASK_STATE_RUNNING。</summary>
    private const int StateDisabled = 1;
    private const int StateRunning = 4;

    /// <summary>TASK_ACTION_EXEC。</summary>
    private const int ActionExec = 0;

    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);
    private const int ErrorAccessDenied = unchecked((int)0x80070005);

    /// <summary>读开机自启任务的状态。</summary>
    public static BootTaskStatus Query() => Query(BootTaskDefinition.TaskName);

    /// <summary>
    /// 按名字读根目录下的一条任务。留成 internal 是给测试用的：
    /// 测试机上没有 AutoZip 的任务，但有别的任务可以拿来验证"字段读得对不对"。
    /// </summary>
    internal static BootTaskStatus Query(string taskName)
    {
        try
        {
            dynamic folder = RootFolder();
            dynamic task;

            try
            {
                task = folder.GetTask(taskName);
            }
            catch (Exception ex) when (ex.HResult is ErrorFileNotFound or ErrorPathNotFound)
            {
                return BootTaskStatus.NotRegistered;
            }

            int state = (int)task.State;
            dynamic definition = task.Definition;

            string? command = null;
            string? arguments = null;
            dynamic actions = definition.Actions;
            int count = (int)actions.Count;

            // COM 集合从 1 开始数。只认第一个"启动程序"动作 —— 本程序注册的任务只有这一个。
            for (int i = 1; i <= count; i++)
            {
                dynamic action = actions[i];

                if ((int)action.Type == ActionExec)
                {
                    command = (string?)action.Path;
                    arguments = (string?)action.Arguments;
                    break;
                }
            }

            dynamic principal = definition.Principal;

            return new BootTaskStatus(
                QueryError: null,
                Exists: true,
                Enabled: state != StateDisabled && (bool)task.Enabled,
                Running: state == StateRunning,
                Command: command,
                Arguments: arguments,
                Account: (string?)principal.UserId,
                LogonType: (int)principal.LogonType,
                LastRunLocal: MeaningfulTime((DateTime)task.LastRunTime),
                LastResult: (int)task.LastTaskResult);
        }
        catch (Exception ex) when (ex.HResult == ErrorAccessDenied)
        {
            return BootTaskStatus.Failed(
                "权限不足。任务可能是用别的账户注册的；用当前账户点一次「重新注册」即可。");
        }
        catch (Exception ex)
        {
            return BootTaskStatus.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 注册（已有则覆盖）。<b>需要管理员权限。</b>
    /// </summary>
    /// <param name="exePath">开机时要运行的程序。</param>
    /// <param name="account">运行账户，<c>机器名\用户名</c>。</param>
    /// <param name="password">该账户的 Windows 登录密码。只交给任务计划程序，本方法不留副本。</param>
    /// <param name="userSid">该账户的 SID，写进任务的安全描述符（见 <see cref="BootTaskDefinition.SecurityDescriptorFor"/>）。</param>
    /// <returns>成功返回 null，否则返回给用户看的原因。</returns>
    public static string? Register(string exePath, string account, string password, string userSid)
    {
        try
        {
            string xml = BootTaskDefinition.BuildXml(exePath, account);
            string sddl = BootTaskDefinition.SecurityDescriptorFor(userSid);

            dynamic folder = RootFolder();
            folder.RegisterTask(
                BootTaskDefinition.TaskName,
                xml,
                TaskCreateOrUpdate,
                account,
                password,
                TaskLogonPassword,
                sddl);

            return null;
        }
        catch (Exception ex)
        {
            // 按 HRESULT 认，不按异常类型认：CLR 会把一部分 HRESULT 换成自己的异常类
            // （0x80070005 → UnauthorizedAccessException、0x80070002 → FileNotFoundException），
            // 只接 COMException 就会漏掉这几种。
            return DescribeRegisterError(ex.HResult, ex.Message);
        }
    }

    /// <summary>删除开机自启任务。不存在也算成功。<b>需要管理员权限。</b></summary>
    public static string? Unregister()
    {
        try
        {
            dynamic folder = RootFolder();
            folder.DeleteTask(BootTaskDefinition.TaskName, 0);
            return null;
        }
        catch (Exception ex) when (ex.HResult is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex.HResult == ErrorAccessDenied)
        {
            return "权限不足：删除开机自启任务需要管理员权限。";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 立刻让任务跑一次 —— 界面退出时用它把后台实例马上拉起来，不必等看门狗的下一个周期。
    ///
    /// 用 <c>schtasks /run</c>：它不需要密码、输出不用解析（只看退出码），
    /// 而任务的安全描述符给了当前用户运行权限，所以不提权也能跑。
    /// 失败无所谓：看门狗最迟 <see cref="BootTaskDefinition.WatchdogInterval"/> 之后会补上。
    /// </summary>
    public static bool TryRunNow(TimeSpan timeout)
    {
        try
        {
            ProcessStartInfo psi = new("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("/run");
            psi.ArgumentList.Add("/tn");
            psi.ArgumentList.Add(BootTaskDefinition.TaskName);

            using Process? process = Process.Start(psi);

            if (process is null)
            {
                return false;
            }

            // 两条流都得读掉，否则子进程写满管道就卡住（旧版 7za 的死锁就是这么来的）。
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch (Exception) { /* 已经退出 */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 只做架构校验：把 XML 交给任务计划程序解析一遍，什么都不注册。
    /// 不需要管理员权限，测试用它确认 <see cref="BootTaskDefinition.BuildXml"/> 生成的东西系统认。
    /// </summary>
    public static string? ValidateXml(string xml)
    {
        try
        {
            dynamic service = Connect();
            dynamic definition = service.NewTask(0);
            definition.XmlText = xml;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>注册失败的几种常见原因，说成人话。</summary>
    public static string DescribeRegisterError(int hresult, string fallback) => hresult switch
    {
        BootTaskStatus.ResultLogonFailure =>
            "Windows 拒绝了这个密码：用户名或密码不对。用微软账户登录的电脑，这里要填微软账户的密码（不是 PIN）。",
        BootTaskStatus.ResultAccountRestriction =>
            "这个账户受限，无法在无人登录时运行。最常见的原因是账户没有设置密码 —— " +
            "Windows 不允许空密码账户这样登录，请先给账户设一个密码。",
        BootTaskStatus.ResultLogonTypeNotGranted =>
            "这个账户没有「作为批处理作业登录」的权限（通常是组策略限制），请联系管理员。",
        ErrorAccessDenied =>
            "权限不足：注册开机启动的任务需要管理员权限。",
        _ => fallback,
    };

    private static dynamic Connect()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic RootFolder() => Connect().GetFolder("\\");

    /// <summary>
    /// 从没跑过的任务，LastRunTime 是 OLE 零点（1899-12-30）或 1999-11-30 之类的占位值。
    /// 那不是一个真实时间，显示出来只会让人困惑。
    /// </summary>
    private static DateTime? MeaningfulTime(DateTime value) => value.Year < 2000 ? null : value;
}
