using System.Collections.Concurrent;
using System.Threading;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.App.Services;

/// <summary>
/// 界面侧的日志接收端。
///
/// 引擎在<b>任意后台线程</b>上写日志，界面每约 250 毫秒批量取走一次。
/// 这样做的原因是旧版每写一条日志就 <c>BeginInvoke</c> 一次：
/// 20 个文件、每轮每个文件推一条"写入中"，扫描间隔又允许填 0
/// （NumericUpDown 默认 Minimum=0），消息队列瞬间被打爆、界面彻底冻死。
///
/// 这里的队列有<b>硬上限</b>：写满了就丢最旧的并计数，绝不因为界面来不及消费
/// 而让内存无限增长 —— 备份程序会连续跑几个月。
/// </summary>
public sealed class UiLogSink : IAppLogger
{
    /// <summary>未被界面取走的日志上限。250ms 一轮，正常情况下只会有几条。</summary>
    private const int MaxPending = 4096;

    private readonly ConcurrentQueue<LogRecord> _pending = new();

    private int _pendingCount;
    private long _dropped;
    private int _minimumLevel = (int)LogLevel.Info;

    /// <summary>界面上的日志级别过滤。可以随时改，立即生效。</summary>
    public LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set => Volatile.Write(ref _minimumLevel, (int)value);
    }

    /// <summary>因界面消费不及而丢弃的条数。会在界面上如实显示，不藏着。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        string text = ex is null
            ? message

            // 界面上只带一行异常摘要；完整堆栈在文件日志里。
            // 旧版把整段堆栈塞进那个只有 12 行的列表框，等于把有用信息全挤掉了。
            : $"{message}（{ex.GetType().Name}：{ex.Message}）";

        _pending.Enqueue(new LogRecord(DateTimeOffset.Now, level, text));

        if (Interlocked.Increment(ref _pendingCount) <= MaxPending)
        {
            return;
        }

        // 超限：丢最旧的一条，保持队列长度恒定。
        if (_pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>界面线程调用，一次取走最多 <paramref name="max"/> 条。</summary>
    public int Drain(List<LogRecord> into, int max)
    {
        int taken = 0;

        while (taken < max && _pending.TryDequeue(out LogRecord record))
        {
            Interlocked.Decrement(ref _pendingCount);
            into.Add(record);
            taken++;
        }

        return taken;
    }
}
