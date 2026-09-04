using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NewAutoZip.Core.Interop;

namespace NewAutoZip.Core.OneDrive;

/// <summary>
/// 一个文件在 OneDrive 眼里的状态。
/// </summary>
/// <param name="Exists">文件是否还在。</param>
/// <param name="Attributes">原始文件属性位（查询失败时为 0）。</param>
/// <param name="LogicalBytes">逻辑大小。脱水后<b>不变</b>。</param>
/// <param name="PhysicalBytes">在卷上实际占用的字节数。脱水后掉到接近 0。</param>
/// <param name="PlaceholderQueried">Cloud Filter API 是否成功给出了答案。</param>
/// <param name="IsPlaceholder">已被同步引擎接管，成为云占位符。</param>
/// <param name="InSync">
/// 本地内容与云端一致 —— 也就是<b>上传已完成</b>。
/// 这一位就是资源管理器里"绿色对勾"的数据源。
/// </param>
/// <param name="PartiallyOnDisk">内容只有一部分在本地（正在回传或正在脱水中间态）。</param>
public sealed record CloudFileStatus(
    bool Exists,
    uint Attributes,
    long LogicalBytes,
    long PhysicalBytes,
    bool PlaceholderQueried,
    bool IsPlaceholder,
    bool InSync,
    bool PartiallyOnDisk)
{
    public static CloudFileStatus Missing { get; } =
        new(false, 0, 0, 0, false, false, false, false);

    /// <summary>用户要求"始终保留在此设备上"，同步引擎不会回收它的空间。</summary>
    public bool IsPinned => (Attributes & FileAttributeFlags.Pinned) != 0;

    /// <summary>已请求"仅联机"，同步引擎可以回收空间。</summary>
    public bool IsUnpinned => (Attributes & FileAttributeFlags.Unpinned) != 0;

    /// <summary>带回传标志位 —— 内容已经不在本地了。</summary>
    public bool HasRecallFlag => (Attributes & FileAttributeFlags.PlaceholderMask) != 0;

    /// <summary>
    /// 本地空间已经被释放。
    ///
    /// 判定用两个独立证据：带回传标志位（内容不在本地）<b>或者</b>实际占用远小于逻辑大小。
    /// 后者对付的是"标志位还没刷新但空间已经回收"的中间态。
    /// </summary>
    public bool IsDehydrated =>
        Exists
        && LogicalBytes > 0
        && (HasRecallFlag || PhysicalBytes * 32 < LogicalBytes);

    /// <summary>已释放的字节数（尽力估算，用于日志与通知里如实交代收益）。</summary>
    public long ReleasedBytes => Math.Max(0, LogicalBytes - PhysicalBytes);
}

/// <summary>
/// 读取 Windows Cloud Files（OneDrive 按需文件）状态，并请求释放本地空间。
///
/// 这是整个重写里技术上最关键的一处替换。旧版
/// <c>OneDriveSyncHelper</c> 通过 <c>Shell.Application</c> COM 遍历最多 500 个列，
/// 拿列名去匹配中文字符串 "可用性状态"，再匹配 "同步" / "在此设备上可用" / "联机"：
///   * 英文（或任何非中文）系统上<b>全部失效</b> → 死等两小时 → 误报超时且永不释放空间；
///   * 每次调用新建 COM 实例且从不 ReleaseComObject，一次上传等待期约 6 万次 COM 往返 + 泄漏。
///
/// 新版只用两个纯 Win32 信号，与系统显示语言完全无关：
///   1. <c>CfGetPlaceholderStateFromFileInfo</c> 的 IN_SYNC 位 —— 上传是否完成；
///   2. <c>GetCompressedFileSize</c> 的实际占用 —— 本地空间是否已释放。
///
/// 所有方法都不抛异常，失败时如实返回"查不到"，绝不谎报成功。
/// </summary>
public static class CloudFileState
{
    /// <summary>cldapi.dll 是否可用。老系统上一次失败后就不再重试。</summary>
    private static int _cloudApiUsable = -1;   // -1 未知 / 0 不可用 / 1 可用

    public static bool CloudApiAvailable => Volatile.Read(ref _cloudApiUsable) != 0;

    /// <summary>查询一个文件的云状态。任何环节失败都只会让结果里的信息变少，不会抛异常。</summary>
    public static CloudFileStatus Query(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return CloudFileStatus.Missing;
        }

        uint attributes = NativeMethods.GetFileAttributes(path);

        if (attributes == NativeMethods.InvalidFileAttributes)
        {
            return CloudFileStatus.Missing;
        }

        long logical = LogicalSize(path);
        long physical = PhysicalSize(path);

        (bool queried, uint state) = QueryPlaceholderState(path, isDirectory: false);

        return new CloudFileStatus(
            Exists: true,
            Attributes: attributes,
            LogicalBytes: logical,
            PhysicalBytes: physical,
            PlaceholderQueried: queried,
            IsPlaceholder: queried && (state & PlaceholderState.Placeholder) != 0,
            InSync: queried && (state & PlaceholderState.InSync) != 0,
            PartiallyOnDisk: queried && (state & PlaceholderState.PartiallyOnDisk) != 0);
    }

    /// <summary>
    /// 从 <paramref name="path"/> 向上找同步根目录（CF_PLACEHOLDER_STATE_SYNC_ROOT）。
    ///
    /// 只看 Cloud Files 状态位。这是最权威的一条证据，但并非唯一 ——
    /// 关掉"按需文件"、或系统不给本进程返回云重解析标记时它会失灵，
    /// 所以对外的判定请用 <see cref="OneDriveScope.Resolve"/>，那里还有另外两条独立证据。
    /// </summary>
    public static string? FindSyncRoot(string path)
    {
        foreach (string ancestor in Ancestors(path))
        {
            if (IsSyncRoot(ancestor))
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>这一层目录自己是不是 Cloud Files 同步根。</summary>
    public static bool IsSyncRoot(string directory)
    {
        (bool queried, uint state) = QueryPlaceholderState(directory, isDirectory: true);

        return queried && (state & PlaceholderState.SyncRoot) != 0;
    }

    /// <summary>
    /// 从 <paramref name="path"/> 自身开始逐层向上枚举目录。
    /// 传入的是文件就从它所在目录开始；最多 64 层，避免任何路径异常导致死循环。
    /// </summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        string? current;

        try
        {
            current = Path.GetFullPath(path);

            if (File.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception)
        {
            yield break;
        }

        for (int depth = 0; depth < 64 && !string.IsNullOrEmpty(current); depth++)
        {
            yield return current;

            string? parent;

            try
            {
                parent = Path.GetDirectoryName(current);
            }
            catch (Exception)
            {
                yield break;
            }

            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent;
        }
    }

    /// <summary>
    /// 请求同步引擎回收这个文件的本地空间（"仅联机"）。
    ///
    /// 取代旧版的 <c>cmd.exe /c attrib +U -P</c> —— 那要为每个文件拉起一个 cmd 子进程，
    /// 而且 attrib 的失败信息完全拿不到。
    ///
    /// 注意这只是一个<b>请求</b>：OneDrive 不会脱水尚未上传完成的文件，
    /// 所以先设置、再等它生效是正确顺序，也正因如此"脱水成功"本身就证明了上传完成。
    /// </summary>
    /// <returns>true 表示属性已成功写入（不代表已经脱水）。</returns>
    public static bool RequestRelease(string path, out string? error)
    {
        error = null;

        uint current = NativeMethods.GetFileAttributes(path);

        if (current == NativeMethods.InvalidFileAttributes)
        {
            error = Win32Message();
            return false;
        }

        // 必须先按可写掩码过滤：直接把读到的属性原样回写，里面的 RECALL_ON_DATA_ACCESS
        // 之类只读位会让整个调用失败，于是"释放空间"静默无效。
        uint target = (current & FileAttributeFlags.SettableMask & ~FileAttributeFlags.Pinned)
                      | FileAttributeFlags.Unpinned;

        if (target == 0)
        {
            target = FileAttributeFlags.Normal;
        }

        if (target == (current & FileAttributeFlags.SettableMask))
        {
            return true;    // 已经是"仅联机"了，不用再写
        }

        if (NativeMethods.SetFileAttributes(path, target))
        {
            return true;
        }

        error = Win32Message();
        return false;
    }

    /// <summary>
    /// 取消"仅联机"，要求始终保留在本地。
    /// 界面上如果提供"取消释放"就用它；也用于把误设的状态改回来。
    /// </summary>
    public static bool RequestPin(string path, out string? error)
    {
        error = null;

        uint current = NativeMethods.GetFileAttributes(path);

        if (current == NativeMethods.InvalidFileAttributes)
        {
            error = Win32Message();
            return false;
        }

        uint target = (current & FileAttributeFlags.SettableMask & ~FileAttributeFlags.Unpinned)
                      | FileAttributeFlags.Pinned;

        if (NativeMethods.SetFileAttributes(path, target))
        {
            return true;
        }

        error = Win32Message();
        return false;
    }

    // ==================================================================
    //  内部
    // ==================================================================

    private static (bool Queried, uint State) QueryPlaceholderState(string path, bool isDirectory)
    {
        if (Volatile.Read(ref _cloudApiUsable) == 0)
        {
            return (false, PlaceholderState.NoStates);
        }

        // 先走目录项读取。实测这条路径能读到真实的重解析标记，
        // 而句柄版在同一进程、同一路径下会把它抹掉（详见 NativeMethods 里的说明）。
        (bool dirEntryQueried, uint dirEntryState) = QueryViaDirectoryEntry(path);

        if (dirEntryQueried && dirEntryState != PlaceholderState.NoStates)
        {
            return (true, dirEntryState);
        }

        uint flags = NativeMethods.FileFlagOpenNoRecall;

        if (isDirectory)
        {
            flags |= NativeMethods.FileFlagBackupSemantics;
        }

        try
        {
            // 只申请 FILE_READ_ATTRIBUTES，再加 FILE_FLAG_OPEN_NO_RECALL：
            // 这样打开一个已脱水的占位符<b>不会</b>触发回传下载 ——
            // 否则"检查一下状态"这个动作本身就会把刚释放的空间又占回来。
            using SafeFileHandle handle = NativeMethods.CreateFile(
                path,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareAll,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                flags,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                // 目录项那一步已经拿到过答案就别否认它（比如文件正被独占打开）。
                return (dirEntryQueried, PlaceholderState.NoStates);
            }

            if (!NativeMethods.GetFileInformationByHandleEx(
                    handle,
                    NativeMethods.FileAttributeTagInfoClass,
                    out NativeMethods.FileAttributeTagInfo info,
                    (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
            {
                return (dirEntryQueried, PlaceholderState.NoStates);
            }

            uint state = NativeMethods.CfGetPlaceholderStateFromFileInfo(
                ref info,
                NativeMethods.FileAttributeTagInfoClass);

            Volatile.Write(ref _cloudApiUsable, 1);

            return state == PlaceholderState.Invalid
                ? (false, PlaceholderState.NoStates)
                : (true, state);
        }
        catch (DllNotFoundException)
        {
            // Windows 10 1709 之前没有 cldapi.dll。记下来，之后不再尝试。
            Volatile.Write(ref _cloudApiUsable, 0);
            return (false, PlaceholderState.NoStates);
        }
        catch (EntryPointNotFoundException)
        {
            Volatile.Write(ref _cloudApiUsable, 0);
            return (false, PlaceholderState.NoStates);
        }
        catch (Exception)
        {
            return (false, PlaceholderState.NoStates);
        }
    }

    /// <summary>
    /// 用 <c>FindFirstFileW</c> 读目录项里的属性与重解析标记，再交给 cldapi 解释。
    ///
    /// 这条路径存在的原因是一个实测差异：同一进程、同一个 OneDrive 同步根目录，
    /// <c>GetFileAttributesW</c> 与句柄版 <c>FileAttributeTagInfo</c> 给出
    /// <c>attr=0x00000031 tag=0</c>（少了 FILE_ATTRIBUTE_REPARSE_POINT），
    /// 而 <c>FindFirstFileW</c> 给出 <c>attr=0x00000431 tag=0x9000701A</c>。
    /// 少了那两位，<c>CfGetPlaceholderStateFromFileInfo</c> 只能回答 NO_STATES，
    /// 于是同步根目录被误判成"不在同步范围内"。
    ///
    /// 只读元数据，不会触发占位符回传。
    /// </summary>
    private static (bool Queried, uint State) QueryViaDirectoryEntry(string path)
    {
        // FindFirstFileW 不接受结尾的反斜杠，也无法用于卷根（"E:\"）。
        string trimmed = path.Length > 3 ? path.TrimEnd('\\', '/') : path;

        if (trimmed.Length < 4 || trimmed.EndsWith(':'))
        {
            return (false, PlaceholderState.NoStates);
        }

        byte[] buffer = new byte[NativeMethods.FindDataSize];
        IntPtr find = NativeMethods.InvalidHandleValue;

        try
        {
            find = NativeMethods.FindFirstFile(trimmed, ref buffer[0]);

            if (find == NativeMethods.InvalidHandleValue)
            {
                return (false, PlaceholderState.NoStates);
            }

            NativeMethods.FileAttributeTagInfo info = new()
            {
                FileAttributes = BitConverter.ToUInt32(buffer, NativeMethods.FindDataAttributesOffset),
                ReparseTag = BitConverter.ToUInt32(buffer, NativeMethods.FindDataReparseTagOffset),
            };

            uint state = NativeMethods.CfGetPlaceholderStateFromFileInfo(
                ref info,
                NativeMethods.FileAttributeTagInfoClass);

            Volatile.Write(ref _cloudApiUsable, 1);

            return state == PlaceholderState.Invalid
                ? (false, PlaceholderState.NoStates)
                : (true, state);
        }
        catch (DllNotFoundException)
        {
            Volatile.Write(ref _cloudApiUsable, 0);
            return (false, PlaceholderState.NoStates);
        }
        catch (EntryPointNotFoundException)
        {
            Volatile.Write(ref _cloudApiUsable, 0);
            return (false, PlaceholderState.NoStates);
        }
        catch (Exception)
        {
            return (false, PlaceholderState.NoStates);
        }
        finally
        {
            if (find != NativeMethods.InvalidHandleValue)
            {
                NativeMethods.FindClose(find);
            }
        }
    }

    private static long LogicalSize(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 卷上实际占用的字节数。脱水后接近 0，而逻辑大小不变 ——
    /// 这是"本地空间确实被释放了"最直接的证据。
    /// </summary>
    private static long PhysicalSize(string path)
    {
        uint low = NativeMethods.GetCompressedFileSize(path, out uint high);

        // 低 32 位恰好是 0xFFFFFFFF 也可能是合法值，必须靠 GetLastError 区分。
        if (low == NativeMethods.InvalidFileSize && Marshal.GetLastWin32Error() != NativeMethods.NoError)
        {
            return LogicalSize(path);   // 查不到就按"没释放"处理，宁可保守。
        }

        return ((long)high << 32) | low;
    }

    private static string Win32Message()
    {
        try
        {
            return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception)
        {
            return "未知错误";
        }
    }
}
