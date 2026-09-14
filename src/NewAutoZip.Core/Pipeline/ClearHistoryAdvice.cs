using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Storage;

namespace NewAutoZip.Core.Pipeline;

/// <summary>
/// 清空历史记录会带来什么后果 —— 确认框的正文就是按它拼出来的。
///
/// 单独一个类型而不是在界面里现写字符串，是因为这里面有<b>真正的判断</b>：
/// 清空之后到底会不会把一大堆旧文件重新备份一遍，取决于用户当前的三种配置组合
/// （见 <see cref="RebackupScope"/>）。这种判断放在界面里没法写测试，
/// 而它恰恰是这个功能里最容易说错、后果也最大的一句话。
/// </summary>
public enum RebackupScope
{
    /// <summary>只处理往后的新文件。<c>Checkpoint</c> 复位成"现在"。</summary>
    NewFilesOnly,

    /// <summary>生效起始日期之后的文件会被整批重新备份。</summary>
    SinceEffectiveFrom,

    /// <summary>监控目录里现有的文件会被整批重新备份。</summary>
    AllExistingFiles,
}

/// <param name="Scope">清空之后下一次启动会重新备份哪些文件。</param>
/// <param name="EffectiveFrom">命中 <see cref="RebackupScope.SinceEffectiveFrom"/> 时的那个日期。</param>
/// <param name="PendingUploadCount">会被清掉的待上传记录条数。</param>
/// <param name="QuarantineCount">会被清掉的隔离批次条数。</param>
/// <param name="TotalArchivesCreated">会被归零的累计归档数。</param>
/// <param name="TotalBytesArchived">会被归零的累计字节数。</param>
public sealed record ClearHistoryAdvice(
    RebackupScope Scope,
    DateOnly? EffectiveFrom,
    int PendingUploadCount,
    int QuarantineCount,
    long TotalArchivesCreated,
    long TotalBytesArchived)
{
    /// <summary>
    /// 看一眼当前配置与状态，算出清空后的后果。
    ///
    /// 三个分支与 <see cref="BackupEngine"/> 里首次运行那段逻辑<b>必须保持一致</b>：
    /// 那边先看生效起始日期、再看"处理已有文件"、都没有才把 Checkpoint 设成当前时刻。
    /// 这里判反了，用户就会按一句错误的提示去做决定。
    /// </summary>
    public static ClearHistoryAdvice For(AppSettings settings, EngineState state)
    {
        RebackupScope scope = settings.EffectiveFrom is not null
            ? RebackupScope.SinceEffectiveFrom
            : settings.ProcessExistingFilesOnFirstRun
                ? RebackupScope.AllExistingFiles
                : RebackupScope.NewFilesOnly;

        return new ClearHistoryAdvice(
            scope,
            settings.EffectiveFrom,
            state.PendingUploads.Count,
            state.Quarantined.Count,
            state.TotalArchivesCreated,
            state.TotalBytesArchived);
    }

    /// <summary>
    /// 确认框的正文。分段拼，缺项就不出现那一段 ——
    /// 没有待上传记录时还硬写一句"待上传记录将被清空（0 条）"只会稀释真正要紧的那几行。
    /// </summary>
    public string ToConfirmationText(bool deleteLogFiles)
    {
        List<string> lines =
        [
            "以下内容将被清空，程序会当作从未运行过：",
            string.Empty,
            $"　· 累计统计（已生成 {TotalArchivesCreated} 个归档、共 {ByteSize.Format(TotalBytesArchived)}）",
            "　· 最近一次打包 / 投递 / 演练的时间与结果",
            "　· 增量水位线与首次运行标记",
        ];

        if (PendingUploadCount > 0)
        {
            lines.Add($"　· 待上传记录 {PendingUploadCount} 条");
        }

        if (QuarantineCount > 0)
        {
            lines.Add($"　· 隔离批次 {QuarantineCount} 条");
        }

        if (deleteLogFiles)
        {
            lines.Add("　· 磁盘上的日志文件");
        }

        lines.Add(string.Empty);

        // 这一行是整个对话框里最重要的一句：用户最怕的是"点下去备份就没了"。
        lines.Add("压缩包不会被删除。临时目录与云盘目录里的备份文件一个都不动，设置也不受影响。");
        lines.Add(string.Empty);

        switch (Scope)
        {
            case RebackupScope.SinceEffectiveFrom:
                lines.Add(
                    $"注意：你配置了生效起始日期 {EffectiveFrom:yyyy-MM-dd}。" +
                    "清空后，该日期之后的文件会被「重新备份一遍」。");
                lines.Add(
                    "　　若不想重来一遍，请先去「设置 → 工作时间」把起始日期改成今天，保存后再清空。");
                break;

            case RebackupScope.AllExistingFiles:
                lines.Add(
                    "注意：你勾选了「首次运行时处理已有文件」。" +
                    "清空后，监控目录里现有的文件会被「全部重新备份一遍」。");
                lines.Add("　　若不想重来一遍，请先去设置里取消那个勾选，保存后再清空。");
                break;

            default:
                lines.Add("清空后只会处理往后新增的文件，目录里已有的文件不会被重新备份。");
                break;
        }

        if (PendingUploadCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"另外：有 {PendingUploadCount} 个压缩包已移入云盘目录、还没确认上传完成。" +
                "清空后不再有人跟进确认，也不会再自动释放本地空间（包本身仍在）。");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
