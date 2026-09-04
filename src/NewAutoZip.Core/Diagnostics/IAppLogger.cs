namespace NewAutoZip.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public readonly record struct LogRecord(DateTimeOffset TimestampLocal, LogLevel Level, string Message);

public interface IAppLogger
{
    void Log(LogLevel level, string message, Exception? ex = null);
}

public static class AppLoggerExtensions
{
    public static void Debug(this IAppLogger log, string message) => log.Log(LogLevel.Debug, message);

    public static void Info(this IAppLogger log, string message) => log.Log(LogLevel.Info, message);

    public static void Warn(this IAppLogger log, string message, Exception? ex = null) =>
        log.Log(LogLevel.Warn, message, ex);

    public static void Error(this IAppLogger log, string message, Exception? ex = null) =>
        log.Log(LogLevel.Error, message, ex);
}

/// <summary>测试与无日志场景使用。</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() { }

    public void Log(LogLevel level, string message, Exception? ex = null) { }
}

/// <summary>把同一条日志分发给多个接收端（文件 + UI）。任何一个接收端抛异常都不会影响其他接收端。</summary>
public sealed class CompositeLogger : IAppLogger
{
    private readonly IAppLogger[] _targets;

    public CompositeLogger(params IAppLogger[] targets) => _targets = targets;

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        foreach (IAppLogger target in _targets)
        {
            try
            {
                target.Log(level, message, ex);
            }
            catch
            {
                // 日志失败绝不允许影响业务流程 —— 这是旧版无限重打包的一类触发源。
            }
        }
    }
}
