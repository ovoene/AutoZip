using System.Text.RegularExpressions;

namespace NewAutoZip.Core.Diagnostics;

/// <summary>
/// 日志脱敏。
///
/// 旧版 <c>LogZipSuccess</c> 把 <c>PASSWORD=xxx</c> 明文写进 backup_zip.log，
/// 而该日志与加密压缩包放在同一台机器上 —— 等于加密完全失效。
///
/// 这里做两层防护：
///   1. 注册过的具体秘密（密码 / Webhook / Token）逐字替换；
///   2. 通用模式兜底（7-Zip 的 -p 参数、URL 里的 key=/token=、Telegram 的 /bot&lt;token&gt;），
///      即使某个秘密忘记注册也不会泄漏。
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "***";

    /// <summary>
    /// 进程级共享实例。所有日志管道都用它，因此"某处忘记脱敏"这件事只可能发生一次
    /// （在 <see cref="RedactingLogger"/> 之外直接写文件时），而那是不存在的路径。
    /// </summary>
    public static SecretRedactor Shared { get; } = new();

    /// <summary>低于该长度的"秘密"不参与逐字替换：像 "233" 这种短串会把无关文本也涂掉。</summary>
    private const int MinLiteralLength = 4;

    private static readonly (Regex Pattern, string Replacement)[] GenericPatterns =
    [
        // 7-Zip 命令行密码：-p"xxx" / -pxxx
        (new Regex("-p(?:\"[^\"]*\"|[^\\s\"]+)", RegexOptions.Compiled), "-p" + Mask),

        // Telegram bot token：/bot123456:AAAA...
        (new Regex("/bot[0-9]+:[A-Za-z0-9_\\-]+", RegexOptions.Compiled), "/bot" + Mask),

        // URL 查询串里的敏感键
        (new Regex("(?<k>(?:key|token|access_token|secret|password|pwd)=)(?<v>[^&\\s\"']+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "${k}" + Mask),
    ];

    private volatile string[] _literals = [];

    /// <summary>注册当前配置里的全部秘密。可随配置变更重复调用（无锁替换整个数组）。</summary>
    public void SetSecrets(IEnumerable<string?> secrets)
    {
        _literals = secrets
            .Where(s => !string.IsNullOrEmpty(s) && s!.Length >= MinLiteralLength)
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            // 长的先替换，避免短秘密是长秘密子串时先被吃掉、留下残片
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        string result = input;

        foreach (string literal in _literals)
        {
            result = result.Replace(literal, Mask, StringComparison.Ordinal);
        }

        foreach ((Regex pattern, string replacement) in GenericPatterns)
        {
            result = pattern.Replace(result, replacement);
        }

        return result;
    }
}
