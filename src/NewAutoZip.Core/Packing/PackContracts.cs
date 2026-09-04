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
    bool VerifyAfterPack = true);

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
