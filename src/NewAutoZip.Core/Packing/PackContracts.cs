namespace NewAutoZip.Core.Packing;

/// <summary>
/// 7-Zip 退出码分级。
///
/// 这是旧版"每 30 秒产出一个完整压缩包直到磁盘满"最常见的触发点：
/// <c>Zip7Service.Pack</c> 直接 <c>return p.ExitCode == 0</c>，
/// 而退出码 1 的含义是"<b>成功但有警告</b>"——例如某个源文件被别的进程锁着没能加进去，
/// 压缩包本身是完好可用的。旧版把它判成彻底失败，进入失败分支，
/// 而失败分支又不复位状态，于是下一轮再打一个包，无穷循环。
/// </summary>
public static class SevenZipExitCode
{
    public const int Ok = 0;

    /// <summary>非致命警告：部分文件被跳过，但归档有效。</summary>
    public const int Warning = 1;

    public const int FatalError = 2;
    public const int CommandLineError = 7;
    public const int NotEnoughMemory = 8;
    public const int UserStopped = 255;

    public static bool IsArchiveUsable(int exitCode) => exitCode is Ok or Warning;

    public static string Describe(int exitCode) => exitCode switch
    {
        Ok => "成功",
        Warning => "成功，但有警告（部分文件被跳过，归档仍然可用）",
        FatalError => "致命错误",
        CommandLineError => "命令行参数错误",
        NotEnoughMemory => "内存不足",
        UserStopped => "被用户或调用方中止",
        _ => $"未知退出码 {exitCode}",
    };
}

public enum PackOutcome
{
    Success,
    SuccessWithWarnings,
    Failed,
    TimedOut,
    Cancelled,
    ExecutableMissing,
    VerificationFailed,
}

// OutputPath 是最终归档路径；Runner 内部先写 OutputPath + ".part"，
// 只有在"退出码可接受 + 文件存在 + 完整性校验通过"之后才改名成 OutputPath。
public sealed record PackRequest(
    string OutputPath,
    IReadOnlyList<string> Files,
    string Password,
    int CompressionLevel,
    bool EncryptFileNames,
    TimeSpan Timeout,
    bool VerifyAfterPack = true)
{
    /// <summary>
    /// 随包一起进归档、但<b>不算作源文件</b>的附加文件（目前只有包内清单
    /// <see cref="ArchiveManifest.EntryName"/>）。
    ///
    /// 单列一份而不是并进 <see cref="Files"/>，是因为 <see cref="Files"/>.Count 被三处逻辑当作
    /// "这一批应该有多少个源文件"来用：空归档判据、"少压进去几个"的告警、以及
    /// <see cref="PackResult.FileCount"/>。混进去会让这三处各自多算一个，
    /// 其中空归档判据尤其危险 —— 源文件全没了、只剩清单进包时，它就不再报空。
    /// </summary>
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];
}

public readonly record struct PackProgress(int Percent, string? CurrentFile);

public sealed record PackResult(
    PackOutcome Outcome,
    int ExitCode,
    string? ArchivePath,
    long ArchiveBytes,
    int FileCount,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public bool Ok => Outcome is PackOutcome.Success or PackOutcome.SuccessWithWarnings;

    public static PackResult Fail(PackOutcome outcome, string error, int exitCode = -1, TimeSpan elapsed = default) =>
        new(outcome, exitCode, null, 0, 0, elapsed, [], error);
}

// ==================== 解压 ====================
//
// 解压侧刻意复用 PackOutcome 而不另立一套枚举：两边的结局是同一组
// （成功 / 有警告 / 失败 / 超时 / 取消 / 找不到 7za），再定义一份平行的
// ExtractOutcome 只会让调用方在两个长得一样的枚举之间来回翻译。
// 唯一解压独有的结局是"密码不对"，它单独用 WrongPassword 标志表达 —— 见下。

/// <param name="ArchivePath">要解开的 .7z。</param>
/// <param name="TargetDirectory">解到哪里。不存在会被创建。</param>
/// <param name="Password">密码。空串表示归档没加密。</param>
/// <param name="Overwrite">
/// true = <c>-aoa</c> 无条件覆盖；false = <c>-aos</c> 跳过已存在的。
/// 默认 false —— 恢复是"把丢掉的东西找回来"，默认就该保守，
/// 绝不能因为点错一个目标目录就把现有文件冲掉。
/// </param>
public sealed record ExtractRequest(
    string ArchivePath,
    string TargetDirectory,
    string Password,
    TimeSpan Timeout,
    bool Overwrite = false);

public sealed record ExtractResult(
    PackOutcome Outcome,
    int ExitCode,
    string TargetDirectory,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    /// <summary>
    /// 密码不对。<b>必须与普通失败区分开</b>：这是恢复演练存在的全部理由 ——
    /// "包坏了"和"你存的那个密码打不开这个包"要采取的行动完全不同，
    /// 前者要重新备份，后者要去找回旧密码，而且晚一天发现就少一天补救机会。
    /// </summary>
    public bool WrongPassword { get; init; }

    public bool Ok => Outcome is PackOutcome.Success or PackOutcome.SuccessWithWarnings;

    public static ExtractResult Fail(
        PackOutcome outcome,
        string error,
        string targetDirectory = "",
        int exitCode = -1,
        TimeSpan elapsed = default,
        bool wrongPassword = false) =>
        new(outcome, exitCode, targetDirectory, elapsed, [], error) { WrongPassword = wrongPassword };
}

/// <summary>归档里的一个条目，由 <c>7za l -slt</c> 解析而来。</summary>
public sealed record ArchiveEntry(string Path, long Size, DateTimeOffset? Modified, bool IsDirectory);

public sealed record ArchiveListing(
    bool Ok,
    IReadOnlyList<ArchiveEntry> Entries,
    string? Error)
{
    public bool WrongPassword { get; init; }

    /// <summary>
    /// 条目数超过上限、后面的没读进来。列出来是给人看的，不是账本 ——
    /// 真正的账本是包内清单。所以这里截断是可以接受的，但<b>必须说出来</b>：
    /// 界面上显示"共 20 个文件"而实际有 20 万个，比不显示更糟。
    /// </summary>
    public bool Truncated { get; init; }

    public long TotalBytes => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public static ArchiveListing Fail(string error, bool wrongPassword = false) =>
        new(false, [], error) { WrongPassword = wrongPassword };
}
