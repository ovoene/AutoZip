using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Storage;

namespace NewAutoZip.Core.OneDrive;

/// <summary>
/// 真实的上传检测：靠 Cloud Files 状态判断，不依赖系统语言，不用 COM。
///
/// 核心洞察（也是旧版两段等待可以合成一段的原因）：
/// <b>OneDrive 不会脱水一个尚未上传完成的文件</b>，所以"脱水成功"本身就是"上传完成"的证明。
/// 而 IN_SYNC 位又能在<b>不</b>脱水的情况下单独确认上传完成 ——
/// 于是用户关掉"释放本地空间"时，我们照样能给出可靠的完成判定。
/// 旧版在这种配置下只能死等到超时。
///
/// 这个类的每次 <see cref="CheckAsync"/> 都立刻返回，绝不在内部睡等，
/// 因此"停止"按钮永远即时生效（旧版是 <c>Thread.Sleep(60000)</c> 循环最长两小时）。
/// </summary>
public sealed class OneDriveUploader : IUploadMonitor
{
    private readonly IAppLogger _log;

    /// <summary>已经抱怨过"这个目录不在同步范围内"的路径，避免每轮刷一遍日志。</summary>
    private readonly HashSet<string> _warnedNotSynced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 连续多少次读到"还不是占位符"就改口说"本地读不到确认信号"。
    /// 目录确实在同步范围内的前提下，这么多次还没被接管，基本就是按需文件被关掉了。
    /// </summary>
    private const int NoPlaceholderTolerance = 15;

    public OneDriveUploader(IAppLogger log) => _log = log;

    public string DisplayName => "OneDrive 按需文件状态";

    public Task<UploadCheckResult> CheckAsync(PendingUpload upload, bool requestRelease, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Check(upload, requestRelease));
    }

    /// <summary>同步实现。全部是本地文件系统调用，微秒级，没有必要真的异步。</summary>
    private UploadCheckResult Check(PendingUpload upload, bool requestRelease)
    {
        string path = upload.ArchivePath;

        CloudFileStatus status = CloudFileState.Query(path);

        if (!status.Exists)
        {
            return new UploadCheckResult(
                UploadStatus.Vanished,
                "归档已不在 OneDrive 目录中（可能被手动删除或移走）。",
                0,
                false);
        }

        // 第一次轮询时把"仅联机"请求打上去。
        // 注意顺序：先请求、再等生效。OneDrive 会先把文件传完，然后才回收空间。
        if (requestRelease && !upload.ReleaseRequested && !status.IsUnpinned)
        {
            if (CloudFileState.RequestRelease(path, out string? releaseError))
            {
                _log.Debug($"已请求释放本地空间（仅联机）：{upload.FileName}");
            }
            else
            {
                _log.Warn($"请求释放本地空间失败：{upload.FileName}（{releaseError}）。" +
                          "上传检测不受影响，但本地空间可能不会被回收。");
            }
        }

        // ==============================================================
        //  判定
        // ==============================================================

        if (status.InSync)
        {
            bool released = status.IsDehydrated;

            string detail = released
                ? $"云端已收到，本地空间已释放（省下 {ByteSize.Format(status.ReleasedBytes)}）。"
                : requestRelease
                    ? "云端已收到。本地副本还在，OneDrive 会在稍后自行回收空间。"
                    : "云端已收到。按设置保留了本地副本。";

            return new UploadCheckResult(UploadStatus.Completed, detail, status.PhysicalBytes, released);
        }

        // 已经脱水但 IN_SYNC 没读到：内容都不在本地了，只可能是已经传完。
        // 这条兜底针对状态位刷新时序上的短暂不一致。
        if (status.IsDehydrated)
        {
            return new UploadCheckResult(
                UploadStatus.Completed,
                $"本地内容已被回收（省下 {ByteSize.Format(status.ReleasedBytes)}），" +
                "OneDrive 只会对已上传完成的文件这样做。",
                status.PhysicalBytes,
                true);
        }

        // ==============================================================
        //  还没完成 —— 区分"正在传"和"根本不会传"
        // ==============================================================

        if (!status.PlaceholderQueried)
        {
            string reason = CloudFileState.CloudApiAvailable
                ? "无法读取该文件的云同步状态（可能所在卷不支持按需文件）。"
                : "本系统不支持按需文件（Cloud Files），无法确认上传状态。";

            return new UploadCheckResult(UploadStatus.Unknown, reason, status.PhysicalBytes, false);
        }

        if (!status.IsPlaceholder)
        {
            // 同步引擎还没接管这个文件。三种可能：
            //   1. 刚放进去（正常，稍等）；
            //   2. 这个目录压根不在同步范围内（永远等不到）；
            //   3. 目录在范围内，但"按需文件"关掉了 —— 文件永远不会变成占位符，
            //      本地拿不到 IN_SYNC 信号。这一条旧版和上一版都会误当成 2。
            SyncScope scope = OneDriveScope.Resolve(path);

            if (!scope.InScope)
            {
                WarnNotSyncedOnce(path);

                return new UploadCheckResult(
                    UploadStatus.Unknown,
                    "这个目录不在 OneDrive 的同步范围内（向上逐层都找不到同步根目录），归档不会被上传。" +
                    "请在设置里把目标目录改到真正的 OneDrive 文件夹。",
                    status.PhysicalBytes,
                    false);
            }

            // 在范围内。给同步引擎足够的时间接管，之后如实说"本地读不到确认信号"，
            // 而不是一直挂着直到超时 —— 关掉按需文件的机器上每个包都要白等一轮。
            if (upload.Polls >= NoPlaceholderTolerance)
            {
                return new UploadCheckResult(
                    UploadStatus.Unknown,
                    $"归档在同步范围内（{scope.Describe()}），但检查 {upload.Polls} 次都没有变成云占位符。" +
                    "很可能这台机器关掉了“按需文件”，本地就拿不到上传完成的信号了。" +
                    "文件仍会被 OneDrive 正常上传，只是程序无法确认，也无法释放本地空间。",
                    status.PhysicalBytes,
                    false);
            }

            return new UploadCheckResult(
                UploadStatus.Pending,
                $"OneDrive 还没有开始处理这个文件（第 {upload.Polls} 次检查）。",
                status.PhysicalBytes,
                false);
        }

        string progress = status.PartiallyOnDisk
            ? "正在同步（内容部分已在本地）"
            : "正在上传";

        return new UploadCheckResult(
            UploadStatus.Pending,
            $"{progress}（第 {upload.Polls} 次检查，当前本地占用 {ByteSize.Format(status.PhysicalBytes)}）。",
            status.PhysicalBytes,
            false);
    }

    private void WarnNotSyncedOnce(string path)
    {
        string? directory = null;

        try
        {
            directory = Path.GetDirectoryName(path);
        }
        catch (Exception)
        {
            // 忽略
        }

        directory ??= path;

        lock (_warnedNotSynced)
        {
            if (!_warnedNotSynced.Add(directory))
            {
                return;
            }
        }

        // 旧版在这种情况下会死等两小时，然后按超时处理并且从不释放空间，
        // 却仍然发出"上传完成"的通知。这里把真相说清楚。
        _log.Warn(
            $"目标目录不在 OneDrive 同步范围内：{directory}。" +
            "归档会一直留在本地，不会被上传，也不会释放空间。请检查设置里的 OneDrive 目录。");
    }
}
