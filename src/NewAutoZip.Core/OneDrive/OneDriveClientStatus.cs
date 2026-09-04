using System.Diagnostics;

namespace NewAutoZip.Core.OneDrive;

/// <summary>OneDrive 客户端进程的存在性判断结果。</summary>
public enum OneDriveClientState
{
    /// <summary>没找到进程 —— 归档移进去也不会被上传。</summary>
    NotRunning = 0,

    /// <summary>进程在跑。</summary>
    Running = 1,

    /// <summary>查不出来（权限不足、WMI/进程枚举被拦）。不能据此断言好坏。</summary>
    Unknown = 2,
}

/// <summary>
/// OneDrive 客户端是否在运行 —— 通知正文里【开始】那一行的来源。
///
/// 旧版 <c>OneDriveHelper.IsRunning()</c> 也做这件事，但把"查不出来"和"没在跑"混为一谈，
/// 而进程枚举在受限账户下是真的会抛异常的。这里分成三态：<b>查不出来就说查不出来</b>，
/// 不替用户下结论。<see cref="Describe"/> 出来的文字直接进通知，所以每一种状态都得是人话。
/// </summary>
public static class OneDriveClientStatus
{
    /// <summary>进程名（不含 .exe）。OneDrive 一直叫这个，个人版与商业版同一个可执行文件。</summary>
    private const string ProcessName = "OneDrive";

    public static OneDriveClientState Query()
    {
        try
        {
            Process[] found = Process.GetProcessesByName(ProcessName);

            try
            {
                return found.Length > 0 ? OneDriveClientState.Running : OneDriveClientState.NotRunning;
            }
            finally
            {
                // Process 是 IDisposable，一次枚举拿到的句柄必须还回去。
                // 旧版每次上传等待要查几万次，句柄泄漏在那种频率下是会撑爆的。
                foreach (Process p in found)
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception)
        {
            return OneDriveClientState.Unknown;
        }
    }

    /// <summary>通知与日志里的那一行：<c>OneDrive：客户端运行正常。</c></summary>
    public static string Describe(OneDriveClientState state) => state switch
    {
        OneDriveClientState.Running => "OneDrive：客户端运行正常。",
        OneDriveClientState.NotRunning =>
            "OneDrive：客户端未在运行，压缩包移进目录后不会被上传，本地空间也不会释放。",
        _ => "OneDrive：无法确认客户端状态（进程查询被拒绝）。",
    };

    /// <summary>查一次并直接给出那行文字。</summary>
    public static string DescribeNow() => Describe(Query());
}
