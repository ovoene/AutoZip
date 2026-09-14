using NewAutoZip.Core.Configuration;

namespace NewAutoZip.Core.Storage;

/// <summary>
/// 那三个数（总 / 已用 / 剩余）是按什么口径算出来的。
/// </summary>
public enum QuotaBasis
{
    /// <summary>按用户填的预算算。已用只数我们自己的包。云盘和远程目录用这个。</summary>
    Budget,

    /// <summary>按卷的真实容量算。已用是整个卷的占用（含别人的文件）。本地硬盘用这个。</summary>
    Volume,
}

/// <param name="Configured">这三个数有意义吗。没意义时别往界面和通知上放。</param>
/// <param name="QuotaBytes">总容量。<see cref="QuotaBasis.Volume"/> 下是卷总容量，否则是用户填的预算。</param>
/// <param name="UsedBytes">已用。<see cref="QuotaBasis.Volume"/> 下是整个卷的占用，否则只是我们的包。</param>
/// <param name="FreeBytes">
/// 剩余。<b>恒等于 <c>max(0, QuotaBytes - UsedBytes)</c></b> —— 这是本类型的核心不变量。
///
/// 曾经不是：普通目录模式下这里会被换成"该卷的真实可用空间"，而前两个数仍是
/// 预算口径，于是卡片上「已用 12.4 GB / 100 GB · 剩余 311.6 GB」三个数之间
/// 根本没有减法关系。那个"取更紧的一道"的判断没有消失，挪到了
/// <see cref="EffectiveFreeBytes"/> —— 它只参与判断，不参与显示。
/// </param>
/// <param name="DiskLimited">
/// 真正能放下的量是被<b>真实磁盘空间</b>卡住的，而不是被预算卡住的。
/// 提示文案要据此改口 —— "预算快满了"和"磁盘快满了"是两件事，解决办法也不同。
/// </param>
/// <param name="Basis">上面三个数的口径。</param>
/// <param name="OurBytes">我们自己的包占了多少。两种口径下都填，供"本程序备份包占 X"那句用。</param>
/// <param name="BudgetBytes">用户填的预算，0 = 没填。<see cref="QuotaBasis.Volume"/> 下与总容量是两回事。</param>
/// <param name="DiskFreeBytes">该卷真实可用空间，取不到时为 -1。只喂给 <see cref="EffectiveFreeBytes"/>。</param>
public readonly record struct CloudQuotaStatus(
    bool Configured,
    long QuotaBytes,
    long UsedBytes,
    long FreeBytes,
    bool DiskLimited,
    QuotaBasis Basis = QuotaBasis.Budget,
    long OurBytes = 0,
    long BudgetBytes = 0,
    long DiskFreeBytes = -1)
{
    public static CloudQuotaStatus NotConfigured { get; } = new(false, 0, 0, 0, false);

    /// <summary>
    /// 真正还能放下多少 —— 预算余量与磁盘真实可用空间取更紧的那一道。
    ///
    /// <b>只用于 <see cref="CanFit"/> / <see cref="IsLow"/> 的判断，绝不参与显示。</b>
    /// 显示一律走 <see cref="FreeBytes"/>，那个才和总容量、已用量对得上。
    /// </summary>
    public long EffectiveFreeBytes
    {
        get
        {
            // 预算那一道：Volume 口径下总容量是卷的，预算是另一回事，得单独算。
            long byBudget = Basis == QuotaBasis.Volume
                ? (BudgetBytes > 0 ? Math.Max(0, BudgetBytes - OurBytes) : long.MaxValue)
                : FreeBytes;

            long byDisk = DiskFreeBytes >= 0 ? DiskFreeBytes : long.MaxValue;

            return Math.Min(byBudget, byDisk);
        }
    }

    /// <summary>剩余是否已经低于告警线。<paramref name="warnBytes"/> 为 0 表示不告警。</summary>
    public bool IsLow(long warnBytes) => Configured && warnBytes > 0 && EffectiveFreeBytes <= warnBytes;

    /// <summary>再写 <paramref name="incomingBytes"/> 字节还放得下吗。</summary>
    public bool CanFit(long incomingBytes) => !Configured || EffectiveFreeBytes >= incomingBytes;
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
    ///
    /// 分两种口径，见 <see cref="QuotaBasis"/>：
    /// <list type="bullet">
    /// <item>本地硬盘（普通目录且盘是 <see cref="DriveKind.Local"/>）→ 三个数全取卷的真实值。
    /// 用户没填预算也照样有数 —— 卷的容量不需要谁来填。</item>
    /// <item>OneDrive / 远程目录 → 按用户填的预算算，没填就是"未配置"。</item>
    /// </list>
    /// </summary>
    public static CloudQuotaStatus Evaluate(AppSettings settings, long usedBytes)
    {
        long budget = settings.CloudQuotaBytes;

        // OneDrive 不看本地磁盘：按需文件会脱水，本地剩多少和云端还能放多少毫无关系。
        DiskSpaceInfo disk = settings.CloudTarget == CloudTarget.Folder
            ? DiskSpace.Query(settings.CloudPath)
            : DiskSpaceInfo.Unknown;

        long diskFree = disk.Known ? disk.FreeBytes : -1;

        // ---- 本地硬盘：三个数全用卷的真实值，减法天然成立 ----
        if (disk.Known && disk.IsLocalDisk && disk.TotalBytes > 0)
        {
            long volumeUsed = Math.Max(0, disk.TotalBytes - disk.FreeBytes);

            CloudQuotaStatus volume = new(
                Configured: true,
                QuotaBytes: disk.TotalBytes,
                UsedBytes: volumeUsed,
                FreeBytes: Math.Max(0, disk.TotalBytes - volumeUsed),
                DiskLimited: false,
                Basis: QuotaBasis.Volume,
                OurBytes: usedBytes,
                BudgetBytes: budget,
                DiskFreeBytes: diskFree);

            // 预算比磁盘更紧时才算"预算卡的"，否则就是磁盘卡的 —— 两种说法的解决办法不同。
            bool budgetTighter = budget > 0 && Math.Max(0, budget - usedBytes) < disk.FreeBytes;

            return volume with { DiskLimited = !budgetTighter };
        }

        // ---- 云盘 / 远程目录：按预算算，没填就没数 ----
        if (budget <= 0)
        {
            return CloudQuotaStatus.NotConfigured;
        }

        long free = Math.Max(0, budget - usedBytes);

        // 远程目录仍保留磁盘兜底：文件在那边是实打实落盘的。但它只影响
        // "放不放得下"的判断（EffectiveFreeBytes），不再顶替显示用的 FreeBytes。
        bool diskLimited = diskFree >= 0 && diskFree < free;

        return new CloudQuotaStatus(
            Configured: true,
            QuotaBytes: budget,
            UsedBytes: usedBytes,
            FreeBytes: free,
            DiskLimited: diskLimited,
            Basis: QuotaBasis.Budget,
            OurBytes: usedBytes,
            BudgetBytes: budget,
            DiskFreeBytes: diskFree);
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
