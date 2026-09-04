using Microsoft.Extensions.Time.Testing;
using NewAutoZip.Core.Pipeline;
using Xunit;

namespace NewAutoZip.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void 阶梯是递增的且有封顶()
    {
        TimeSpan previous = TimeSpan.Zero;

        for (int i = 1; i <= RetryPolicy.MaxLadderStep; i++)
        {
            TimeSpan delay = RetryPolicy.DelayFor(i);
            Assert.True(delay > previous, $"第 {i} 级 {delay} 没有比第 {i - 1} 级 {previous} 更长");
            previous = delay;
        }

        // 超出阶梯后保持封顶值，不会无限增长也不会绕回 0。
        Assert.Equal(previous, RetryPolicy.DelayFor(RetryPolicy.MaxLadderStep + 1));
        Assert.Equal(previous, RetryPolicy.DelayFor(999));
    }

    [Fact]
    public void 首次失败也要等_不允许立刻重打()
    {
        // 这一条直接对应用户报告的症状：旧版失败后下一个扫描周期（30 秒）就再打一个包，
        // 而且不复位状态，于是无限循环。这里要求第一次重试就必须有明确的等待。
        Assert.True(RetryPolicy.DelayFor(1) >= TimeSpan.FromSeconds(30));
        Assert.True(RetryPolicy.DelayFor(0) > TimeSpan.Zero);
        Assert.True(RetryPolicy.DelayFor(-5) > TimeSpan.Zero);
    }

    [Fact]
    public void NextAttemptUtc_基于传入的当前时刻()
    {
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset next = RetryPolicy.NextAttemptUtc(1, now);

        Assert.Equal(now + RetryPolicy.DelayFor(1), next);
    }

    [Fact]
    public void 阶梯说明文字可读() =>
        Assert.False(string.IsNullOrWhiteSpace(RetryPolicy.DescribeLadder()));
}

public class RetryQueueTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static (RetryQueue Queue, FakeTimeProvider Time, RecordingLogger Log) Build()
    {
        FakeTimeProvider time = new(Origin);
        RecordingLogger log = new();
        return (new RetryQueue(log, time), time, log);
    }

    [Fact]
    public void 连续失败最终收敛到隔离区_并且停止重试()
    {
        // 这是整个重写里最重要的一条保证：
        // 一个批次不可能无限重试。旧版彻底没有这个概念 —— 失败即无限循环。
        const int maxAttempts = 6;
        (RetryQueue q, FakeTimeProvider time, _) = Build();
        string[] files = ["C:\\a.dat", "C:\\b.dat"];

        string? id = null;
        QuarantinedBatch? quarantined = null;

        for (int attempt = 1; attempt <= 20; attempt++)
        {
            FailureOutcome outcome = q.RecordFailure(id, files, 1024, $"第 {attempt} 次失败", maxAttempts);

            if (outcome.WasQuarantined)
            {
                quarantined = outcome.Quarantined;
                break;
            }

            Assert.NotNull(outcome.Requeued);
            id = outcome.Requeued!.Id;

            // 推进到下次尝试时刻，模拟"等到点了再试"。
            time.SetUtcNow(outcome.Requeued.NextAttemptUtc);
            RetryEntry? due = q.TakeDue();
            Assert.NotNull(due);
        }

        Assert.NotNull(quarantined);
        Assert.Equal(maxAttempts, quarantined!.Attempts);
        Assert.Equal(2, quarantined.Files.Count);

        // 进隔离区后队列必须是空的 —— 不再有任何自动重试。
        Assert.Equal(0, q.Count);
        Assert.Null(q.TakeDue());
    }

    [Fact]
    public void 未到点的批次不会被取出()
    {
        (RetryQueue q, FakeTimeProvider time, _) = Build();

        FailureOutcome outcome = q.RecordFailure(null, ["C:\\a.dat"], 10, "失败", 6);
        Assert.NotNull(outcome.Requeued);

        Assert.Null(q.TakeDue());                                  // 还没到点

        time.Advance(RetryPolicy.DelayFor(1) - TimeSpan.FromSeconds(1));
        Assert.Null(q.TakeDue());                                  // 差一秒也不行

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(q.TakeDue());                               // 到点了
    }

    [Fact]
    public void 重试次数累加而不是每次新建条目()
    {
        (RetryQueue q, FakeTimeProvider time, _) = Build();
        string[] files = ["C:\\a.dat"];

        FailureOutcome first = q.RecordFailure(null, files, 10, "第一次", 6);
        string id = first.Requeued!.Id;
        Assert.Equal(1, first.Requeued.Attempts);

        time.Advance(TimeSpan.FromHours(1));
        FailureOutcome second = q.RecordFailure(id, files, 10, "第二次", 6);

        Assert.Equal(1, q.Count);                       // 仍然只有一个条目
        Assert.Equal(2, second.Requeued!.Attempts);
        Assert.Equal(id, second.Requeued.Id);
        Assert.Contains("第二次", second.Requeued.LastError);

        // 退避间隔随次数变长。
        Assert.True(
            second.Requeued.NextAttemptUtc - time.GetUtcNow() > RetryPolicy.DelayFor(1),
            "第二次失败的等待时间没有比第一次更长");
    }

    [Fact]
    public void 已消失的源文件被剔除_批次不会永远无法满足()
    {
        // 旧版：窗口期内文件被删 → 重试永远不可能成功 → 无限循环 + 无限产包。
        using TempDir dir = new();
        string alive = dir.WriteFile("alive.dat");
        string dead = dir.File("dead.dat");

        (RetryQueue q, _, _) = Build();
        q.RecordFailure(null, [alive, dead], 100, "失败", 6);

        // 返回值是"被整批丢弃的批次数"；这里批次还剩一个文件，所以是 0。
        int droppedBatches = q.PruneMissingFiles(File.Exists);

        Assert.Equal(0, droppedBatches);
        Assert.Equal(1, q.Count);
        Assert.Single(q.Entries[0].Files);
        Assert.Equal(alive, q.Entries[0].Files[0]);
    }

    [Fact]
    public void 所有源文件都消失时整个批次被丢弃()
    {
        (RetryQueue q, _, _) = Build();
        q.RecordFailure(null, ["C:\\gone1.dat", "C:\\gone2.dat"], 100, "失败", 6);

        int droppedBatches = q.PruneMissingFiles(_ => false);

        Assert.Equal(1, droppedBatches);
        Assert.Equal(0, q.Count);
        Assert.Null(q.TakeDue());
    }

    [Fact]
    public void AllFiles_汇总所有待重试文件_供窗口排除()
    {
        (RetryQueue q, FakeTimeProvider time, _) = Build();

        q.RecordFailure(null, ["C:\\a.dat"], 10, "x", 6);
        time.Advance(TimeSpan.FromSeconds(1));
        q.RecordFailure(null, ["C:\\b.dat", "C:\\c.dat"], 10, "x", 6);

        HashSet<string> all = q.AllFiles();

        Assert.Equal(3, all.Count);
        Assert.Contains("C:\\A.DAT", all);   // 必须忽略大小写，否则同一文件会被重复收集
    }

    [Fact]
    public void 每个批次拿到唯一Id()
    {
        (RetryQueue q, FakeTimeProvider time, _) = Build();
        HashSet<string> ids = [];

        for (int i = 0; i < 50; i++)
        {
            FailureOutcome o = q.RecordFailure(null, [$"C:\\f{i}.dat"], 10, "x", 99);
            Assert.True(ids.Add(o.Requeued!.Id), $"Id 重复：{o.Requeued.Id}");
        }
    }

    [Fact]
    public void NextAttemptUtc_返回最早到点的那个()
    {
        (RetryQueue q, FakeTimeProvider time, _) = Build();

        q.RecordFailure(null, ["C:\\a.dat"], 10, "x", 6);        // 第 1 级退避
        DateTimeOffset firstDue = q.Entries[0].NextAttemptUtc;

        time.Advance(TimeSpan.FromSeconds(5));
        string id = q.RecordFailure(null, ["C:\\b.dat"], 10, "x", 6).Requeued!.Id;
        q.RecordFailure(id, ["C:\\b.dat"], 10, "x", 6);          // 第 2 级，更晚

        Assert.Equal(firstDue, q.NextAttemptUtc);
    }
}
