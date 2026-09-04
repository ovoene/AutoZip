using System.Collections.Concurrent;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Watching;

/// <summary>
/// 目录监控。
///
/// 旧版每 <c>ScanIntervalSeconds</c> 秒对整个目录做一次 <c>Directory.GetFiles</c> 全量枚举
/// 并为每个文件 new 一个 FileInfo；目录里上万个文件时这非常费。
/// 新版以 FileSystemWatcher 事件为主、定时对账扫描为辅。
///
/// FileSystemWatcher 的两个坑必须处理：
///   * 内部缓冲区溢出会<b>静默丢事件</b> —— 必须监听 Error 事件并触发一次全目录对账；
///   * 出错后 EnableRaisingEvents 会变成 false，watcher 就此哑掉 —— 必须重建。
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _hints = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly IAppLogger _log;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private string _root = string.Empty;
    private bool _recurse;
    private int _overflowPending;
    private bool _disposed;

    public MonitorService(IAppLogger log) => _log = log;

    public bool IsRunning => _watcher is not null;

    public int PendingHintCount => _hints.Count;

    public void Start(string root, bool recurse)
    {
        lock (_gate)
        {
            StopCore();

            _root = root;
            _recurse = recurse;

            CreateWatcher();

            // 启动时先要求一次对账，把已有文件纳入视野。
            Interlocked.Exchange(ref _overflowPending, 1);
            Poke();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    private void CreateWatcher()
    {
        FileSystemWatcher watcher = new(_root)
        {
            IncludeSubdirectories = _recurse,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,   // 上限，尽量少丢事件
        };

        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Deleted += OnDeleted;
        watcher.Error += OnError;

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        _log.Info($"已开始监控目录：{_root}（包含子目录：{(_recurse ? "是" : "否")}）");
    }

    private void StopCore()
    {
        FileSystemWatcher? watcher = _watcher;
        _watcher = null;

        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Deleted -= OnDeleted;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        catch
        {
            // 释放失败无需处理
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => AddHint(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // 新名字是候选；旧名字的条目会在下一次 Refresh 里因"文件不存在"被剔除。
        AddHint(e.FullPath);
        Poke();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        // 不加入候选，只唤醒引擎让它尽快剔除已消失的条目。
        Poke();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref _overflowPending, 1);
        _log.Warn("FileSystemWatcher 报错（很可能是缓冲区溢出、事件已丢失），将触发一次全目录对账扫描。",
            e.GetException());

        // watcher 出错后会自行停止，必须重建，否则从此再也收不到任何事件。
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                StopCore();
                CreateWatcher();
            }
            catch (Exception ex)
            {
                _log.Error("重建 FileSystemWatcher 失败，将只依赖定时对账扫描。", ex);
            }
        }

        Poke();
    }

    private void AddHint(string path)
    {
        _hints[path] = 0;
        Poke();
    }

    /// <summary>主动唤醒等待中的引擎。</summary>
    public void Poke()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已有信号在等，忽略
            }
            catch (ObjectDisposedException)
            {
                // 正在关闭，忽略
            }
        }
    }

    /// <summary>取出并清空累积的候选路径。</summary>
    public string[] DrainHints()
    {
        if (_hints.IsEmpty)
        {
            return [];
        }

        string[] paths = _hints.Keys.ToArray();

        foreach (string path in paths)
        {
            _hints.TryRemove(path, out _);
        }

        return paths;
    }

    /// <summary>是否需要做一次全目录对账（首次启动或 watcher 丢过事件）。读取即清除。</summary>
    public bool ConsumeReconcileRequest() => Interlocked.Exchange(ref _overflowPending, 0) == 1;

    public void RequestReconcile() => Interlocked.Exchange(ref _overflowPending, 1);

    /// <summary>
    /// 等待"有事发生"或超时。返回 true 表示被事件唤醒，false 表示超时到点。
    /// 引擎用它实现"事件驱动为主、定时兜底为辅"。
    /// </summary>
    public async Task<bool> WaitForActivityAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await _signal.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }

        _signal.Dispose();
    }
}
