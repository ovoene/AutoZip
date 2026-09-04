using System.IO.Enumeration;

namespace NewAutoZip.Core.Watching;

/// <summary>
/// 文件筛选：通配符排除 + 目录排除。
///
/// 目录排除是防止"自我喂养"的最后一道防线：如果 ZipTemp 或 OneDrive 目录位于监控目录内，
/// 生成的 .7z 会被当成新增文件再次打包，越打越大直到磁盘满。
/// <see cref="Configuration.SettingsValidator"/> 会在启动前直接报错拦下这种配置，
/// 这里再兜一层，避免运行期被人改了目录结构。
/// </summary>
public sealed class FileFilter
{
    private readonly string[] _patterns;
    private readonly string[] _excludedDirectories;

    public FileFilter(IEnumerable<string>? excludePatterns, IEnumerable<string>? excludedDirectories = null)
    {
        _patterns = (excludePatterns ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _excludedDirectories = (excludedDirectories ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool Accept(string path)
    {
        foreach (string directory in _excludedDirectories)
        {
            if (path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string name = Path.GetFileName(path);
        if (name.Length == 0)
        {
            return false;
        }

        foreach (string pattern in _patterns)
        {
            // Windows 语义的通配符匹配（* ? 以及 DOS 的 <">= 特例），与资源管理器一致。
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeDirectory(string directory)
    {
        string full = Path.GetFullPath(directory);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>判断 <paramref name="candidate"/> 是否位于 <paramref name="ancestor"/> 之内（含相等）。</summary>
    public static bool IsSameOrUnder(string candidate, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(ancestor))
        {
            return false;
        }

        string a;
        string b;

        try
        {
            a = NormalizeDirectory(candidate);
            b = NormalizeDirectory(ancestor);
        }
        catch
        {
            return false;
        }

        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }
}
