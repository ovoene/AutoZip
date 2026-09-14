using NewAutoZip.Core.Interop;

namespace NewAutoZip.Core.Storage;

/// <summary>
/// 路径落在什么样的盘上。
///
/// 加这个是为了把"本地硬盘"从 <see cref="Configuration.CloudTarget.Folder"/> 里分出来 ——
/// 那个枚举的说明是"其他云盘 / 普通目录"，真实本地硬盘、Dropbox 同步目录、
/// UNC 网络共享全混在一起，而这三者的容量口径完全不同。
/// </summary>
public enum DriveKind
{
    /// <summary>读不到。<b>一律按远程处理</b> —— 见 <see cref="DiskSpaceInfo.Kind"/>。</summary>
    Unknown,

    /// <summary>本机硬盘 / U 盘 / 内存盘。卷的总容量与已用量对用户有直接意义。</summary>
    Local,

    /// <summary>UNC 共享或映射的网络盘。</summary>
    Remote,
}

/// <param name="Known">是否成功取到磁盘信息。取不到时不要拿 0 当"没空间"用。</param>
/// <param name="FreeBytes">当前用户可用字节数（已考虑配额）。</param>
/// <param name="TotalBytes">卷总容量。</param>
/// <param name="Kind">
/// 本地还是远程。<see cref="DriveKind.Unknown"/> 要<b>当成远程</b>对待：
/// 读不到盘的类型就别拿卷的数字去覆盖用户自己填的预算，
/// 这和本文件"<see cref="Known"/>=false 时不要拿 0 当没空间用"是同一条原则。
/// </param>
public readonly record struct DiskSpaceInfo(
    bool Known,
    long FreeBytes,
    long TotalBytes,
    DriveKind Kind = DriveKind.Unknown)
{
    public static DiskSpaceInfo Unknown { get; } = new(false, 0, 0, DriveKind.Unknown);

    /// <summary>是不是一块本机硬盘 —— 容量卡片和通知里那三个数要不要用卷的真实值，看这个。</summary>
    public bool IsLocalDisk => Kind == DriveKind.Local;

    public double UsedRatio => TotalBytes > 0 ? 1.0 - ((double)FreeBytes / TotalBytes) : 0;

    public override string ToString() => Known
        ? $"可用 {ByteSize.Format(FreeBytes)} / 共 {ByteSize.Format(TotalBytes)}"
        : "磁盘信息不可用";
}

public static class DiskSpace
{
    /// <summary>
    /// 查询 <paramref name="path"/> 所在卷的空间。path 可以是文件或目录，
    /// 不存在时逐级向上找到最近的存在目录（新建 ZipTemp 之前也要能预检）。
    /// </summary>
    public static DiskSpaceInfo Query(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DiskSpaceInfo.Unknown;
        }

        string? probe;

        try
        {
            probe = Path.GetFullPath(path);
        }
        catch
        {
            return DiskSpaceInfo.Unknown;
        }

        if (File.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        // 目录可能还不存在，向上找一个存在的祖先。
        int guard = 0;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe) && guard++ < 64)
        {
            probe = Path.GetDirectoryName(probe);
        }

        if (string.IsNullOrEmpty(probe))
        {
            return DiskSpaceInfo.Unknown;
        }

        string root = probe.EndsWith(Path.DirectorySeparatorChar) ? probe : probe + Path.DirectorySeparatorChar;

        DriveKind kind = ClassifyDrive(root);

        if (NativeMethods.GetDiskFreeSpaceEx(root, out ulong freeToCaller, out ulong total, out _))
        {
            return new DiskSpaceInfo(
                true,
                (long)Math.Min(freeToCaller, long.MaxValue),
                (long)Math.Min(total, long.MaxValue),
                kind);
        }

        // 退回 DriveInfo（UNC 路径上 GetDiskFreeSpaceEx 有时会失败）。
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(root) ?? root);
            if (drive.IsReady)
            {
                return new DiskSpaceInfo(true, drive.AvailableFreeSpace, drive.TotalSize, kind);
            }
        }
        catch
        {
            // 忽略
        }

        return DiskSpaceInfo.Unknown;
    }

    /// <summary>
    /// 判断一个<b>已经规范化过</b>的根路径落在什么盘上。
    ///
    /// UNC 必须先判：<c>new DriveInfo(@"\\nas\share")</c> 会抛
    /// （DriveInfo 只认盘符），走不到下面那一步。
    /// </summary>
    private static DriveKind ClassifyDrive(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return DriveKind.Remote;
        }

        try
        {
            string? pathRoot = Path.GetPathRoot(root);

            if (string.IsNullOrEmpty(pathRoot))
            {
                return DriveKind.Unknown;
            }

            return new DriveInfo(pathRoot).DriveType switch
            {
                DriveType.Fixed or DriveType.Removable or DriveType.Ram => DriveKind.Local,

                // 映射盘（Z: → \\nas\share）走这条。
                DriveType.Network => DriveKind.Remote,

                // NoRootDirectory / CDRom / Unknown：说不清就别当本地，
                // 下游会退回"按用户填的预算算"。
                _ => DriveKind.Unknown,
            };
        }
        catch
        {
            return DriveKind.Unknown;
        }
    }
}
