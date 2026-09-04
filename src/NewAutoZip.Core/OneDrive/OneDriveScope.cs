namespace NewAutoZip.Core.OneDrive;

/// <summary>是靠哪一条证据认定"这个目录在 OneDrive 同步范围内"的。</summary>
public enum SyncScopeEvidence
{
    /// <summary>三条证据都不成立 —— 真的不在同步范围内。</summary>
    None = 0,

    /// <summary>Cloud Files 的 SYNC_ROOT 状态位。最权威。</summary>
    CloudFilesSyncRoot,

    /// <summary>OneDrive 自己在每个同步根目录里放的标记文件。</summary>
    MarkerFile,

    /// <summary>注册表 / 环境变量里登记的账号根目录。</summary>
    AccountRoot,
}

/// <param name="Root">找到的同步根目录；<c>null</c> 表示不在任何同步范围内。</param>
/// <param name="Evidence">认定依据。</param>
public sealed record SyncScope(string? Root, SyncScopeEvidence Evidence)
{
    public static SyncScope OutOfScope { get; } = new(null, SyncScopeEvidence.None);

    public bool InScope => Root is not null;

    /// <summary>
    /// 同步状态位是否可读。
    ///
    /// 只有 <see cref="SyncScopeEvidence.CloudFilesSyncRoot"/> 能说明按需文件功能对本进程可用，
    /// 也就是上传完成、空间释放这些判定拿得到真实信号。
    /// 靠标记文件或注册表认定时，目录确实在同步范围内，但状态位可能读不到，
    /// 这时只能如实告诉用户"无法确认"，不能假装成功。
    /// </summary>
    public bool StateReadable => Evidence == SyncScopeEvidence.CloudFilesSyncRoot;

    public string Describe() => Evidence switch
    {
        SyncScopeEvidence.CloudFilesSyncRoot => $"同步根目录：{Root}（按需文件状态位）",
        SyncScopeEvidence.MarkerFile => $"同步根目录：{Root}（OneDrive 同步根标记文件）",
        SyncScopeEvidence.AccountRoot => $"同步根目录：{Root}（已登录账号的本地目录）",
        _ => "不在任何 OneDrive 同步范围内",
    };
}

/// <summary>
/// 回答"这个目录在不在 OneDrive 的同步范围内"。
///
/// 为什么不能只看 Cloud Files 的 SYNC_ROOT 状态位：那一位在下面几种情况下读不到，
/// 而目录其实完全正常在同步 ——
///   * 用户把"按需文件（Files On-Demand）"关掉了，本地全是实体文件，没有占位符；
///   * 系统不给本进程返回云重解析标记（已实测到同一路径不同 API 给出不同属性）；
///   * Windows 10 1709 之前没有 cldapi.dll。
/// 只认这一位，就会对着一个正常的 OneDrive 子目录报"不在同步范围内"，
/// 于是每个压缩包都白等一轮超时。
///
/// 所以这里逐层向上找，任一条证据成立即认定在范围内：
///   1. Cloud Files 的 SYNC_ROOT 状态位；
///   2. OneDrive 在每个同步根目录里放的标记文件（与系统语言、与 API 都无关）；
///   3. 注册表 / 环境变量登记的账号根目录。
///
/// <b>子目录天然被覆盖</b>：同步根之下任意深度的目录都会在向上走的过程中命中根目录。
/// </summary>
public static class OneDriveScope
{
    /// <summary>
    /// OneDrive 在每一个同步根目录下放的标记文件名。
    /// 这个 GUID 是 OneDrive 自己固定使用的，与账号、语言、版本都无关。
    /// </summary>
    private const string SyncRootMarker = ".849C9593-D756-4E56-8D6E-42412F2A707B";

    public static SyncScope Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SyncScope.OutOfScope;
        }

        // 账号根目录只枚举一次，循环里逐层比对。
        List<string> accountRoots = KnownRoots();

        foreach (string ancestor in CloudFileState.Ancestors(path))
        {
            if (CloudFileState.IsSyncRoot(ancestor))
            {
                return new SyncScope(ancestor, SyncScopeEvidence.CloudFilesSyncRoot);
            }

            if (HasMarker(ancestor))
            {
                return new SyncScope(ancestor, SyncScopeEvidence.MarkerFile);
            }

            if (accountRoots.Any(root => PathEquals(root, ancestor)))
            {
                return new SyncScope(ancestor, SyncScopeEvidence.AccountRoot);
            }
        }

        return SyncScope.OutOfScope;
    }

    /// <summary>方便调用方只问"在不在范围内"。</summary>
    public static bool InScope(string path) => Resolve(path).InScope;

    // ==================================================================

    private static List<string> KnownRoots()
    {
        try
        {
            return
            [
                .. OneDriveLocator.Discover()
                    .Select(a => a.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool HasMarker(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, SyncRootMarker));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
