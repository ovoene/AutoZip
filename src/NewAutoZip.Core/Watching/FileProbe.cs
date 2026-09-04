using NewAutoZip.Core.Interop;

namespace NewAutoZip.Core.Watching;

/// <summary>
/// 单次探测结果。
/// </summary>
/// <param name="Exists">文件是否存在。</param>
/// <param name="Readable">是否能以共享读方式打开。为 false 说明写入方用了独占锁，7-Zip 也读不了。</param>
/// <param name="Length">实时大小（尽可能来自文件句柄而非目录项缓存）。</param>
/// <param name="LastWriteUtc">实时修改时间。</param>
public readonly record struct FileProbeResult(
    bool Exists,
    bool Readable,
    long Length,
    DateTimeOffset LastWriteUtc)
{
    public static FileProbeResult Missing { get; } = new(false, false, 0, default);
}

public interface IFileProbe
{
    FileProbeResult Probe(string path);

    IEnumerable<string> Enumerate(string root, bool recurse);
}

/// <summary>
/// 真实文件系统探测。
///
/// 与旧版的两个关键差异：
///   1. 旧版 <c>IsFileLocked</c> 用 <c>FileShare.None</c> <b>独占打开</b>被监控文件 ——
///      在写入方还开着句柄时这必然失败（所以被当成"仍在写入"），更糟的是如果恰好抢到了独占锁，
///      业务进程的下一次写入就会失败。这里改用 ReadWrite|Delete 共享，只读不打扰。
///   2. 大小与修改时间通过文件句柄查询（GetFileInformationByHandle），拿到的是实时值。
///      NTFS 对目录项的延迟更新会让 FileInfo 的值在持续写入期间保持不变，
///      从而把"正在写入"误判为"已稳定"。
/// </summary>
public sealed class Win32FileProbe : IFileProbe
{
    public static Win32FileProbe Instance { get; } = new();

    public FileProbeResult Probe(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);

            if (NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out NativeMethods.ByHandleFileInformation info))
            {
                return new FileProbeResult(
                    Exists: true,
                    Readable: true,
                    Length: info.Size,
                    LastWriteUtc: new DateTimeOffset(DateTime.FromFileTimeUtc(info.LastWriteTime.ToTicks()), TimeSpan.Zero));
            }

            // 句柄查询失败（极少见）：退回流长度 + 目录项时间。
            return new FileProbeResult(
                Exists: true,
                Readable: true,
                Length: stream.Length,
                LastWriteUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }
        catch (FileNotFoundException)
        {
            return FileProbeResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeResult.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被独占锁住或没有读权限。文件确实存在，但当前读不了。
            try
            {
                FileInfo info = new(path);
                if (!info.Exists)
                {
                    return FileProbeResult.Missing;
                }

                return new FileProbeResult(
                    Exists: true,
                    Readable: false,
                    Length: info.Length,
                    LastWriteUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            }
            catch
            {
                return FileProbeResult.Missing;
            }
        }
    }

    public IEnumerable<string> Enumerate(string root, bool recurse)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Directory | FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
        };

        try
        {
            return Directory.EnumerateFiles(root, "*", options);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}
