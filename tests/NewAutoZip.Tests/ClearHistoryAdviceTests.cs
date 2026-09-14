using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Storage;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// "清空历史记录"确认框的正文。
///
/// 这段文字是整个功能里后果最大的一句话 —— 用户按它决定要不要点那个按钮。
/// 说错两个方向都很糟：把"会重来一遍"说成"不会"，用户点完发现满目录重传；
/// 反过来则会吓住本来该清的人。所以这里逐句钉死。
///
/// 三个分支必须与 <see cref="BackupEngine"/> 里首次运行那段
/// （<c>StartupAsync</c>：先看生效起始日期、再看"处理已有文件"、都没有才记水位线）一致。
/// </summary>
public class ClearHistoryAdviceTests
{
    private static AppSettings Settings(
        DateOnly? effectiveFrom = null,
        bool processExisting = false) =>
        new()
        {
            EffectiveFrom = effectiveFrom,
            ProcessExistingFilesOnFirstRun = processExisting,
        };

    private static EngineState State(
        int pendingUploads = 0,
        int quarantined = 0,
        long archives = 0,
        long bytes = 0)
    {
        EngineState state = new()
        {
            TotalArchivesCreated = archives,
            TotalBytesArchived = bytes,
        };

        for (int i = 0; i < pendingUploads; i++)
        {
            state.PendingUploads.Add(new PendingUpload
            {
                ArchivePath = $"X:\\cloud\\Backup_{i}.7z",
                Bytes = 1024,
                FileCount = 1,
            });
        }

        for (int i = 0; i < quarantined; i++)
        {
            state.Quarantined.Add(new QuarantinedBatch { Id = $"批次{i}", Reason = "测试" });
        }

        return state;
    }

    // ==================================================================
    //  三种分支的判定顺序
    // ==================================================================

    [Fact]
    public void 没配日期也没勾已有文件_清空后只处理新文件()
    {
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(Settings(), State());

        Assert.Equal(RebackupScope.NewFilesOnly, advice.Scope);

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("清空后只会处理往后新增的文件，目录里已有的文件不会被重新备份。", text);
        Assert.DoesNotContain("重新备份一遍", text);
    }

    [Fact]
    public void 勾了首次运行处理已有文件_清空后会整批重来()
    {
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(processExisting: true), State());

        Assert.Equal(RebackupScope.AllExistingFiles, advice.Scope);

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("监控目录里现有的文件会被「全部重新备份一遍」", text);
        Assert.Contains("取消那个勾选", text);
    }

    [Fact]
    public void 配了生效起始日期_优先于勾选项()
    {
        // 用户要的就是这一条：用了限定的生效日期范围时必须提醒去改起始日期。
        // 判定顺序与引擎一致 —— 日期在，处理已有文件那个勾选就管不着了。
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(effectiveFrom: new DateOnly(2026, 1, 1), processExisting: true),
            State());

        Assert.Equal(RebackupScope.SinceEffectiveFrom, advice.Scope);
        Assert.Equal(new DateOnly(2026, 1, 1), advice.EffectiveFrom);
    }

    [Fact]
    public void 配了日期时要报出日期本身并给出改哪里的指引()
    {
        // 只说"你配了生效起始日期"是不够的：用户得知道是哪个日期、
        // 以及去哪里改。这两样缺一个，那条提醒就只是吓人，帮不上忙。
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(effectiveFrom: new DateOnly(2026, 3, 15)), State());

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("2026-03-15", text);
        Assert.Contains("重新备份一遍", text);
        Assert.Contains("把起始日期改成今天", text);
    }

    [Fact]
    public void 日期提醒里说的是往后顺延而不是删掉设置()
    {
        // 改日期 = 把水位线挪到"现在"，不是让用户放弃这个功能。
        // 提法写成"删掉日期"会把人往错的方向引。
        //
        // 只查提醒那两行，不查全文 —— 下文那句"压缩包不会被删除"里的"删除"
        // 是另一回事，全文断言会被它误伤。
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(effectiveFrom: new DateOnly(2026, 3, 15)), State());

        string[] reminderLines = advice.ToConfirmationText(deleteLogFiles: false)
            .Split(Environment.NewLine)
            .Where(l => l.Contains("生效起始日期") || l.Contains("改成今天"))
            .ToArray();

        Assert.Equal(2, reminderLines.Length);

        foreach (string line in reminderLines)
        {
            Assert.DoesNotContain("删除", line);
            Assert.DoesNotContain("取消", line);
        }

        Assert.Contains("把起始日期改成今天", reminderLines[1]);
    }

    // ==================================================================
    //  那句最要紧的话：压缩包不会被删
    // ==================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 三种分支下都要说清压缩包不会被删(bool deleteLogs)
    {
        // 用户最怕的是"点下去备份就没了"。这一句在任何一个分支里都不能少 ——
        // 少了它，前面所有的技术性说明都压不住那一下手抖。
        foreach (AppSettings settings in new[]
        {
            Settings(),
            Settings(processExisting: true),
            Settings(effectiveFrom: new DateOnly(2026, 1, 1)),
        })
        {
            string text = ClearHistoryAdvice.For(settings, State())
                .ToConfirmationText(deleteLogs);

            Assert.Contains("压缩包不会被删除。", text);
            Assert.Contains("设置也不受影响", text);
        }
    }

    [Fact]
    public void 日志勾选框勾了才会提到日志()
    {
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(Settings(), State());

        Assert.DoesNotContain("日志文件", advice.ToConfirmationText(deleteLogFiles: false));
        Assert.Contains("磁盘上的日志文件", advice.ToConfirmationText(deleteLogFiles: true));
    }

    // ==================================================================
    //  按实际状态拼出来的部分
    // ==================================================================

    [Fact]
    public void 累计数字如实出现在确认框里()
    {
        // 3 GB 已经超过 int 上限，字面量得带 L —— 不带就是个编译期溢出。
        long threeGb = 3L * 1024 * 1024 * 1024;

        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(), State(archives: 42, bytes: threeGb));

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("42", text);
        Assert.Contains(ByteSize.Format(threeGb), text);
    }

    [Fact]
    public void 没有待上传和隔离时不出现那两行()
    {
        // "待上传记录将被清空（0 条）"只会稀释真正要紧的那几行。
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(Settings(), State());

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.DoesNotContain("待上传记录", text);
        Assert.DoesNotContain("隔离批次", text);
    }

    [Fact]
    public void 有待上传记录时条数如实报出()
    {
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(), State(pendingUploads: 3));

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("待上传记录 3 条", text);
    }

    [Fact]
    public void 有待上传记录时要说清没人再跟进确认()
    {
        // 这一条比"清掉 3 条记录"本身重要得多：那些包的本地空间不会再被释放，
        // 用户得知道这件事，否则会一直等一个不会来的"已完成"。
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(), State(pendingUploads: 3));

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("不会再自动释放本地空间", text);
        Assert.Contains("包本身仍在", text);
    }

    [Fact]
    public void 有隔离批次时条数如实报出()
    {
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(
            Settings(), State(quarantined: 2));

        Assert.Contains("隔离批次 2 条", advice.ToConfirmationText(deleteLogFiles: false));
    }

    [Fact]
    public void 全空状态也能拼出一段完整的话()
    {
        // 全新安装、什么都没跑过的情况下点清空 —— 正文不能是空的或者只剩一个标题。
        ClearHistoryAdvice advice = ClearHistoryAdvice.For(Settings(), State());

        string text = advice.ToConfirmationText(deleteLogFiles: false);

        Assert.Contains("程序会当作从未运行过", text);
        Assert.Contains("压缩包不会被删除", text);
        Assert.Contains("清空后只会处理往后新增的文件", text);
    }
}
