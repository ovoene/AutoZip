using System.Xml.Linq;
using NewAutoZip.Core.Startup;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 开机自启（无需登录）那条计划任务。
///
/// 真正的注册要管理员权限和 Windows 密码，还会改测试机的系统配置，这里一概不做。
/// 测的是三件不需要这些的事：任务定义长得对不对（含交给任务计划程序做一次只解析、
/// 不注册的架构校验）、读回来的状态说成人话说得对不对、读状态的那段 COM 代码字段取得对不对。
/// </summary>
public sealed class BootTaskTests
{
    private const string Exe = @"C:\Tools\AutoZip\AutoZip.exe";
    private const string Account = @"PC\alice";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // ==================================================================
    //  任务定义
    // ==================================================================

    [Fact]
    public void Definition_starts_at_boot_without_a_logon_and_never_times_out()
    {
        XElement task = XElement.Parse(BootTaskDefinition.BuildXml(Exe, Account));

        // 开机触发，不是登录触发 —— 这正是"无需登录"。
        Assert.NotNull(task.Descendants(Ns + "BootTrigger").SingleOrDefault());
        Assert.Empty(task.Descendants(Ns + "LogonTrigger"));

        // 保存密码：只有这种登录方式拿得到 DPAPI 主密钥，配置里加密的压缩密码才解得开。
        Assert.Equal("Password", task.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal(Account, task.Descendants(Ns + "UserId").Single().Value);
        Assert.Equal("LeastPrivilege", task.Descendants(Ns + "RunLevel").Single().Value);

        // 默认 72 小时会把常驻的后台实例杀掉。
        Assert.Equal("PT0S", task.Descendants(Ns + "ExecutionTimeLimit").Single().Value);

        // 看门狗触发时已有实例在跑是常态，必须跳过而不是叠一个。
        Assert.Equal("IgnoreNew", task.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT5M", task.Descendants(Ns + "Interval").First().Value);

        // 笔记本拔了电源也要照常备份。
        Assert.Equal("false", task.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", task.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
    }

    [Fact]
    public void Definition_runs_this_exe_in_background_mode_with_a_quoted_path()
    {
        const string spaced = @"D:\My Tools\Auto Zip\AutoZip.exe";

        XElement task = XElement.Parse(BootTaskDefinition.BuildXml(spaced, Account));
        XElement exec = task.Descendants(Ns + "Exec").Single();

        // 带空格的路径不加引号，就是经典的"未加引号路径"：系统会先去试 D:\My.exe。
        Assert.Equal($"\"{spaced}\"", exec.Element(Ns + "Command")!.Value);
        Assert.Equal(BootTaskDefinition.BackgroundArgument, exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal(@"D:\My Tools\Auto Zip", exec.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void Definition_escapes_paths_that_contain_xml_characters()
    {
        const string odd = @"D:\R&D <backup>\AutoZip.exe";

        // 拼字符串的话，路径里一个 & 就让整份 XML 作废，任务计划程序直接拒收。
        XElement task = XElement.Parse(BootTaskDefinition.BuildXml(odd, Account));

        Assert.Equal($"\"{odd}\"", task.Descendants(Ns + "Command").Single().Value);
    }

    [Fact]
    public void Task_scheduler_accepts_the_definition()
    {
        // 只解析、不注册：不需要管理员权限，也不会在测试机上留下任何东西。
        string? error = BootTaskScheduler.ValidateXml(
            BootTaskDefinition.BuildXml(@"D:\R&D tools\AutoZip.exe", Account));

        Assert.Null(error);
    }

    [Fact]
    public void Security_descriptor_lets_only_the_registered_user_read_and_run()
    {
        const string sid = "S-1-5-21-1000-2000-3000-1001";

        string sddl = BootTaskDefinition.SecurityDescriptorFor(sid);

        Assert.Equal($"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{sid})", sddl);

        // 不给写：能改任务定义就能把它改成以最高权限运行别的程序。
        Assert.DoesNotContain($"FA;;;{sid}", sddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21)(A;;FA;;;WD")]
    public void Security_descriptor_refuses_anything_that_is_not_a_sid(string bogus)
    {
        // 参数原样拼进 SDDL 的话，第二个值会给 Everyone 加一条完全控制。
        Assert.ThrowsAny<ArgumentException>(() => BootTaskDefinition.SecurityDescriptorFor(bogus));
    }

    [Theory]
    [InlineData("\"C:\\A\\AutoZip.exe\"", "C:\\A\\AutoZip.exe")]
    [InlineData("  C:\\A\\AutoZip.exe  ", "C:\\A\\AutoZip.exe")]
    [InlineData("\"\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Command_path_drops_quotes_and_blanks(string? raw, string? expected) =>
        Assert.Equal(expected, BootTaskDefinition.CommandPathOf(raw));

    [Fact]
    public void Same_executable_ignores_case_quotes_and_dot_segments()
    {
        Assert.True(BootTaskDefinition.SameExecutable("\"C:\\Tools\\AutoZip.exe\"", @"c:\tools\.\AUTOZIP.EXE"));
        Assert.False(BootTaskDefinition.SameExecutable(@"C:\Old\AutoZip.exe", @"C:\New\AutoZip.exe"));
        Assert.False(BootTaskDefinition.SameExecutable(null, @"C:\New\AutoZip.exe"));
    }

    [Theory]
    [InlineData("--background", true)]
    [InlineData("--BACKGROUND", true)]
    [InlineData("--x \"--background\"", true)]
    [InlineData("--backgroundx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Background_argument_is_recognised_exactly(string? args, bool expected) =>
        Assert.Equal(expected, BootTaskDefinition.HasBackgroundArgument(args));

    // ==================================================================
    //  状态说成人话
    // ==================================================================

    private static BootTaskStatus Healthy(
        string command = Exe,
        string arguments = BootTaskDefinition.BackgroundArgument,
        bool enabled = true,
        int logon = BootTaskStatus.PasswordLogon,
        int lastResult = 0,
        DateTime? lastRun = null) =>
        new(null, true, enabled, false, $"\"{command}\"", arguments, Account, logon,
            lastRun ?? new DateTime(2026, 10, 29, 8, 0, 30), lastResult);

    private static bool AllExist(string _) => true;

    [Fact]
    public void Not_registered_is_not_an_alarm()
    {
        BootTaskAdvice advice = BootTaskStatus.NotRegistered.Describe(Exe, AllExist);

        Assert.False(BootTaskStatus.NotRegistered.Registered);
        Assert.False(advice.NeedsAttention);
        Assert.Contains("未注册", advice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_healthy_task_says_who_it_runs_as_and_when_it_last_started()
    {
        BootTaskAdvice advice = Healthy().Describe(Exe, AllExist);

        Assert.False(advice.NeedsAttention);
        Assert.Contains(Account, advice.Text, StringComparison.Ordinal);
        Assert.Contains("2026-10-29 08:00:30", advice.Text, StringComparison.Ordinal);
        Assert.Contains("正常", advice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_that_never_ran_says_it_takes_effect_on_next_boot()
    {
        BootTaskStatus fresh = Healthy() with { LastRunLocal = null, LastResult = BootTaskStatus.ResultHasNotRun };

        BootTaskAdvice advice = fresh.Describe(Exe, AllExist);

        Assert.False(advice.NeedsAttention);
        Assert.Contains("下次开机起生效", advice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Moved_program_is_flagged_and_points_at_reregistration()
    {
        // 用户把整个程序目录挪到了别处 —— 任务还指着老位置，而老位置上的 exe 还在。
        BootTaskAdvice advice = Healthy(command: @"C:\Old\AutoZip.exe").Describe(Exe, AllExist);

        Assert.True(advice.NeedsAttention);
        Assert.Contains(@"C:\Old\AutoZip.exe", advice.Text, StringComparison.Ordinal);
        Assert.Contains(Exe, advice.Text, StringComparison.Ordinal);
        Assert.Contains("重新注册", advice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_program_is_flagged()
    {
        BootTaskAdvice advice = Healthy(command: @"C:\Gone\AutoZip.exe").Describe(Exe, _ => false);

        Assert.True(advice.NeedsAttention);
        Assert.Contains("不存在", advice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_windows_password_is_explained_in_plain_words()
    {
        BootTaskAdvice advice = Healthy(lastResult: BootTaskStatus.ResultLogonFailure).Describe(Exe, AllExist);

        Assert.True(advice.NeedsAttention);
        Assert.Contains("密码", advice.Text, StringComparison.Ordinal);
        Assert.Contains("重新注册", advice.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, BootTaskStatus.PasswordLogon, "--background")]   // 被停用
    [InlineData(true, 2, "--background")]                              // S4U：解不开 DPAPI
    [InlineData(true, BootTaskStatus.PasswordLogon, "")]               // 参数被改：开机会弹界面
    public void Tampered_tasks_need_attention(bool enabled, int logon, string arguments)
    {
        BootTaskAdvice advice = Healthy(arguments: arguments, enabled: enabled, logon: logon).Describe(Exe, AllExist);

        Assert.True(advice.NeedsAttention);
    }

    [Fact]
    public void Query_failure_is_reported_not_mistaken_for_not_registered()
    {
        BootTaskStatus failed = BootTaskStatus.Failed("拒绝访问");

        Assert.False(failed.Registered);
        Assert.True(failed.Describe(Exe, AllExist).NeedsAttention);
    }

    [Theory]
    [InlineData(0, "正常")]
    [InlineData(BootTaskStatus.ResultHasNotRun, "还没运行过")]
    [InlineData(BootTaskStatus.ResultLogonFailure, "密码")]
    [InlineData(0x12345, "0x00012345")]
    public void Result_codes_read_as_words(int code, string fragment) =>
        Assert.Contains(fragment, BootTaskStatus.DescribeResult(code), StringComparison.Ordinal);

    // ==================================================================
    //  读状态（真实的任务计划程序，只读）
    // ==================================================================

    [Fact]
    public void Querying_a_task_that_does_not_exist_reads_as_not_registered()
    {
        BootTaskStatus status = BootTaskScheduler.Query($"AutoZip-Test-{Guid.NewGuid():N}");

        // 必须是"没注册"，不能是"读失败" —— 后者会在设置页上亮一条警告。
        Assert.Null(status.QueryError);
        Assert.False(status.Exists);
    }

    [Fact]
    public void Querying_an_existing_task_reads_its_program_path()
    {
        // 测试机上没有 AutoZip 的任务，借根目录下随便哪条带"启动程序"动作的任务验证字段读取。
        // 一条都没有（精简过的系统）就没得验，直接结束。
        if (FirstRootExecTask() is not { } sample)
        {
            return;
        }

        BootTaskStatus status = BootTaskScheduler.Query(sample.Name);

        Assert.Null(status.QueryError);
        Assert.True(status.Exists);
        Assert.Equal(
            BootTaskDefinition.CommandPathOf(sample.Command),
            BootTaskDefinition.CommandPathOf(status.Command));
    }

    /// <summary>用 COM 直接枚举，不走被测代码，免得自己验自己。</summary>
    private static (string Name, string Command)? FirstRootExecTask()
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            service.Connect();
            dynamic tasks = service.GetFolder("\\").GetTasks(1);

            for (int i = 1; i <= (int)tasks.Count; i++)
            {
                dynamic task = tasks[i];
                dynamic actions = task.Definition.Actions;

                for (int j = 1; j <= (int)actions.Count; j++)
                {
                    dynamic action = actions[j];

                    if ((int)action.Type == 0 && action.Path is string path && path.Length > 0)
                    {
                        return ((string)task.Name, path);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 没有权限读某条任务之类 —— 没得验就不验。
        }

        return null;
    }
}
