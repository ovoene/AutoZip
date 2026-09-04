using System.Runtime.CompilerServices;

using NewAutoZip.Core.Configuration;

namespace NewAutoZip.Tests;

/// <summary>
/// 测试进程级初始化。
///
/// AppPaths.DataRoot 是 Lazy 且进程内只解析一次，所以必须在任何测试触碰它<b>之前</b>
/// 把 AUTOZIP_DATA 指向临时目录 —— 否则测试会往程序目录（或退让后的
/// %LOCALAPPDATA%\AutoZip\）里写状态文件和日志，污染用户的实际安装。
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"{AppPaths.ProductName}-tests-{Environment.ProcessId}");

        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, root);
    }
}
