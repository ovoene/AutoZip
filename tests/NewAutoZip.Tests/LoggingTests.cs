using NewAutoZip.Core.Diagnostics;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 日志文件的大小上限与轮转。
///
/// 旧版的 backup_zip.log 只增不减，常驻跑几个月能长到几百 MB，用记事本根本打不开 ——
/// 出了事最需要翻的东西反而看不了。这里钉住三件事：上限就是 10 MB、
/// 写满会开新文件而历史还在、历史文件个数有硬上限（含上一版留下的多余编号）。
/// </summary>
public class RollingFileLoggerTests
{
    /// <summary>测试用的小上限。构造函数的下限就是 64 KB，比这更小会被抬回来，测了也没意义。</summary>
    private const long SmallCap = 64 * 1024;

    /// <summary>测试里统一留两个历史文件，和产品默认值一致。</summary>
    private const int Archives = 2;

    [Fact]
    public void 默认上限就是要求里的10MB()
    {
        // 这条是「把要求钉在代码里」：谁改了这两个数，测试立刻红，而不是等用户发现日志涨到几百 MB。
        Assert.Equal(10L * 1024 * 1024, RollingFileLogger.DefaultMaxBytesPerFile);
        Assert.Equal(2, RollingFileLogger.DefaultMaxArchives);
    }

    [Fact]
    public void 写满上限后开新文件且历史内容还在()
    {
        using TempDir dir = new("nazlog");

        WriteUntilFull(dir.Path, "第一批");
        Assert.True(File.Exists(dir.File("app.log")), "第一轮就该有 app.log");

        // 第二个 logger 面对的是上一轮留下的满文件 —— 顺带覆盖「重启后接着写」。
        WriteOnce(dir.Path, "第二批");

        string archived = dir.File("app.1.log");

        Assert.True(File.Exists(archived), "写满之后应当轮转出 app.1.log");
        Assert.Contains("第一批", File.ReadAllText(archived), StringComparison.Ordinal);
        Assert.Contains("第二批", File.ReadAllText(dir.File("app.log")), StringComparison.Ordinal);

        // 轮转之后当前文件是新开的，不该还带着上一轮那一堆内容。
        Assert.True(
            new FileInfo(dir.File("app.log")).Length < SmallCap,
            "轮转后的 app.log 应当远小于上限");
    }

    [Fact]
    public void 历史文件个数有硬上限()
    {
        using TempDir dir = new("nazlog");

        // 连着写满五轮，每轮至少触发一次轮转。
        for (int round = 0; round < 5; round++)
        {
            WriteUntilFull(dir.Path, $"第{round}批");
        }

        Assert.True(File.Exists(dir.File("app.log")));
        Assert.True(File.Exists(dir.File("app.1.log")));
        Assert.True(File.Exists(dir.File("app.2.log")));
        Assert.False(File.Exists(dir.File("app.3.log")), "历史文件只留两个，不该出现 app.3.log");

        // 当前 + 历史 = 3 个文件，占盘上限就是 3 × 单文件上限。
        Assert.Equal(Archives + 1, dir.Files("app*.log").Length);
    }

    [Fact]
    public void 上一版留下的多余历史文件会被清掉()
    {
        using TempDir dir = new("nazlog");

        // 上一版留 5 个历史文件，升级后只留 2 个。多出来的那几个没人碰，
        // 不主动清就会永远占着盘 —— 占盘上限也就成了一句空话。
        File.WriteAllText(dir.File("app.3.log"), "上一版留下的");
        File.WriteAllText(dir.File("app.5.log"), "上一版留下的");

        WriteUntilFull(dir.Path, "第一批");
        WriteOnce(dir.Path, "第二批");

        Assert.False(File.Exists(dir.File("app.3.log")));
        Assert.False(File.Exists(dir.File("app.5.log")));

        // 这里只能钉上界，不能钉等号：后台泵把 1200 行切成几批是不定的，
        // 切两批就多轮转一次，最终是 2 个文件还是 3 个由时序决定。
        // 「恰好 3 个」由上面那条五轮的测试负责，这条要证的是「多余编号不会赖着不走」。
        Assert.True(
            dir.Files("app*.log").Length <= Archives + 1,
            $"日志文件个数应当不超过 {Archives + 1}，实际 {dir.Files("app*.log").Length} 个");
    }

    [Fact]
    public void 密码不落盘()
    {
        using TempDir dir = new("nazlog");

        using (RollingFileLogger log = New(dir.Path))
        {
            log.Info("7za a -mhe=on -phunter2 Backup.7z @list.txt");
            log.Info("PASSWORD=hunter2 已应用");
        }

        string text = File.ReadAllText(dir.File("app.log"));

        // 旧版把 PASSWORD=xxx 明文写进日志，而日志和压缩包放在同一台机器上，等于加密白做了。
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);

        // 只断言「没有明文」是不够的：脱敏整段吞掉这行，同样能过。
        // 所以再钉一次「该留的还在」—— 排查问题时那条命令行本身是有用的。
        Assert.Contains("-p" + SecretRedactor.Mask, text, StringComparison.Ordinal);
        Assert.Contains("PASSWORD=" + SecretRedactor.Mask, text, StringComparison.Ordinal);
    }

    private static RollingFileLogger New(string directory) => new(
        directory,
        SecretRedactor.Shared,
        maxBytesPerFile: SmallCap,
        maxArchives: Archives);

    /// <summary>写到稳稳超过上限，然后关掉 logger —— Dispose 会把队列刷干，落盘是确定的。</summary>
    private static void WriteUntilFull(string directory, string tag)
    {
        using RollingFileLogger log = New(directory);

        // 每行 130 字节上下，1200 行约 150 KB，稳稳超过 64 KB 又不到单批 256 KB 的切分线。
        for (int i = 0; i < 1200; i++)
        {
            log.Info($"{tag} 第 {i} 行：这一行只是用来把文件写满的填充内容，长度大致是一条真实日志的量级。");
        }
    }

    /// <summary>只写一行。用来在「文件已经满了」的前提下触发一次轮转判断。</summary>
    private static void WriteOnce(string directory, string tag)
    {
        using RollingFileLogger log = New(directory);
        log.Info($"{tag}：只写一行，用来触发轮转判断。");
    }
}
