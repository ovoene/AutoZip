namespace NewAutoZip.Core.Pipeline;

/// <summary>
/// 失败重试的退避阶梯。
///
/// 旧版没有任何退避：失败后下一个扫描周期（默认 30 秒）立刻重打一个完整压缩包，
/// 而且失败分支不复位状态，所以这不是"重试"而是"无限产出"。
/// 用户看到的现象就是 ZipTemp 每 30 秒多一个包，直到磁盘满。
///
/// 阶梯设计成先密后疏：短暂的瞬时故障（文件刚好被占用、网络抖动）在前两级就能恢复；
/// 真正的持续性故障则很快退到半小时一次，不再消耗磁盘和 CPU。
/// </summary>
public static class RetryPolicy
{
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
    ];

    public static int MaxLadderStep => Ladder.Length;

    /// <summary><paramref name="consecutiveFailures"/> 从 1 开始（刚失败第一次）。</summary>
    public static TimeSpan DelayFor(int consecutiveFailures)
    {
        int index = Math.Clamp(consecutiveFailures, 1, Ladder.Length) - 1;
        return Ladder[index];
    }

    public static DateTimeOffset NextAttemptUtc(int consecutiveFailures, DateTimeOffset nowUtc) =>
        nowUtc + DelayFor(consecutiveFailures);

    public static string DescribeLadder() =>
        string.Join(" → ", Ladder.Select(t =>
            t.TotalMinutes < 1 ? $"{t.TotalSeconds:0}秒" : $"{t.TotalMinutes:0}分"));
}
