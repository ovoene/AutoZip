using Microsoft.Extensions.Time.Testing;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Packing;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Storage;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 云盘容量预算的算账逻辑。
///
/// OneDrive 的真实配额读不到（那个数字从不落盘，只在 OneDrive.exe 的内存里），
/// 所以总容量由用户自己填，已用量靠扫目标目录得出。
/// 这里钉的就是"扫得准"和"算得对"这两件事。
/// </summary>
public class CloudQuotaTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Origin = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static ZipTempManager Scanner(string path)
    {
        ZipTempManager mgr = new(new RecordingLogger(), new FakeTimeProvider(Origin));
        mgr.Configure(path);
        return mgr;
    }

    private static AppSettings Settings(TempDir root, long quota, CloudTarget target = CloudTarget.OneDrive) =>
        new()
        {
            CloudPath = root.Path,
            CloudTarget = target,
            CloudQuotaBytes = quota,
        };

    /// <summary>给一个归档配一份真的旁挂清单。</summary>
    private static string MakeSidecar(string archivePath)
    {
        string sidecarPath = ArchiveManifest.SidecarPathFor(archivePath);

        ArchiveManifest.WriteSidecarFile(sidecarPath, new ArchiveManifestSidecar
        {
            ArchiveFileName = Path.GetFileName(archivePath),
            ArchiveBytes = 1024,
            ArchiveSha256 = new string('a', 64),
            CreatedLocal = Origin,
            FileCount = 3,
        });

        return sidecarPath;
    }

    // ==================================================================
    //  MeasureUsed：扫目标目录
    // ==================================================================

    [Fact]
    public void 已用量是目录里所有包加起来()
    {
        using TempDir dir = new();
        dir.WriteFile("Backup_0.7z", 1024);
        dir.WriteFile("Backup_1.7z", 2048);
        dir.WriteFile("Backup_2.7z", 4096);

        Assert.Equal(1024 + 2048 + 4096, CloudQuota.MeasureUsed(Scanner(dir.Path)));
    }

    [Fact]
    public void 旁挂清单的体积也算进已用量()
    {
        // 清单跟着包一起躺在云盘目录里，一样占地方。
        // 漏算的那部分永远不会被任何一条上限看见 —— 预算就会慢慢失真。
        using TempDir dir = new();
        string archive = dir.WriteFile("Backup_0.7z", 1024);
        string sidecar = MakeSidecar(archive);

        long used = CloudQuota.MeasureUsed(Scanner(dir.Path));

        Assert.Equal(1024 + new FileInfo(sidecar).Length, used);
        Assert.True(used > 1024, "旁挂清单的体积没有计入");
    }

    [Fact]
    public void 目录里别人放的东西不计入()
    {
        // 只算我们自己产出的包。用户往云盘目录里放的照片、文档不是这个程序造成的占用，
        // 拿它们去拒绝用户的备份是越权。
        using TempDir dir = new();
        dir.WriteFile("Backup_0.7z", 1024);
        dir.WriteFile("家庭照片.jpg", 900_000);
        dir.WriteFile("报表.xlsx", 500_000);

        Assert.Equal(1024, CloudQuota.MeasureUsed(Scanner(dir.Path)));
    }

    [Fact]
    public void 目录为空或不存在时已用量是零而不是抛异常()
    {
        using TempDir empty = new();

        Assert.Equal(0, CloudQuota.MeasureUsed(Scanner(empty.Path)));
        Assert.Equal(0, CloudQuota.MeasureUsed(Scanner("Z:\\这个盘不存在\\OneDrive")));
    }

    [Fact]
    public void 手动删掉包之后已用量会回落()
    {
        // 这正是选"扫描"而不是"累计计数器"的理由：
        // 用户在资源管理器里删掉几个包，下一轮扫描数字就自动跟上。
        // 计数器只增不减，预算会永久失真。
        using TempDir dir = new();
        ZipTempManager scanner = Scanner(dir.Path);

        string a = dir.WriteFile("Backup_0.7z", 4096);
        dir.WriteFile("Backup_1.7z", 4096);

        Assert.Equal(8192, CloudQuota.MeasureUsed(scanner));

        File.Delete(a);

        Assert.Equal(4096, CloudQuota.MeasureUsed(scanner));
    }

    [Fact]
    public void 算账不会删掉云盘目录里的任何东西()
    {
        // 云盘目录里的包是备份本体，不是 ZipTemp 那种中转站。
        // 这个类只算账 —— 将来谁顺手在这里调了 Enforce，会先在这里被挡下。
        using TempDir dir = new();
        string archive = dir.WriteFile("Backup_0.7z", 1024);
        string sidecar = MakeSidecar(archive);

        CloudQuota.MeasureUsed(Scanner(dir.Path));
        CloudQuota.Evaluate(Settings(dir, 1 * GB), 1024);

        Assert.True(File.Exists(archive), "算一次账就把备份删了");
        Assert.True(File.Exists(sidecar), "算一次账就把清单删了");
    }

    // ==================================================================
    //  Evaluate：剩余量
    // ==================================================================

    [Fact]
    public void 没填总容量就是没启用()
    {
        // 新字段遇上老 settings.json 拿到的就是 0。
        // 这一条钉的是"老用户升级后行为完全不变"。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 0), usedBytes: 500 * GB);

        Assert.False(status.Configured);
        Assert.Equal(CloudQuotaStatus.NotConfigured, status);
    }

    [Fact]
    public void 没启用时什么都放得下也不告警()
    {
        CloudQuotaStatus status = CloudQuotaStatus.NotConfigured;

        Assert.True(status.CanFit(long.MaxValue), "没启用预算却拒绝了打包");
        Assert.False(status.IsLow(1 * GB));
    }

    [Fact]
    public void 剩余等于总容量减已用()
    {
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 100 * GB), usedBytes: 40 * GB);

        Assert.True(status.Configured);
        Assert.Equal(100 * GB, status.QuotaBytes);
        Assert.Equal(40 * GB, status.UsedBytes);
        Assert.Equal(60 * GB, status.FreeBytes);
        Assert.False(status.DiskLimited);
    }

    [Fact]
    public void 已用超过总容量时剩余夹在零而不是负数()
    {
        // 用户把预算调到比现有占用还小就会走到这里。
        // 负的剩余量会让后面每一处比较都反过来 —— 比如 CanFit 用负数去比会永远为假，
        // 而进度条拿到负数会画出个反向的条。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 10 * GB), usedBytes: 30 * GB);

        Assert.Equal(0, status.FreeBytes);
        Assert.False(status.CanFit(1));
    }

    [Fact]
    public void OneDrive模式不拿本地磁盘卡剩余量()
    {
        // 云端的包会脱水，本地卷还剩多少和云端还能放多少毫无关系。
        // 填 1 PB 预算就该得到 1 PB 的剩余，哪怕本机磁盘只有几百 GB。
        using TempDir dir = new();
        long petabyte = 1024L * 1024 * 1024 * 1024 * 1024;

        CloudQuotaStatus status = CloudQuota.Evaluate(
            Settings(dir, petabyte, CloudTarget.OneDrive), usedBytes: 0);

        Assert.Equal(petabyte, status.FreeBytes);
        Assert.False(status.DiskLimited, "OneDrive 模式被本地磁盘卡住了");
    }

    [Fact]
    public void 普通目录模式下预算再大也大不过磁盘()
    {
        // 预算里写着 1 PB，磁盘上没有 —— 此时"还能放 1 PB"是假话。
        // 取两者较小值，并且要说清是磁盘卡的，因为两种情况的解决办法不同：
        // 预算满了改设置就行，磁盘满了得腾地方。
        //
        // 这一条现在断言在 EffectiveFreeBytes 上：本地硬盘走 Volume 口径后，
        // FreeBytes 是"卷的真实可用"（也确实 < 1 PB），但真正承载
        // "取两者较小值"这条保护的是 EffectiveFreeBytes —— 判断用它，显示用前者。
        using TempDir dir = new();
        long petabyte = 1024L * 1024 * 1024 * 1024 * 1024;

        CloudQuotaStatus status = CloudQuota.Evaluate(
            Settings(dir, petabyte, CloudTarget.Folder), usedBytes: 0);

        Assert.True(status.EffectiveFreeBytes < petabyte,
            $"普通目录模式报了 {ByteSize.Format(status.EffectiveFreeBytes)} 可用，比任何真实磁盘都大");
        Assert.True(status.DiskLimited, "被磁盘卡住了却没说");
    }

    [Fact]
    public void 普通目录模式下预算比磁盘小时以预算为准()
    {
        // 反过来的方向：预算才是更紧的那一道，此时不该报成"磁盘卡的"，
        // 否则用户会去腾磁盘空间，而真正该改的是设置里那个数字。
        //
        // 同上：能放下多少看 EffectiveFreeBytes。FreeBytes 此时是卷的可用空间，
        // 与 QuotaBytes / UsedBytes 同属卷的口径，三者之间保持减法关系。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(
            Settings(dir, 1024, CloudTarget.Folder), usedBytes: 0);

        Assert.Equal(1024, status.EffectiveFreeBytes);
        Assert.False(status.DiskLimited, "预算才是更紧的那道，却报成了磁盘限制");
    }

    // ==================================================================
    //  CanFit / IsLow：两条判断分开，措辞和后果都不同
    // ==================================================================

    [Theory]
    [InlineData(5, true)]      // 正好填满，放得下
    [InlineData(4, true)]
    [InlineData(6, false)]     // 差 1 GB，放不下
    public void 放不放得下按剩余量判断(int incomingGb, bool expected)
    {
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 100 * GB), usedBytes: 95 * GB);

        Assert.Equal(5 * GB, status.FreeBytes);
        Assert.Equal(expected, status.CanFit(incomingGb * GB));
    }

    [Fact]
    public void 剩余低于警戒线时告警_但这一包还塞得下()
    {
        // 这两件事是分开的：告警是"快满了，注意一下"，拒绝是"这一包放不进去"。
        // 混在一起的后果是剩余一进入警戒区就再也不打包了。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 100 * GB), usedBytes: 95 * GB);

        Assert.True(status.IsLow(10 * GB), "剩余 5 GB 低于警戒线 10 GB，却没告警");
        Assert.True(status.CanFit(1 * GB), "告警不该顺手把打包也拒了");
    }

    [Fact]
    public void 警戒线为零表示不告警()
    {
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 100 * GB), usedBytes: 99 * GB);

        Assert.False(status.IsLow(0), "警戒线填 0 是不告警的意思");
    }

    [Fact]
    public void 剩余正好等于警戒线算已经触线()
    {
        // 边界取"包含"：警戒线的意思是"低到这里就该提醒了"，
        // 正好压在线上还不提醒，下一轮就已经在线下面了。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 100 * GB), usedBytes: 90 * GB);

        Assert.Equal(10 * GB, status.FreeBytes);
        Assert.True(status.IsLow(10 * GB));
    }

    [Fact]
    public void 剩余高于警戒线时不告警()
    {
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(Settings(dir, 100 * GB), usedBytes: 40 * GB);

        Assert.False(status.IsLow(10 * GB));
    }

    // ==================================================================
    //  QuotaSourceFor：容量卡片该读哪份数字
    //
    //  钉的是一个真实出现过的 bug：EngineSnapshot.Stopped 里容量四个字段写死是 0，
    //  而 CloudQuotaConfigured 判的是 CloudQuotaBytes > 0 ——
    //  于是只要引擎一停，用户明明填了容量，整张卡片就消失了。
    // ==================================================================

    [Fact]
    public void 引擎停着但配了预算_卡片仍然要显示()
    {
        // 这条就是那个 bug 本身。改坏了它必红。
        EngineSnapshot stopped = EngineSnapshot.Stopped;

        Assert.False(stopped.CloudQuotaConfigured, "前提变了：停止快照本来容量就是 0");

        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            stopped,
            new CloudQuotaStatus(true, 1 * GB, 340 * 1024 * 1024, 684 * 1024 * 1024, false),
            savedWarnBytes: 512 * 1024 * 1024);

        Assert.True(shown.CloudQuotaConfigured, "配了容量却还是不显示卡片");
        Assert.Equal(1 * GB, shown.CloudQuotaBytes);
        Assert.Equal(340 * 1024 * 1024, shown.CloudUsedBytes);
        Assert.Equal(684 * 1024 * 1024, shown.CloudFreeBytes);
        Assert.Equal(512 * 1024 * 1024, shown.CloudQuotaWarnBytes);
    }

    [Fact]
    public void 引擎在跑时以快照为准_不看磁盘上的配置()
    {
        // 正在跑的这一轮用的是启动时那份配置。换成磁盘上刚改的值，
        // 总览就会报一个还没生效的未来 —— 和云盘类型那边是同一条规矩。
        EngineSnapshot running = EngineSnapshot.Stopped with
        {
            Running = true,
            CloudQuotaBytes = 100 * GB,
            CloudUsedBytes = 40 * GB,
            CloudFreeBytes = 60 * GB,
            CloudQuotaWarnBytes = 10 * GB,
        };

        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            running,
            new CloudQuotaStatus(true, 1 * GB, 1, 1, false),
            savedWarnBytes: 1);

        Assert.Equal(100 * GB, shown.CloudQuotaBytes);
        Assert.Equal(40 * GB, shown.CloudUsedBytes);
        Assert.Equal(60 * GB, shown.CloudFreeBytes);
        Assert.Equal(10 * GB, shown.CloudQuotaWarnBytes);
    }

    [Fact]
    public void 引擎停着且没配预算_卡片照旧隐藏()
    {
        // 修那个 bug 不能把"没配容量就藏起来"一起修没了。
        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            EngineSnapshot.Stopped,
            CloudQuotaStatus.NotConfigured,
            savedWarnBytes: 0);

        Assert.False(shown.CloudQuotaConfigured);
    }

    [Fact]
    public void 停止后清掉预算_残留的旧数字要一起抹掉()
    {
        // 引擎跑过一轮、用户去设置里把总容量清成 0 再停机：
        // 快照里还留着上一轮的数字，只判 Configured 不归零的话，
        // 卡片虽然藏了，_quotaSource 里仍留着过期的值。
        EngineSnapshot leftover = EngineSnapshot.Stopped with
        {
            CloudQuotaBytes = 100 * GB,
            CloudUsedBytes = 40 * GB,
            CloudFreeBytes = 60 * GB,
            CloudQuotaWarnBytes = 10 * GB,
        };

        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            leftover,
            CloudQuotaStatus.NotConfigured,
            savedWarnBytes: 0);

        Assert.False(shown.CloudQuotaConfigured);
        Assert.Equal(0, shown.CloudQuotaBytes);
        Assert.Equal(0, shown.CloudUsedBytes);
        Assert.Equal(0, shown.CloudFreeBytes);
        Assert.Equal(0, shown.CloudQuotaWarnBytes);
    }

    [Fact]
    public void 警戒线也要跟着过来_否则停机后红色提示会消失()
    {
        // 警戒线不在 CloudQuotaStatus 里（它是 IsLow 的参数），是单独传的，
        // 最容易漏。漏了的话剩余明明已经触线，停机后却不红了。
        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            EngineSnapshot.Stopped,
            new CloudQuotaStatus(true, 100 * GB, 95 * GB, 5 * GB, false),
            savedWarnBytes: 10 * GB);

        Assert.True(shown.CloudQuotaLow, "剩余 5 GB 已低于警戒线 10 GB，卡片却没变红");
        Assert.Equal("警戒线 10.0 GB", shown.CloudQuotaWarnText);
    }

    [Fact]
    public void 只换容量那四个字段_其余原样带过()
    {
        // 这份快照是拼出来的。要是顺手把别的字段也动了，
        // 调用方拿它读阶段、读计数就会读到假的。
        EngineSnapshot stopped = EngineSnapshot.Stopped with
        {
            ArchivesCreated = 7,
            FilesArchived = 42,
            CloudTarget = CloudTarget.Folder,
        };

        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            stopped,
            new CloudQuotaStatus(true, 1 * GB, 0, 1 * GB, false),
            savedWarnBytes: 0);

        Assert.Equal(EnginePhase.Stopped, shown.Phase);
        Assert.False(shown.Running);
        Assert.Equal(7, shown.ArchivesCreated);
        Assert.Equal(42, shown.FilesArchived);
        Assert.Equal(CloudTarget.Folder, shown.CloudTarget);
    }
}

/// <summary>
/// 本地盘 / 远程盘的判定，以及本地盘那套"三个数全用卷的真实值"的口径。
///
/// 为什么要分本地远程：<see cref="CloudTarget.Folder"/> 的说明是"其他云盘 / 普通目录"，
/// 真实本地硬盘、Dropbox 同步目录、UNC 网络共享全混在一个值里，而三者的容量口径
/// 完全不同 —— 本地盘的卷容量是用户能直接理解的数，云盘的卷容量则毫无意义
/// （按需文件会脱水，本地占用和云端占用没有关系）。
/// </summary>
public class DriveKindTests
{
    private const long GB = 1024L * 1024 * 1024;

    // ==================================================================
    //  DiskSpace.Query：盘的类型
    // ==================================================================

    [Fact]
    public void 临时目录在本机硬盘上()
    {
        using TempDir dir = new();

        DiskSpaceInfo disk = DiskSpace.Query(dir.Path);

        Assert.True(disk.Known, "连临时目录所在卷都读不到");
        Assert.Equal(DriveKind.Local, disk.Kind);
        Assert.True(disk.IsLocalDisk);
    }

    [Fact]
    public void UNC路径判为远程()
    {
        // 这条必须在 DriveInfo 之前判 —— new DriveInfo(@"\\nas\share") 会抛，
        // 顺序反了的话 UNC 会掉进 catch 变成 Unknown。
        //
        // 共享不存在，所以 Known 是 false；但盘的类型是从路径形状看出来的，
        // 不需要连得上。这正是要点：连不上的网络盘更不能拿卷的数字去覆盖用户预算。
        DiskSpaceInfo disk = DiskSpace.Query(@"\\这台机器不存在\共享\备份");

        Assert.False(disk.IsLocalDisk, "UNC 路径被当成了本机硬盘");
    }

    [Fact]
    public void 判不出来的路径不算本地()
    {
        // 空路径、非法路径一律 Unknown。Unknown 要当远程处理：
        // 读不到盘的类型就别拿卷的数字去覆盖用户自己填的预算。
        Assert.Equal(DriveKind.Unknown, DiskSpace.Query(null).Kind);
        Assert.Equal(DriveKind.Unknown, DiskSpace.Query("   ").Kind);
        Assert.False(DiskSpace.Query(null).IsLocalDisk);
    }

    // ==================================================================
    //  Evaluate：本地硬盘走卷的口径
    //
    //  钉的是用户报的那个缺陷原文："剩余容量没有问题，但总容量减去已用是有问题的"。
    //  旧代码里 QuotaBytes 是预算、UsedBytes 只数我们的包，FreeBytes 却被换成了
    //  卷的真实可用空间 —— 三个数根本不是一个口径，减法当然对不上。
    // ==================================================================

    private static AppSettings LocalFolder(TempDir root, long quota) =>
        new()
        {
            CloudPath = root.Path,
            CloudTarget = CloudTarget.Folder,
            CloudQuotaBytes = quota,
        };

    [Fact]
    public void 本地硬盘下总减已用恒等于剩余()
    {
        // 这就是那个缺陷本身。改坏了它必红。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(LocalFolder(dir, 100 * GB), usedBytes: 12 * GB);

        Assert.Equal(QuotaBasis.Volume, status.Basis);
        Assert.True(status.QuotaBytes > 0, "本地硬盘的卷总容量不该是 0");
        Assert.Equal(status.QuotaBytes - status.UsedBytes, status.FreeBytes);
    }

    [Fact]
    public void 本地硬盘没填预算也照样有数()
    {
        // 卷的容量不需要谁来填。没填预算时卡片和消息里的容量行都该照常出现 ——
        // 这是"本地磁盘显示，远程不显示"那条里的本地这一半。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(LocalFolder(dir, 0), usedBytes: 0);

        Assert.True(status.Configured, "本地硬盘没填预算就整个不算了");
        Assert.Equal(QuotaBasis.Volume, status.Basis);
        Assert.Equal(0, status.BudgetBytes);
        Assert.Equal(status.QuotaBytes - status.UsedBytes, status.FreeBytes);
    }

    [Fact]
    public void 本地硬盘的已用是整个卷的占用_不只是我们的包()
    {
        // 卷上躺着别人的文件，那些也占地方。只数我们自己的包会报出一个
        // "还能放很多"的假象，而用户点开资源管理器看到的是另一个数。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(LocalFolder(dir, 100 * GB), usedBytes: 1024);

        Assert.Equal(1024, status.OurBytes);
        Assert.True(
            status.UsedBytes > status.OurBytes,
            $"卷已用 {ByteSize.Format(status.UsedBytes)} 不该等于我们的包 {ByteSize.Format(status.OurBytes)}");
    }

    [Fact]
    public void 本地硬盘下预算更紧时以预算为准_但显示的仍是卷的数()
    {
        // 两件事分开：显示看 FreeBytes（卷的口径，减法成立），
        // 能不能放下看 EffectiveFreeBytes（预算余量与磁盘可用取更紧的那个）。
        // 预算填 1 KB，那就只能再放 1 KB，哪怕盘上还有几百 GB。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(LocalFolder(dir, 4096), usedBytes: 3072);

        Assert.Equal(1024, status.EffectiveFreeBytes);
        Assert.False(status.CanFit(2048), "预算只剩 1 KB 却说放得下 2 KB");
        Assert.Equal(status.QuotaBytes - status.UsedBytes, status.FreeBytes);
    }

    [Fact]
    public void 本地硬盘没填预算时能放下的是磁盘真实可用()
    {
        // 没预算就只剩磁盘这一道闸。EffectiveFreeBytes 不能退化成 long.MaxValue，
        // 否则盘满了照样接着打包。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(LocalFolder(dir, 0), usedBytes: 0);

        Assert.Equal(status.FreeBytes, status.EffectiveFreeBytes);
        Assert.False(status.CanFit(long.MaxValue), "没预算就什么都放得下了");
    }

    [Fact]
    public void OneDrive模式永远不看盘的类型()
    {
        // 即便 CloudPath 指着本机硬盘上的 OneDrive 同步目录，也不能改用卷的口径：
        // 云端的包会脱水，本地卷剩多少和云端还能放多少毫无关系。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(
            new AppSettings
            {
                CloudPath = dir.Path,
                CloudTarget = CloudTarget.OneDrive,
                CloudQuotaBytes = 100 * GB,
            },
            usedBytes: 40 * GB);

        Assert.Equal(QuotaBasis.Budget, status.Basis);
        Assert.Equal(100 * GB, status.QuotaBytes);
        Assert.Equal(40 * GB, status.UsedBytes);
        Assert.Equal(60 * GB, status.FreeBytes);
    }

    [Fact]
    public void OneDrive没填预算仍然整个不显示()
    {
        // "本地磁盘显示，远程不显示"里的远程这一半。
        using TempDir dir = new();

        CloudQuotaStatus status = CloudQuota.Evaluate(
            new AppSettings
            {
                CloudPath = dir.Path,
                CloudTarget = CloudTarget.OneDrive,
                CloudQuotaBytes = 0,
            },
            usedBytes: 0);

        Assert.False(status.Configured);
    }

    // ==================================================================
    //  卡片文案：本地硬盘那句"其中本程序备份包"
    // ==================================================================

    [Fact]
    public void 本地硬盘的卡片要说清那么多不是备份占的()
    {
        // 卷已用 1.51 TB 直接摆上去，用户会以为全是备份包。
        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            EngineSnapshot.Stopped,
            new CloudQuotaStatus(
                Configured: true,
                QuotaBytes: 200 * GB,
                UsedBytes: 150 * GB,
                FreeBytes: 50 * GB,
                DiskLimited: false,
                Basis: QuotaBasis.Volume,
                OurBytes: 12 * GB,
                BudgetBytes: 100 * GB,
                DiskFreeBytes: 50 * GB),
            savedWarnBytes: 0);

        Assert.True(shown.CloudQuotaConfigured);
        Assert.Contains("其中本程序备份包 12.0 GB", shown.CloudQuotaUsageText);
        Assert.Contains("预算 100 GB", shown.CloudQuotaUsageText);
    }

    [Fact]
    public void 预算口径的卡片文案一个字都没变()
    {
        // 云盘那边的显示是老行为，不该被本地硬盘这次改动波及。
        EngineSnapshot shown = EngineSnapshot.QuotaSourceFor(
            EngineSnapshot.Stopped,
            new CloudQuotaStatus(true, 100 * GB, 40 * GB, 60 * GB, false),
            savedWarnBytes: 0);

        Assert.Equal("已用 40.0 GB / 100 GB · 剩余 60.0 GB", shown.CloudQuotaUsageText);
    }
}
