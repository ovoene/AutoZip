using NewAutoZip.Core.Configuration;

namespace NewAutoZip.Core.Storage;

/// <param name="Configured">用户填了总容量预算吗。没填时其余字段全无意义。</param>
/// <param name="QuotaBytes">用户填的总容量。</param>
/// <param name="UsedBytes">目标目录里我们的包实际占用（含旁挂清单）。</param>
/// <param name="FreeBytes">还能放多少。普通目录模式下已与该卷真实可用空间取过较小值。</param>
/// <param name="DiskLimited">
/// 剩余量是被<b>真实磁盘空间</b>卡住的，而不是被预算卡住的。
/// 提示文案要据此改口 —— "预算快满了"和"磁盘快满了"是两件事，解决办法也不同。
/// </param>
public readonly record struct CloudQuotaStatus(
    bool Configured,
    long QuotaBytes,
    long UsedBytes,
    long FreeBytes,
    bool DiskLimited)
{
    public static CloudQuotaStatus NotConfigured { get; } = new(false, 0, 0, 0, false);

    /// <summary>剩余是否已经低于告警线。<paramref name="warnBytes"/> 为 0 表示不告警。</summary>
    public bool IsLow(long warnBytes) => Configured && warnBytes > 0 && FreeBytes <= warnBytes;

    /// <summary>再写 <paramref name="incomingBytes"/> 字节还放得下吗。</summary>
    public bool CanFit(long incomingBytes) => !Configured || FreeBytes >= incomingBytes;
}

/// <summary>
/// 云盘/目标目录的容量算账。
///
/// 为什么是"扫描"而不是"累计计数器"：用户在资源管理器里手动删掉几个包之后，
/// 计数器只增不减，预算会永久失真；而扫描下一轮就自动回落。
/// 何况"清空历史记录"会把计数器抹成 0 —— 两个功能会互相打架。
///
/// 已用量的口径是<b>我们自己产出的包</b>（<c>*.7z</c> + 旁挂清单），
/// 复用 <see cref="ZipTempManager.ListArchives"/>：它已经处理好了
/// "归档 + 清单一起算"、单文件读失败跳过这些细节。目录里别人放的东西不计入 ——
/// 那些不是这个程序制造的占用，拿它去拒绝用户的备份是越权。
///
/// <b>这个类只算账，不删东西。</b>云盘目录里的包是备份本体，
/// 与 ZipTemp 那种"中转站"性质完全不同，绝不套用 <see cref="ZipTempManager.Enforce"/>。
/// </summary>
public static class CloudQuota
{
    /// <summary>
    /// 算一遍当前占用。<paramref name="usedBytes"/> 由调用方扫出来传进来 ——
    /// 引擎每轮构造快照时本来就要扫一次，没必要在这里再扫一遍。
    /// </summary>
    public static CloudQuotaStatus Evaluate(AppSettings settings, long usedBytes)
    {
        if (settings.CloudQuotaBytes <= 0)
        {
            return CloudQuotaStatus.NotConfigured;
        }

        long quota = settings.CloudQuotaBytes;
        long free = Math.Max(0, quota - usedBytes);
        bool diskLimited = false;

        // 普通目录：预算再大也大不过磁盘。OneDrive 不做这一步 ——
        // 按需文件会脱水，本地剩余空间和云端还能放多少毫无关系。
        if (settings.CloudTarget == CloudTarget.Folder)
        {
            DiskSpaceInfo disk = DiskSpace.Query(settings.CloudPath);

            if (disk.Known && disk.FreeBytes < free)
            {
                free = disk.FreeBytes;
                diskLimited = true;
            }
        }

        return new CloudQuotaStatus(true, quota, usedBytes, free, diskLimited);
    }

    /// <summary>
    /// 扫目标目录，算出我们的包占了多少。目录为空或不存在时返回 0。
    /// </summary>
    public static long MeasureUsed(ZipTempManager scanner)
    {
        long total = 0;

        foreach (ArchiveInfo archive in scanner.ListArchives())
        {
            total += archive.TotalBytes;
        }

        return total;
    }
}
