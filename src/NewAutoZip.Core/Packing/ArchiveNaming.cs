using System.Globalization;
using System.Text.RegularExpressions;

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
    /// 本程序起的名字：<c>前缀_yyyyMMdd_HHmmss.7z</c>，撞名时后面再加 <c>_2</c>… 或一段 32 位十六进制
    /// （见 BackupEngine.MakeUnique）。前缀不限 —— 用户中途改过前缀，老包也得认得出来。
    /// </summary>
    private static readonly Regex OursPattern = new(
        @"^.+_\d{8}_\d{6}(?:_(?:\d{1,4}|[0-9a-fA-F]{32}))?\.7z$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 这个文件名是不是本程序打出来的包。
    ///
    /// 临时目录按配额淘汰旧包、云端演练挑包来验、恢复页列包，全都只认这个名字。
    /// 原先认的是所有 <c>*.7z</c>：临时目录要是选了一个本来就放着别的压缩包的目录，
    /// 用户自己的包会被当成"超过保留天数的旧包"删掉；目标目录里别人的 .7z
    /// 会被拿去演练，然后报一条"现在的密码解不开这个包"的假警报。
    /// </summary>
    public static bool IsOurs(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && OursPattern.IsMatch(fileName);

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
