using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NewAutoZip.Core.Interop;

/// <summary>
/// 本程序用到的全部 Win32 调用，集中在一处。
///
/// 旧版通过 <c>Shell.Application</c> COM + 遍历最多 500 个列名去匹配中文字符串 "可用性状态"
/// 来判断 OneDrive 状态：英文系统上直接失效，每次调用还新建 COM 实例且从不 ReleaseComObject。
/// 这里全部换成文件属性与磁盘 API —— 零 COM、零本地化依赖。
/// </summary>
internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";
    private const string CldApi = "cldapi.dll";

    internal const uint InvalidFileAttributes = 0xFFFFFFFF;
    internal const uint InvalidFileSize = 0xFFFFFFFF;
    internal const int NoError = 0;

    // ==================== 文件属性 ====================

    [LibraryImport(Kernel32, EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFileAttributes(string fileName);

    [LibraryImport(Kernel32, EntryPoint = "SetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileAttributes(string fileName, uint fileAttributes);

    // ==================== 实际占用字节 ====================

    /// <summary>
    /// 返回文件在卷上实际占用的字节数。OneDrive 把文件脱水成占位符后，
    /// 这个值会掉到接近 0，而逻辑大小保持不变 —— 这是"本地空间已释放"最可靠的信号。
    /// </summary>
    [LibraryImport(Kernel32, EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    // ==================== 句柄级文件信息 ====================

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToTicks() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;

        public long Size => ((long)FileSizeHigh << 32) | FileSizeLow;
    }

    /// <summary>
    /// 通过已打开的句柄查询大小与修改时间。
    ///
    /// 为什么不用 FileInfo：NTFS 对目录项里的大小/时间是<b>延迟更新</b>的，
    /// 一个正在被持续写入的文件，FileInfo.Length / LastWriteTime 可能长时间保持旧值
    /// （最长可达一小时）。用句柄查询拿到的是实时值，稳定性判定才可靠。
    /// </summary>
    [LibraryImport(Kernel32, EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    // ==================== 磁盘剩余空间 ====================

    /// <summary>支持本地卷与 UNC 路径（DriveInfo 不支持 UNC）。</summary>
    [LibraryImport(Kernel32, EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    // ==================== 云占位符状态（Cloud Filter API） ====================
    //
    //  这一组是取代旧版 Shell COM + 中文列名匹配的关键。
    //  CfGetPlaceholderStateFromFileInfo 返回的 IN_SYNC 位，就是资源管理器里那个
    //  "绿色对勾"背后的数据源 —— 纯 Win32、与系统显示语言无关。

    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileShareAll = 0x0000_0007;         // READ | WRITE | DELETE
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x0200_0000;   // 打开目录必需
    internal const uint FileFlagOpenNoRecall = 0x0010_0000;      // 绝不触发回传下载

    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo</summary>
    internal const int FileAttributeTagInfoClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    // -------------------- 目录项级别的属性读取 --------------------
    //
    //  为什么需要它：实测在同一个进程里，同一个同步根目录，
    //  GetFileAttributesW 与句柄版 FileAttributeTagInfo 都读不到
    //  FILE_ATTRIBUTE_REPARSE_POINT 与云重解析标记（attr=0x031 / tag=0），
    //  而 FindFirstFileW 读到的是真实值（attr=0x431 / tag=0x9000701A）。
    //  少了这两位，CfGetPlaceholderStateFromFileInfo 只能返回 NO_STATES，
    //  于是"这个目录在不在同步范围内"永远答错。
    //
    //  FindFirstFileW 只读目录项元数据，不会触发占位符回传下载，用它是安全的。

    /// <summary>WIN32_FIND_DATAW 的总字节数。</summary>
    internal const int FindDataSize = 592;

    /// <summary>WIN32_FIND_DATAW.dwFileAttributes 的偏移。</summary>
    internal const int FindDataAttributesOffset = 0;

    /// <summary>WIN32_FIND_DATAW.dwReserved0 的偏移 —— 重解析点的标记值就在这里。</summary>
    internal const int FindDataReparseTagOffset = 36;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [LibraryImport(Kernel32, EntryPoint = "FindFirstFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr FindFirstFile(string fileName, ref byte findFileData);

    [LibraryImport(Kernel32, EntryPoint = "FindClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindClose(IntPtr findFile);

    /// <summary>
    /// 由文件属性推导云占位符状态。返回 <see cref="PlaceholderState.Invalid"/> 表示查询失败。
    ///
    /// cldapi.dll 从 Windows 10 1709 起随系统提供。更早的系统上会抛
    /// <see cref="DllNotFoundException"/>，调用方必须接住并退回到属性位判断。
    /// </summary>
    [LibraryImport(CldApi, EntryPoint = "CfGetPlaceholderStateFromFileInfo", SetLastError = true)]
    internal static partial uint CfGetPlaceholderStateFromFileInfo(
        ref FileAttributeTagInfo infoBuffer,
        int infoClass);
}

/// <summary>
/// CF_PLACEHOLDER_STATE。<see cref="InSync"/> 是本程序最关心的一位：
/// 它表示"本地内容与云端一致"，也就是<b>上传已完成</b>。
/// </summary>
internal static class PlaceholderState
{
    internal const uint NoStates = 0x0000_0000;
    internal const uint Placeholder = 0x0000_0001;
    internal const uint SyncRoot = 0x0000_0002;
    internal const uint EssentialPropPresent = 0x0000_0004;
    internal const uint InSync = 0x0000_0008;
    internal const uint Partial = 0x0000_0010;
    internal const uint PartiallyOnDisk = 0x0000_0020;
    internal const uint Invalid = 0xFFFF_FFFF;
}

/// <summary>
/// 文件属性位。除标准属性外还包含 Windows Cloud Files（OneDrive 占位符）用到的几个，
/// 这些在 <see cref="FileAttributes"/> 枚举里没有对应项。
/// </summary>
internal static class FileAttributeFlags
{
    internal const uint ReadOnly = 0x00000001;
    internal const uint Hidden = 0x00000002;
    internal const uint System = 0x00000004;
    internal const uint Directory = 0x00000010;
    internal const uint Archive = 0x00000020;
    internal const uint Normal = 0x00000080;
    internal const uint Temporary = 0x00000100;

    /// <summary>文件内容不在本地（旧的离线属性，OneDrive 也会设）。</summary>
    internal const uint Offline = 0x00001000;

    internal const uint NotContentIndexed = 0x00002000;

    /// <summary>用户要求"始终保留在此设备上"。</summary>
    internal const uint Pinned = 0x00080000;

    /// <summary>用户要求"仅联机"，即允许同步引擎回收本地空间。</summary>
    internal const uint Unpinned = 0x00100000;

    /// <summary>打开文件即触发回传（较旧的占位符形态）。</summary>
    internal const uint RecallOnOpen = 0x00040000;

    /// <summary>读取数据时才回传 —— 已脱水的 OneDrive 占位符的标志位。</summary>
    internal const uint RecallOnDataAccess = 0x00400000;

    internal const uint NoScrubData = 0x00020000;

    internal const uint PlaceholderMask = RecallOnOpen | RecallOnDataAccess;

    /// <summary>
    /// <c>SetFileAttributes</c> 真正接受的属性位。
    ///
    /// 必须先按这个掩码过一遍再回写：直接把 <c>GetFileAttributes</c> 的结果原样写回去，
    /// 里面可能带着 RECALL_ON_DATA_ACCESS / REPARSE_POINT 这类只读位，调用会失败，
    /// 于是"请求释放空间"静默失效。
    /// </summary>
    internal const uint SettableMask =
        ReadOnly | Hidden | System | Archive | Temporary
        | Offline | NotContentIndexed | NoScrubData | Pinned | Unpinned;
}
