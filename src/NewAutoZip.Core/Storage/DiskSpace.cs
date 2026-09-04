using NewAutoZip.Core.Interop;

namespace NewAutoZip.Core.Storage;

/// <param name="Known">是否成功取到磁盘信息。取不到时不要拿 0 当"没空间"用。</param>
/// <param name="FreeBytes">当前用户可用字节数（已考虑配额）。</param>
/// <param name="TotalBytes">卷总容量。</param>
public readonly record struct DiskSpaceInfo(bool Known, long FreeBytes, long TotalBytes)
{
    public static DiskSpaceInfo Unknown { get; } = new(false, 0, 0);

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

        if (NativeMethods.GetDiskFreeSpaceEx(root, out ulong freeToCaller, out ulong total, out _))
        {
            return new DiskSpaceInfo(
                true,
                (long)Math.Min(freeToCaller, long.MaxValue),
                (long)Math.Min(total, long.MaxValue));
        }

        // 退回 DriveInfo（UNC 路径上 GetDiskFreeSpaceEx 有时会失败）。
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(root) ?? root);
            if (drive.IsReady)
            {
                return new DiskSpaceInfo(true, drive.AvailableFreeSpace, drive.TotalSize);
            }
        }
        catch
        {
            // 忽略
        }

        return DiskSpaceInfo.Unknown;
    }
}
