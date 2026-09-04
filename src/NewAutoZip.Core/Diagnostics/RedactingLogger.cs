namespace NewAutoZip.Core.Diagnostics;

/// <summary>
/// 脱敏装饰器：套在整个日志管道最外层，任何 sink（文件、UI、通知）都拿不到明文密码。
///
/// 旧版把密码明文写进与压缩包同机存放的 backup_zip.log，加密形同虚设。
/// 把脱敏做成"管道入口的强制一步"而不是"调用方的自觉"，是因为后者一定会有人漏掉。
/// </summary>
public sealed class RedactingLogger : IAppLogger
{
    private readonly IAppLogger _inner;
    private readonly SecretRedactor _redactor;

    public RedactingLogger(IAppLogger inner, SecretRedactor? redactor = null)
    {
        _inner = inner;
        _redactor = redactor ?? SecretRedactor.Shared;
    }

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        // 异常的 ToString 里同样可能带上命令行或 URL，一并脱敏；
        // 但要保住类型名与堆栈，所以转成文本后拼进 message，而不是原样传下去。
        if (ex is null)
        {
            _inner.Log(level, _redactor.Redact(message));
            return;
        }

        _inner.Log(level, _redactor.Redact(message + " | " + ex));
    }
}
