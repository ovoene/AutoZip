using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Interop;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Pipeline;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 云占位符状态的位解析。
///
/// 这一层是纯逻辑，可以完全确定性地测 —— 而它恰恰是旧版最脆弱的地方：
/// 旧版靠 Shell COM 拿本地化列名字符串做匹配，既没法测，也在非中文系统上全盘失效。
/// </summary>
public class CloudFileStatusTests
{
    private static CloudFileStatus Make(
        uint attributes = 0,
        long logical = 1024,
        long physical = 1024,
        bool queried = true,
        bool placeholder = true,
        bool inSync = false,
        bool partiallyOnDisk = false) =>
        new(true, attributes, logical, physical, queried, placeholder, inSync, partiallyOnDisk);

    [Fact]
    public void 缺失状态没有任何位()
    {
        CloudFileStatus missing = CloudFileStatus.Missing;

        Assert.False(missing.Exists);
        Assert.False(missing.IsPinned);
        Assert.False(missing.IsUnpinned);
        Assert.False(missing.HasRecallFlag);
        Assert.False(missing.IsDehydrated);
        Assert.Equal(0, missing.ReleasedBytes);
    }

    [Fact]
    public void 固定与仅联机两个位分别识别()
    {
        Assert.True(Make(attributes: FileAttributeFlags.Pinned).IsPinned);
        Assert.False(Make(attributes: FileAttributeFlags.Pinned).IsUnpinned);

        Assert.True(Make(attributes: FileAttributeFlags.Unpinned).IsUnpinned);
        Assert.False(Make(attributes: FileAttributeFlags.Unpinned).IsPinned);
    }

    [Theory]
    [InlineData(FileAttributeFlags.RecallOnOpen)]
    [InlineData(FileAttributeFlags.RecallOnDataAccess)]
    public void 任一回传标志位都算已脱水(uint flag)
    {
        // 这两位任意一个出现都意味着内容已经不在本地了。
        CloudFileStatus status = Make(attributes: flag, logical: 100 * 1024 * 1024, physical: 4096);

        Assert.True(status.HasRecallFlag);
        Assert.True(status.IsDehydrated);
    }

    [Fact]
    public void 标志位未刷新但占用已归零时也算已脱水()
    {
        // 中间态：属性位还没更新，但卷上占用已经掉下来了。
        // 只靠标志位判断会在这里误判成"还没释放"，于是白等到超时。
        CloudFileStatus status = Make(attributes: 0, logical: 100 * 1024 * 1024, physical: 4096);

        Assert.False(status.HasRecallFlag);
        Assert.True(status.IsDehydrated);
    }

    [Fact]
    public void 占用与逻辑大小相当时不算已脱水()
    {
        CloudFileStatus status = Make(attributes: 0, logical: 1_000_000, physical: 1_000_000);

        Assert.False(status.IsDehydrated);
        Assert.Equal(0, status.ReleasedBytes);
    }

    [Fact]
    public void 小文件不会因为簇对齐被误判为已脱水()
    {
        // 一个 3 KB 的文件在 4 KB 簇上实际占 4096 字节 —— physical 反而比 logical 大。
        // 阈值判定必须留出这个余量，否则每个小归档都会被当成"已经释放了"。
        CloudFileStatus status = Make(attributes: 0, logical: 3000, physical: 4096);

        Assert.False(status.IsDehydrated);
        Assert.Equal(0, status.ReleasedBytes);     // 不允许出现负数
    }

    [Fact]
    public void 逻辑大小为零的文件永远不算已脱水()
    {
        // 空文件的 physical 也是 0，0 * 32 < 0 不成立，但这里再兜一层：
        // 一个 0 字节的归档本身就是异常，不该被当成"释放成功"。
        Assert.False(Make(attributes: 0, logical: 0, physical: 0).IsDehydrated);
    }

    [Fact]
    public void 已释放字节数按逻辑与实际之差算()
    {
        CloudFileStatus status = Make(
            attributes: FileAttributeFlags.RecallOnDataAccess,
            logical: 10_000_000,
            physical: 4096);

        Assert.Equal(10_000_000 - 4096, status.ReleasedBytes);
    }

    [Fact]
    public void 查询失败时不谎报任何云状态()
    {
        CloudFileStatus status = Make(queried: false, placeholder: false, inSync: false);

        Assert.False(status.InSync);
        Assert.False(status.IsPlaceholder);
        Assert.False(status.PlaceholderQueried);
    }

    [Fact]
    public void 可写属性掩码不含只读位()
    {
        // 这一条是 RequestRelease 正确性的根 —— 把 GetFileAttributes 的结果原样回写，
        // 里面的 RECALL_ON_DATA_ACCESS 会让 SetFileAttributes 失败，"释放空间"静默无效。
        Assert.Equal(0u, FileAttributeFlags.SettableMask & FileAttributeFlags.RecallOnDataAccess);
        Assert.Equal(0u, FileAttributeFlags.SettableMask & FileAttributeFlags.RecallOnOpen);
        Assert.Equal(0u, FileAttributeFlags.SettableMask & FileAttributeFlags.Directory);

        // 而这两位必须留着 —— 它们正是我们要写进去的东西。
        Assert.NotEqual(0u, FileAttributeFlags.SettableMask & FileAttributeFlags.Pinned);
        Assert.NotEqual(0u, FileAttributeFlags.SettableMask & FileAttributeFlags.Unpinned);
    }
}

/// <summary>
/// 真实文件系统上的 Win32 查询。跑在普通 NTFS 目录里（不是 OneDrive），
/// 所以断言的是"非同步目录上的正确行为"—— 恰好也是旧版会误判成超时的场景。
/// </summary>
public class CloudFileStateTests
{
    [Fact]
    public void 查询不存在的文件返回缺失()
    {
        using TempDir dir = new();

        CloudFileStatus status = CloudFileState.Query(dir.File("没有这个文件.7z"));

        Assert.False(status.Exists);
        Assert.Same(CloudFileStatus.Missing, status);
    }

    [Fact]
    public void 路径为空不抛异常()
    {
        Assert.False(CloudFileState.Query("").Exists);
        Assert.False(CloudFileState.Query("   ").Exists);
        Assert.Null(CloudFileState.FindSyncRoot(""));
    }

    [Fact]
    public void 查询普通文件得到正确大小且不算已脱水()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("data.bin", 64 * 1024);

        CloudFileStatus status = CloudFileState.Query(path);

        Assert.True(status.Exists);
        Assert.Equal(64 * 1024, status.LogicalBytes);
        Assert.True(status.PhysicalBytes > 0, $"实际占用读成了 {status.PhysicalBytes}");
        Assert.False(status.HasRecallFlag);
        Assert.False(status.IsDehydrated);
        Assert.False(status.InSync);
        Assert.False(status.IsPlaceholder);
    }

    [Fact]
    public void 普通目录不在任何同步根之下()
    {
        // 临时目录一定不在 OneDrive 里。找不到同步根 —— 这正是启动前该提醒用户的情况。
        using TempDir dir = new();

        Assert.Null(CloudFileState.FindSyncRoot(dir.Path));
        Assert.False(OneDriveLocator.LooksSynced(dir.Path));
    }

    [Fact]
    public void 向上查找同步根不会因为路径怪异而死循环()
    {
        // 深路径、根目录、驱动器号都应当在有限步内返回。
        using TempDir dir = new();
        string deep = dir.Sub(Path.Combine("a", "b", "c", "d", "e", "f", "g"));

        Assert.Null(CloudFileState.FindSyncRoot(deep));
        Assert.Null(CloudFileState.FindSyncRoot(Path.GetPathRoot(dir.Path)!));
    }

    [Fact]
    public void 请求释放与取消释放可以往返且不破坏其他属性()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 4096);

        // 先带上一个别的可写属性，验证它在往返过程中被保留。
        File.SetAttributes(path, FileAttributes.Hidden);

        Assert.True(CloudFileState.RequestRelease(path, out string? releaseError), releaseError);

        CloudFileStatus afterRelease = CloudFileState.Query(path);
        Assert.True(afterRelease.IsUnpinned);
        Assert.False(afterRelease.IsPinned);
        Assert.True((afterRelease.Attributes & FileAttributeFlags.Hidden) != 0, "隐藏属性被顺手清掉了");

        // 普通 NTFS 文件不会真的脱水 —— 属性写上了，但内容还在本地。
        // 如实报告这一点比谎报成功重要得多。
        Assert.False(afterRelease.IsDehydrated);

        Assert.True(CloudFileState.RequestPin(path, out string? pinError), pinError);

        CloudFileStatus afterPin = CloudFileState.Query(path);
        Assert.True(afterPin.IsPinned);
        Assert.False(afterPin.IsUnpinned);
        Assert.True((afterPin.Attributes & FileAttributeFlags.Hidden) != 0);
    }

    [Fact]
    public void 重复请求释放是幂等的()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 4096);

        Assert.True(CloudFileState.RequestRelease(path, out _));
        Assert.True(CloudFileState.RequestRelease(path, out _));   // 第二次走短路分支

        Assert.True(CloudFileState.Query(path).IsUnpinned);
    }

    [Fact]
    public void 对不存在的文件请求释放返回失败并给出原因()
    {
        using TempDir dir = new();

        Assert.False(CloudFileState.RequestRelease(dir.File("不存在.7z"), out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 发现OneDrive账号不抛异常()
    {
        // 这台机器可能装了 OneDrive 也可能没装，两种情况都不许抛。
        IReadOnlyList<OneDriveAccount> accounts = OneDriveLocator.Discover();

        Assert.NotNull(accounts);
        Assert.All(accounts, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Path));
            Assert.False(string.IsNullOrWhiteSpace(a.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(a.Source));
        });

        // 同一个目录不允许出现两次（三个来源合并时要去重）。
        Assert.Equal(
            accounts.Select(a => a.Path.ToUpperInvariant()).Distinct().Count(),
            accounts.Count);

        // Preferred 只会给出真实存在的目录，或者什么都不给。
        OneDriveAccount? preferred = OneDriveLocator.Preferred();
        if (preferred is not null)
        {
            Assert.True(Directory.Exists(preferred.Path));
        }
    }

    /// <summary>OneDrive 在每个同步根目录里都会放这个固定名字的隐藏标记文件。</summary>
    private const string SyncRootMarker = ".849C9593-D756-4E56-8D6E-42412F2A707B";

    private static IEnumerable<string> RealSyncRoots() =>
        OneDriveLocator.Discover()
            .Select(a => a.Path)
            .Where(p => Directory.Exists(p) && File.Exists(Path.Combine(p, SyncRootMarker)));

    [Fact]
    public void 带标记文件的真实同步根必须被识别出来()
    {
        // 判定依据故意选了一个与 Cloud Files API 无关的信号（OneDrive 自己的标记文件），
        // 否则这个测试就只是在重复被测代码的逻辑。
        //
        // 这一条守的是一个真实回归：句柄版 FileAttributeTagInfo 在同步根目录上
        // 读不到 FILE_ATTRIBUTE_REPARSE_POINT，于是 FindSyncRoot 恒为 null，
        // 上传检测直接走进【这个目录不在同步范围内】的错误分支。
        string[] roots = RealSyncRoots().ToArray();

        if (roots.Length == 0)
        {
            return;     // 这台机器没装 OneDrive 或没登录，无从验证
        }

        Assert.All(roots, root =>
        {
            Assert.Equal(
                root.TrimEnd(Path.DirectorySeparatorChar),
                CloudFileState.FindSyncRoot(root)?.TrimEnd(Path.DirectorySeparatorChar));

            Assert.True(OneDriveLocator.LooksSynced(root), $"{root} 被误判成不在同步范围内");
        });
    }

    [Fact]
    public void 同步根之下的子目录也算在同步范围内()
    {
        string? root = RealSyncRoots().FirstOrDefault();

        if (root is null)
        {
            return;
        }

        string sub = Path.Combine(root, $"NewAutoZip-测试-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sub);

        try
        {
            // 新建的子目录自己还没被同步引擎接管，必须靠向上查找才能得到正确答案。
            Assert.Equal(
                root.TrimEnd(Path.DirectorySeparatorChar),
                CloudFileState.FindSyncRoot(sub)?.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(sub, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"\")]
    [InlineData(@"\\")]
    [InlineData("")]
    public void 同步根查找对结尾反斜杠不敏感(string suffix)
    {
        string? root = RealSyncRoots().FirstOrDefault();

        if (root is null)
        {
            return;
        }

        string probe = root.TrimEnd(Path.DirectorySeparatorChar) + suffix;

        Assert.Equal(
            root.TrimEnd(Path.DirectorySeparatorChar),
            CloudFileState.FindSyncRoot(probe)?.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void 卷根与不存在的路径都安全返回空()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            // 卷根不能当同步根，而且 FindFirstFileW 对 X:\ 会失败 —— 不许因此抛异常。
            Assert.Null(CloudFileState.FindSyncRoot(drive.Name));
        }

        Assert.Null(CloudFileState.FindSyncRoot(@"Z:\这个盘不存在\x\y"));
        Assert.Null(CloudFileState.FindSyncRoot("   "));
    }
}

/// <summary>
/// 上传判定。重点是"绝不谎报成功"—— 旧版在非中文系统、非同步目录、
/// 关闭空间释放这三种情况下都会误判，其中两种还会发出"上传完成"的通知。
/// </summary>
public class OneDriveUploaderTests
{
    private static PendingUpload Upload(string path, long bytes = 4096) =>
        new()
        {
            ArchivePath = path,
            Bytes = bytes,
            FileCount = 1,
            DeliveredUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            Polls = 1,
        };

    private static (OneDriveUploader Uploader, RecordingLogger Log) Build()
    {
        RecordingLogger log = new();
        return (new OneDriveUploader(log), log);
    }

    [Fact]
    public async Task 归档消失时报告消失()
    {
        using TempDir dir = new();
        (OneDriveUploader uploader, _) = Build();

        UploadCheckResult result = await uploader.CheckAsync(
            Upload(dir.File("走丢了.7z")), requestRelease: true, CancellationToken.None);

        Assert.Equal(UploadStatus.Vanished, result.Status);
        Assert.False(result.SpaceReleased);
        Assert.Equal(0, result.LocalBytes);
    }

    [Fact]
    public async Task 不在同步范围内时报告无法确认而不是完成()
    {
        // 旧版这种情况下死等两小时，然后仍旧发出"上传完成"通知 —— 最坏的一种错误。
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 8192);
        (OneDriveUploader uploader, RecordingLogger log) = Build();

        UploadCheckResult result = await uploader.CheckAsync(
            Upload(path), requestRelease: true, CancellationToken.None);

        Assert.Equal(UploadStatus.Unknown, result.Status);
        Assert.False(result.SpaceReleased);
        Assert.Contains("同步范围", result.Detail);
        Assert.True(log.Contains("不在 OneDrive 同步范围内"), log.Dump());
    }

    [Fact]
    public async Task 同一目录只抱怨一次()
    {
        using TempDir dir = new();
        string a = dir.WriteFile("a.7z", 4096);
        string b = dir.WriteFile("b.7z", 4096);
        (OneDriveUploader uploader, RecordingLogger log) = Build();

        for (int i = 0; i < 5; i++)
        {
            await uploader.CheckAsync(Upload(a), true, CancellationToken.None);
            await uploader.CheckAsync(Upload(b), true, CancellationToken.None);
        }

        // 一次上传等待期会轮询几百次，每次刷一条同样的警告就把日志淹了。
        Assert.Equal(1, log.CountContaining("不在 OneDrive 同步范围内"));
    }

    [Fact]
    public async Task 关闭空间释放时不写属性()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 4096);
        (OneDriveUploader uploader, _) = Build();

        await uploader.CheckAsync(Upload(path), requestRelease: false, CancellationToken.None);

        Assert.False(CloudFileState.Query(path).IsUnpinned);
    }

    [Fact]
    public async Task 开启空间释放时首轮就打上仅联机()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 4096);
        (OneDriveUploader uploader, RecordingLogger log) = Build();

        PendingUpload upload = Upload(path);
        Assert.False(upload.ReleaseRequested);

        await uploader.CheckAsync(upload, requestRelease: true, CancellationToken.None);

        // 先请求、再等生效：OneDrive 不会脱水尚未传完的文件，所以早设置是安全的，
        // 而"脱水成功"本身就成了"上传完成"的证据。
        Assert.True(CloudFileState.Query(path).IsUnpinned);
        Assert.True(log.Contains("已请求释放本地空间"), log.Dump());
    }

    [Fact]
    public async Task 已经请求过就不再重复写属性()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 4096);
        (OneDriveUploader uploader, RecordingLogger log) = Build();

        PendingUpload upload = Upload(path);
        upload.ReleaseRequested = true;

        await uploader.CheckAsync(upload, requestRelease: true, CancellationToken.None);

        Assert.False(CloudFileState.Query(path).IsUnpinned);
        Assert.False(log.Contains("已请求释放本地空间"));
    }

    [Fact]
    public async Task 取消令牌立即生效()
    {
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 4096);
        (OneDriveUploader uploader, _) = Build();

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => uploader.CheckAsync(Upload(path), true, cts.Token));
    }

    [Fact]
    public async Task 检查状态不会把文件变大也不会触发下载()
    {
        // FILE_FLAG_OPEN_NO_RECALL 的意义：查一次状态不能把刚释放的空间又占回来。
        // 普通文件上没法直接观察到回传，但至少要保证查询本身不改动文件。
        using TempDir dir = new();
        string path = dir.WriteFile("archive.7z", 32 * 1024);
        (OneDriveUploader uploader, _) = Build();

        DateTime before = File.GetLastWriteTimeUtc(path);
        long size = new FileInfo(path).Length;

        for (int i = 0; i < 20; i++)
        {
            await uploader.CheckAsync(Upload(path), requestRelease: false, CancellationToken.None);
        }

        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void 显示名称说清了判定依据()
    {
        (OneDriveUploader uploader, _) = Build();

        Assert.False(string.IsNullOrWhiteSpace(uploader.DisplayName));
    }
}

/// <summary>启动前校验里与 OneDrive 相关的那一条。</summary>
public class OneDriveValidationTests
{
    [Fact]
    public void 目标目录不在同步范围内时给出提醒但不拦启动()
    {
        using TempDir root = new();
        string monitor = root.Sub("monitor");
        string oneDrive = root.Sub("onedrive");
        string exe = root.WriteFile("7za.exe", 16);

        AppSettings settings = new()
        {
            MonitorPath = monitor,
            CloudPath = oneDrive,
            ZipTempPath = root.Sub("temp"),
            Password = "Str0ng-Passphrase-2026",
            NotifyChannel = NotifierKind.None,
            ReleaseLocalSpace = true,
        };

        ValidationReport report = SettingsValidator.Validate(settings, exe);

        // 不拦：程序照样能正确打包并移入目录，只是确认不了上传。有人就是想这么用。
        Assert.True(report.CanStart, report.ToText());

        ValidationIssue issue = Assert.Single(
            report.Warnings,
            i => i.Field == nameof(AppSettings.CloudPath) && i.Message.Contains("同步范围"));

        Assert.Contains("无法确认云端已收到", issue.Message);
        Assert.Contains("也无法释放本地空间", issue.Message);
    }

    [Fact]
    public void 关闭空间释放时提醒里不再提释放()
    {
        using TempDir root = new();
        string exe = root.WriteFile("7za.exe", 16);

        AppSettings settings = new()
        {
            MonitorPath = root.Sub("monitor"),
            CloudPath = root.Sub("onedrive"),
            ZipTempPath = root.Sub("temp"),
            Password = "Str0ng-Passphrase-2026",
            NotifyChannel = NotifierKind.None,
            ReleaseLocalSpace = false,
        };

        ValidationReport report = SettingsValidator.Validate(settings, exe);

        ValidationIssue issue = Assert.Single(
            report.Warnings,
            i => i.Field == nameof(AppSettings.CloudPath) && i.Message.Contains("同步范围"));

        Assert.DoesNotContain("也无法释放本地空间", issue.Message);
    }

    [Fact]
    public void OneDrive目录不存在时不重复报同步范围()
    {
        using TempDir root = new();
        string exe = root.WriteFile("7za.exe", 16);

        AppSettings settings = new()
        {
            MonitorPath = root.Sub("monitor"),
            CloudPath = Path.Combine(root.Path, "根本没建"),
            ZipTempPath = root.Sub("temp"),
            Password = "Str0ng-Passphrase-2026",
            NotifyChannel = NotifierKind.None,
        };

        ValidationReport report = SettingsValidator.Validate(settings, exe);

        // "目录不存在"已经是错误了，再叠一条"不在同步范围内"只是噪音。
        Assert.False(report.CanStart);
        Assert.DoesNotContain(report.Issues, i => i.Message.Contains("同步范围"));
    }
}

/// <summary>
/// 同步范围判定。这一组守的是用户报的真实问题：
/// OneDrive 目录下本来就会有子目录，归档也确实是往子目录里放的，
/// 结果被判成"不在同步范围内"，每个包都白等一轮超时。
///
/// 判定依据全部用 OneDrive 自己的同步根标记文件伪造，
/// 因此不依赖这台机器装没装 OneDrive、也不依赖按需文件是否可用。
/// </summary>
public class OneDriveScopeTests
{
    private const string SyncRootMarker = ".849C9593-D756-4E56-8D6E-42412F2A707B";

    /// <summary>造一个"看起来就是 OneDrive 同步根"的目录。</summary>
    private static string FakeSyncRoot(TempDir root, string name = "OneDrive - 默认目录")
    {
        string dir = root.Sub(name);
        File.WriteAllText(Path.Combine(dir, SyncRootMarker), string.Empty);
        return dir;
    }

    [Fact]
    public void 同步根标记文件本身就足以认定在范围内()
    {
        using TempDir root = new();
        string syncRoot = FakeSyncRoot(root);

        SyncScope scope = OneDriveScope.Resolve(syncRoot);

        Assert.True(scope.InScope);
        Assert.Equal(SyncScopeEvidence.MarkerFile, scope.Evidence);
        Assert.Equal(syncRoot, scope.Root);
    }

    [Theory]
    [InlineData("AutoZip_Bak")]
    [InlineData(@"备份\AutoZip_Bak")]
    [InlineData(@"a\b\c\d\e\AutoZip_Bak")]
    public void 任意深度的子目录都算在同步范围内(string relative)
    {
        using TempDir root = new();
        string syncRoot = FakeSyncRoot(root);
        string target = Path.Combine(syncRoot, relative);
        Directory.CreateDirectory(target);

        SyncScope scope = OneDriveScope.Resolve(target);

        Assert.True(scope.InScope, $"{target} 被误判成不在同步范围内");
        Assert.Equal(syncRoot, scope.Root);
    }

    [Fact]
    public void 子目录还没建出来也能靠上层判定()
    {
        using TempDir root = new();
        string syncRoot = FakeSyncRoot(root);

        // 首次运行时目标目录常常还不存在，这时也必须答对，否则启动就先弹一条错误提醒。
        SyncScope scope = OneDriveScope.Resolve(Path.Combine(syncRoot, "还没建", "AutoZip_Bak"));

        Assert.True(scope.InScope);
        Assert.Equal(syncRoot, scope.Root);
    }

    [Fact]
    public void 名字里带OneDrive的普通目录不算在范围内()
    {
        using TempDir root = new();

        // 谁都可以建一个叫 OneDrive 的普通文件夹，里面的东西永远不会被上传。
        string fake = root.Sub("OneDrive - 冒牌货");
        string sub = Path.Combine(fake, "AutoZip_Bak");
        Directory.CreateDirectory(sub);

        Assert.False(OneDriveScope.Resolve(sub).InScope);
        Assert.Equal(SyncScopeEvidence.None, OneDriveScope.Resolve(sub).Evidence);
        Assert.False(OneDriveLocator.LooksSynced(sub));
    }

    [Fact]
    public void 空路径与不存在的盘安全返回不在范围内()
    {
        Assert.False(OneDriveScope.Resolve("").InScope);
        Assert.False(OneDriveScope.Resolve("   ").InScope);
        Assert.False(OneDriveScope.Resolve(@"Z:\这个盘不存在\x\y").InScope);
    }

    [Fact]
    public void 启动校验对同步根子目录不再误报()
    {
        using TempDir root = new();
        string syncRoot = FakeSyncRoot(root);
        string target = Path.Combine(syncRoot, "AutoZip_Bak");
        Directory.CreateDirectory(target);

        string exe = root.WriteFile("7za.exe", 16);

        AppSettings settings = new()
        {
            MonitorPath = root.Sub("monitor"),
            CloudPath = target,
            ZipTempPath = root.Sub("temp"),
            Password = "Str0ng-Passphrase-2026",
            NotifyChannel = NotifierKind.None,
            ReleaseLocalSpace = true,
        };

        ValidationReport report = SettingsValidator.Validate(settings, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.DoesNotContain(report.Issues, i => i.Message.Contains("同步范围"));
    }
}
