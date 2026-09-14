using System.Collections.Concurrent;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Watching;

namespace NewAutoZip.Tests;

/// <summary>
/// 记录型日志。测试用它断言"该说的话说了"（例如失败时必须记录退避间隔），
/// 也用来在断言失败时把完整日志打出来，省掉猜。
/// </summary>
internal sealed class RecordingLogger : IAppLogger
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public IReadOnlyList<LogRecord> Records => [.. _records];

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        string text = ex is null ? message : message + " | " + ex.Message;
        _records.Enqueue(new LogRecord(DateTimeOffset.Now, level, text));
    }

    public bool Contains(string fragment) =>
        _records.Any(r => r.Message.Contains(fragment, StringComparison.Ordinal));

    public int CountContaining(string fragment) =>
        _records.Count(r => r.Message.Contains(fragment, StringComparison.Ordinal));

    public string Dump() =>
        string.Join(Environment.NewLine, _records.Select(r => $"[{r.Level}] {r.Message}"));
}

/// <summary>用后即焚的临时目录。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string label = "nazt")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{label}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>写一个指定大小的文件，返回完整路径。</summary>
    public string WriteFile(string name, int bytes = 16)
    {
        string full = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    /// <summary>
    /// 写一个<b>有指定内容</b>的文件（UTF-8 无 BOM），返回完整路径。
    ///
    /// <see cref="WriteFile"/> 写的是全零字节：两个不同名的文件只要大小相同，
    /// SHA-256 就完全一样。凡是要验"校验值对不对得上""内容有没有变"的测试，
    /// 用它就等于没测 —— 那种断言在哈希算错、甚至张冠李戴时照样通过。
    /// 所以涉及校验值与内容比对的场景一律用这个方法，给每个文件不同的内容。
    /// </summary>
    public string WriteText(string name, string content)
    {
        string full = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }

    public string Sub(string name)
    {
        string full = File(name);
        Directory.CreateDirectory(full);
        return full;
    }

    public int CountFiles(string pattern = "*") =>
        Directory.Exists(Path) ? Directory.GetFiles(Path, pattern).Length : 0;

    public string[] Files(string pattern = "*") =>
        Directory.Exists(Path) ? Directory.GetFiles(Path, pattern) : [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // 临时目录删不掉不算测试失败。
        }
    }
}

/// <summary>
/// 可编程的文件探测。让"文件还在写""文件被删了""文件被独占锁住"三种情况
/// 都能在没有真实 IO 竞争的前提下确定性复现 —— 这三种正是旧版静默假死的入口。
/// </summary>
internal sealed class FakeFileProbe : IFileProbe
{
    private readonly Dictionary<string, FileProbeResult> _files = new(StringComparer.OrdinalIgnoreCase);

    public int ProbeCount { get; private set; }

    public void Set(string path, long length, DateTimeOffset writeUtc, bool readable = true) =>
        _files[path] = new FileProbeResult(true, readable, length, writeUtc);

    public void Remove(string path) => _files.Remove(path);

    public FileProbeResult Probe(string path)
    {
        ProbeCount++;
        return _files.TryGetValue(path, out FileProbeResult r) ? r : FileProbeResult.Missing;
    }

    public IEnumerable<string> Enumerate(string root, bool recurse) => _files.Keys;
}

/// <summary>记录所有通知；可以配置成"永远抛异常"，验证通知故障绝不影响主流程。</summary>
internal sealed class RecordingNotificationHub : INotificationHub
{
    private readonly ConcurrentQueue<NotifyMessage> _sent = new();

    public string ChannelName => "测试";

    public bool IsEnabled => true;

    /// <summary>设为 true 时每次发送都抛异常，模拟钉钉限流 / Webhook 500 / TG 连不上。</summary>
    public bool ThrowOnSend { get; set; }

    public IReadOnlyList<NotifyMessage> Sent => [.. _sent];

    public int CountOf(NotifyEvent evt) => _sent.Count(m => m.Event == evt);

    public bool Any(NotifyEvent evt) => _sent.Any(m => m.Event == evt);

    /// <summary>某事件的第一条通知。没有就直接抛，并把实际发出过的事件列出来便于排查。</summary>
    public NotifyMessage First(NotifyEvent evt) =>
        _sent.FirstOrDefault(m => m.Event == evt)
        ?? throw new InvalidOperationException(
            $"没有发出 {evt} 通知。实际发出的是：{string.Join("、", _sent.Select(m => m.Event))}");

    /// <summary>某事件在发送序列中的位置，用来断言先后顺序。没有则返回 -1。</summary>
    public int IndexOf(NotifyEvent evt)
    {
        NotifyMessage[] all = [.. _sent];

        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].Event == evt)
            {
                return i;
            }
        }

        return -1;
    }

    public Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct)
    {
        _sent.Enqueue(message);

        if (ThrowOnSend)
        {
            throw new HttpRequestException("测试注入的通知故障");
        }

        return Task.FromResult(NotifyResult.Success(1));
    }
}

/// <summary>可编程的上传监视器。默认立刻确认完成。</summary>
internal sealed class FakeUploadMonitor : IUploadMonitor
{
    public string DisplayName => "测试上传监视器";

    public UploadStatus Status { get; set; } = UploadStatus.Completed;

    public bool SpaceReleased { get; set; } = true;

    public int Checks { get; private set; }

    public Task<UploadCheckResult> CheckAsync(PendingUpload upload, bool requestRelease, CancellationToken ct)
    {
        Checks++;
        return Task.FromResult(new UploadCheckResult(Status, "测试", upload.Bytes, SpaceReleased));
    }
}

/// <summary>
/// 仓库里那个真实的 7za.exe。
///
/// 引擎测试默认指向一个不存在的路径来制造失败（覆盖失败分支），
/// 但"通知格式"这类断言必须走成功路径 —— 那就得有真家伙。
/// 测试程序集自己的输出目录里没有它（tools\7za.exe 是 App 项目的 Content），
/// 所以从测试 bin 往上找仓库根。找不到就失败而不是跳过：它是仓库的一部分，缺了本身就是缺陷。
/// </summary>
internal static class TestBinaries
{
    public static string SevenZip()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "NewAutoZip.App", "tools", "7za.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            @"找不到 src\NewAutoZip.App\tools\7za.exe。它是随程序发布的压缩引擎，必须在仓库里。");
    }
}
