using System.Globalization;

namespace NewAutoZip.Core.Packing;

/// <summary>
/// 压缩包文件名的唯一来源。
///
/// 形如 <c>Backup_20260813_180900.7z</c> —— 前缀 + 秒级时间戳，<b>不带任何别的后缀</b>。
/// 单独拎出来是为了让"名字长什么样"这件事有一处可以直接测的地方：
/// 它会出现在通知里、出现在云端目录里，是用户唯一会长期看到的产物名。
/// </summary>
public static class ArchiveNaming
{
    public const string Extension = ".7z";

    /// <summary>时间戳格式。固定 <see cref="CultureInfo.InvariantCulture"/>，不受系统区域设置影响。</summary>
    public const string StampFormat = "yyyyMMdd_HHmmss";

    public static string Stamp(DateTimeOffset localTime) =>
        localTime.ToString(StampFormat, CultureInfo.InvariantCulture);

    public static string Build(string prefix, DateTimeOffset localTime) =>
        $"{Sanitize(prefix)}_{Stamp(localTime)}{Extension}";

    /// <summary>
    /// 前缀来自配置，用户可能敲进非法字符。挨个换成下划线，空前缀退回 <c>Backup</c>，
    /// 免得拼出一个 <c>File.Move</c> 直接抛异常的路径 —— 那正好落在失败分支上。
    /// </summary>
    private static string Sanitize(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "Backup";
        }

        char[] chars = prefix.Trim().ToCharArray();
        char[] invalid = Path.GetInvalidFileNameChars();

        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        string cleaned = new string(chars).Trim('.', ' ');

        return cleaned.Length == 0 ? "Backup" : cleaned;
    }
}
