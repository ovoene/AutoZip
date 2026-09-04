using System.Text;
using System.Threading.Channels;

namespace NewAutoZip.Core.Diagnostics;

/// <summary>
/// 轮转文件日志。
///
/// 设计要点：
///   * 写盘发生在后台任务里，业务线程只做一次 <c>TryWrite</c> —— 磁盘卡住不会拖慢打包流程。
///   * 队列有界（满则丢弃最新一条），因为「日志写不进去」绝不该演变成「程序卡死」。
///     旧版所有 IO 都是同步直写，磁盘满时连日志都能抛异常并中断主流程。
///   * 所有内容过一遍 <see cref="SecretRedactor"/>，密码永不落盘。
///   * <b>单个文件写满 <see cref="DefaultMaxBytesPerFile"/>（10 MB）就轮转</b>，
///     另外最多留 <see cref="DefaultMaxArchives"/> 个历史文件，占盘有硬上限
///     （当前 + 历史 = 3 个文件 = 30 MB）。
///     旧版的 <c>backup_zip.log</c> 只增不减，常驻跑几个月能长到几百 MB，
///     而且用记事本根本打不开 —— 出了事最需要翻的东西反而看不了。
/// </summary>
public sealed class RollingFileLogger : IAppLogger, IDisposable
{
    private const int QueueCapacity = 8192;

    /// <summary>
    /// 单个日志文件的上限：10 MB。
    ///
    /// 这个数字是「一个文本编辑器还能秒开、又足够装下一整轮备份的全部细节」之间的折中。
    /// 一条日志约 100 字节，10 MB 大概十万行 —— 按默认级别跑，够记好几个月。
    /// </summary>
    public const long DefaultMaxBytesPerFile = 10L * 1024 * 1024;

    /// <summary>
    /// 最多留几个<b>历史</b>文件（<c>app.1.log</c> / <c>app.2.log</c>），当前的 <c>app.log</c> 不算在内。
    ///
    /// 所以占盘上限是 (2 + 1) × 10 MB = 30 MB。这里刻意数的是「历史文件个数」而不是「总代数」：
    /// 轮转把 <c>app.log</c> 推成 <c>app.1.log</c>，历史和当前本来就是两回事，
    /// 混着数一定会差一个 —— 我自己第一版注释就差了一个。
    ///
    /// 为什么不是 0 个：那样一轮转就把历史全丢了，而排查问题恰恰要看「出事之前」那一段。
    /// </summary>
    public const int DefaultMaxArchives = 2;

    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytesPerFile;
    private readonly int _maxArchives;
    private readonly SecretRedactor _redactor;
    private readonly TimeProvider _time;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private long _droppedLines;
    private bool _disposed;

    public RollingFileLogger(
        string directory,
        SecretRedactor redactor,
        TimeProvider? time = null,
        string baseName = "app",
        long maxBytesPerFile = DefaultMaxBytesPerFile,
        int maxArchives = DefaultMaxArchives)
    {
        _directory = directory;
        _baseName = baseName;
        _maxBytesPerFile = Math.Max(64 * 1024, maxBytesPerFile);
        _maxArchives = Math.Max(1, maxArchives);
        _redactor = redactor;
        _time = time ?? TimeProvider.System;

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        _pump = Task.Run(PumpAsync);
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public string CurrentFile => Path.Combine(_directory, _baseName + ".log");

    /// <summary>因队列满而丢弃的行数，用于在日志里如实交代"这里缺了内容"。</summary>
    public long DroppedLines => Interlocked.Read(ref _droppedLines);

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinimumLevel || _disposed)
        {
            return;
        }

        string text = ex is null ? message : message + " | " + ex;
        string line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_time.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} [{Abbrev(level)}] {_redactor.Redact(text)}");

        if (!_queue.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _droppedLines);
        }
    }

    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    private async Task PumpAsync()
    {
        StringBuilder batch = new();

        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                batch.Clear();

                while (_queue.Reader.TryRead(out string? line))
                {
                    batch.Append(line).Append(Environment.NewLine);

                    // 单批上限，避免一次性构造巨大字符串
                    if (batch.Length > 256 * 1024)
                    {
                        break;
                    }
                }

                if (batch.Length > 0)
                {
                    WriteBatch(batch.ToString());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch
        {
            // 日志线程本身绝不允许把异常抛到 TaskScheduler.UnobservedTaskException
        }

        // 关闭前尽力把剩余内容刷出去
        try
        {
            batch.Clear();
            while (_queue.Reader.TryRead(out string? line))
            {
                batch.Append(line).Append(Environment.NewLine);
            }

            if (batch.Length > 0)
            {
                WriteBatch(batch.ToString());
            }
        }
        catch
        {
            // 忽略
        }
    }

    private void WriteBatch(string text)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            string path = CurrentFile;
            long existing = 0;
            FileInfo info = new(path);
            if (info.Exists)
            {
                existing = info.Length;
            }

            if (existing + Encoding.UTF8.GetByteCount(text) > _maxBytesPerFile)
            {
                Rotate();
            }

            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch
        {
            // 写日志失败只能放弃这一批，绝不抛出。
        }
    }

    private void Rotate()
    {
        try
        {
            // 超出保留个数的历史文件（比如上一版留下的 app.3.log / app.4.log）一并清掉。
            // 不清的话它们永远没人碰，占盘上限就成了一句空话。
            SweepBeyondLimit();

            // app.1.log -> app.2.log, ... , app.log -> app.1.log
            string oldest = Path.Combine(_directory, $"{_baseName}.{_maxArchives}.log");
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int i = _maxArchives - 1; i >= 1; i--)
            {
                string from = Path.Combine(_directory, $"{_baseName}.{i}.log");
                string to = Path.Combine(_directory, $"{_baseName}.{i + 1}.log");
                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }

            if (File.Exists(CurrentFile))
            {
                File.Move(CurrentFile, Path.Combine(_directory, $"{_baseName}.1.log"), overwrite: true);
            }
        }
        catch
        {
            // 轮转失败就继续往当前文件追加，宁可日志变大也不要中断。
        }
    }

    /// <summary>
    /// 删掉编号大于保留个数的历史日志。
    ///
    /// 编号是有限的（轮转最多推到 <see cref="_maxArchives"/>），所以从上限往后探十几个就够；
    /// 用 <c>Directory.GetFiles</c> 通配一遍也行，但那样得再解析一次编号，
    /// 而解析失败时该不该删是个说不清的判断 —— 这里宁可只删名字完全对得上的。
    /// </summary>
    private void SweepBeyondLimit()
    {
        for (int i = _maxArchives + 1; i <= _maxArchives + 16; i++)
        {
            string stale = Path.Combine(_directory, $"{_baseName}.{i}.log");

            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }
    }

    /// <summary>
    /// 在<b>当前线程</b>上把队列里已有的内容立刻写盘。
    ///
    /// 专门给"进程马上就要死了"的场合用（<c>AppDomain.UnhandledException</c>）：
    /// 那时后台写盘任务不一定还有机会被调度，而最后那几行恰恰是最重要的。
    /// 正常路径不要调用它 —— 那会把磁盘延迟带回业务线程。
    /// </summary>
    public void Flush()
    {
        try
        {
            StringBuilder batch = new();

            while (_queue.Reader.TryRead(out string? line))
            {
                batch.Append(line).Append(Environment.NewLine);
            }

            if (batch.Length > 0)
            {
                WriteBatch(batch.ToString());
            }
        }
        catch
        {
            // 刷日志本身绝不抛异常。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();

        try
        {
            // 给后台任务一点时间把队列刷完；超时就放弃，绝不阻塞退出。
            if (!_pump.Wait(TimeSpan.FromSeconds(3)))
            {
                _shutdown.Cancel();
                _pump.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
