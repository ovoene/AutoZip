namespace NewAutoZip.Core.Configuration;

/// <summary>
/// 运行时路径解析。
///
/// 旧版把 checkpoint.txt / webhook.txt / backup_zip.log / ZipTemp 都写在 exe 同目录，
/// 而 ZipTemp 甚至是相对路径（依赖当前工作目录）。装到 Program Files 下会直接因无写权限抛异常，
/// 而 Checkpoint.Save 抛异常又恰好跳过了状态复位 —— 于是演变成无限重打包。
///
/// 新版<b>默认就写在 exe 同目录</b>——拷走整个文件夹就是完整搬迁，不用去用户配置目录里翻。
/// 但不再假设那里一定可写：启动时做一次真实写入探测，
/// 装在 Program Files 之类只读位置时自动退到 %LOCALAPPDATA%\AutoZip\，
/// 并把退让原因记进 <see cref="DataRootFallbackReason"/>，由启动校验如实提醒，绝不静默换地方。
/// 环境变量 AUTOZIP_DATA 优先级最高（测试与自定义部署用）。
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// 程序名。界面上凡是要显示程序名的地方都取这里 —— 标题栏、托盘提示、
    /// 关于、错误弹窗标题、通知里的 source 字段。散落着抄的字符串迟早改漏一处。
    /// </summary>
    public const string ProductName = "AutoZip";

    public const string ProductFolderName = ProductName;
    public const string PortableMarkerFileName = "portable.txt";
    public const string DataRootEnvironmentVariable = "AUTOZIP_DATA";

    /// <summary>
    /// 改名前的环境变量名。老部署脚本里可能还写着它，继续认，但不再往外宣传。
    /// </summary>
    public const string LegacyDataRootEnvironmentVariable = "NEWAUTOZIP_DATA";

    /// <summary>
    /// 版本号，取自程序集元数据（<c>Directory.Build.props</c> 的 <c>Version</c>）。
    /// 手抄的常量迟早和真实版本对不上，而用户报问题时第一句要问的就是版本。
    /// </summary>
    public static string ProductVersion { get; } =
        typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly Lazy<string> LazyDataRoot = new(ResolveDataRoot, isThreadSafe: true);
    private static readonly Lazy<string> LazySevenZip = new(ResolveSevenZip, isThreadSafe: true);

    /// <summary>exe 所在目录。单文件发布下 .NET 6+ 保证返回 exe 目录而非解包临时目录。</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// 便携标记文件。数据本来就默认放在 exe 同目录，所以这个标记只剩一个作用：
    /// 即使写入探测失败也强行留在 exe 同目录（U 盘、网络盘上偶发探测失败时用）。
    /// </summary>
    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName));

    /// <summary>
    /// 数据目录没能落在程序目录时的原因；一切正常时为 <c>null</c>。
    /// </summary>
    public static string? DataRootFallbackReason
    {
        get
        {
            _ = DataRoot;       // 先触发解析，原因是在解析过程中记下的
            return FallbackReason;
        }
    }

    private static string? FallbackReason;

    public static string DataRoot => LazyDataRoot.Value;

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static string StateFile => Path.Combine(DataRoot, "state.json");

    public static string LogDirectory => Path.Combine(DataRoot, "logs");

    public static string DefaultZipTemp => Path.Combine(DataRoot, "ZipTemp");

    /// <summary>随程序携带的 7-Zip 控制台版。</summary>
    public static string SevenZipExe => LazySevenZip.Value;

    /// <summary>期望的 7za.exe 位置，用于"找不到"时给出准确提示。</summary>
    public static string PreferredSevenZipExe => Path.Combine(BaseDirectory, "tools", "7za.exe");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolveDataRoot()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(fromEnvironment))
        {
            fromEnvironment = Environment.GetEnvironmentVariable(LegacyDataRootEnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        if (IsPortable)
        {
            return BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }

        // 默认就是 exe 同目录 —— 全部数据都在程序当前目录生成。
        string beside = BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        if (IsWritable(beside, out string? why))
        {
            return beside;
        }

        // 只读位置（Program Files、只读介质、被组策略锁住）不能硬写：
        // 旧版就是在这里抛异常，而抛异常又恰好跳过状态复位，演变成无限重打包。
        FallbackReason = $"程序所在目录不可写（{why}），数据已改放到 %LOCALAPPDATA%。"
                         + "想让数据留在程序目录，请把程序移到有写权限的位置。";

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        // 极端情况下（服务账户、精简环境）LocalApplicationData 可能为空，退回临时目录而不是崩溃。
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, ProductFolderName);
    }

    /// <summary>
    /// 真的写一个文件试试。只看 ACL 或只看目录属性都会漏掉写入虚拟化、只读介质、组策略这几种情况。
    /// </summary>
    private static bool IsWritable(string directory, out string? reason)
    {
        reason = null;

        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");

            File.WriteAllText(probe, "probe");
            File.Delete(probe);

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static string ResolveSevenZip()
    {
        string[] candidates =
        [
            PreferredSevenZipExe,
            Path.Combine(BaseDirectory, "7za.exe"),
            Path.Combine(BaseDirectory, "7z.exe"),

            // 开发机兜底：装了完整版 7-Zip 时也能直接跑。
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 路径非法则跳过
            }
        }

        // 一个都没找到：返回首选路径，让错误信息指向"应该放在哪里"。
        return PreferredSevenZipExe;
    }
}
