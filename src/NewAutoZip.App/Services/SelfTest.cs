using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NewAutoZip.App.ViewModels;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Packing;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Watching;

namespace NewAutoZip.App.Services;

/// <summary>
/// 界面自检（<c>AutoZip.exe --selftest</c>）。
///
/// XAML 里的绑定写错了不会让编译失败 —— 它只会在运行时静静地把那一处显示成空白，
/// 而错误信息只出现在调试输出窗口里。也就是说：<b>“编译通过”完全不代表界面是对的。</b>
///
/// 这个自检把绑定错误变成一个可以被脚本检查的东西：挂上 WPF 的绑定诊断源，
/// 把六个页面逐个显示并强制布局，往每个列表里塞入样例行（<b>空列表永远不会去求值
/// 单元格里的绑定，所以只看空界面等于什么都没测</b>），最后把捕获到的错误写进报告文件，
/// 有错就以非零退出码结束。
///
/// 窗口会被放到屏幕外并且不进任务栏，跑完自己关掉。
/// </summary>
internal static class SelfTest
{
    private const double TestWidth = 1280;
    private const double TestHeight = 860;

    /// <summary>
    /// 判定"两段文字叠在一起"的容差（像素）。
    ///
    /// 相邻控件的边框在亚像素布局下常有零点几像素的交叠，那不是缺陷；
    /// 真正的重叠（一段文字压在另一段上）在两个方向上都远超这个值。
    /// </summary>
    private const double OverlapSlack = 2.0;

    /// <summary>表格至少要留出的高度：表头 + 一行数据。低于这个值就等于没显示。</summary>
    private const double MinGridHeight = 80.0;

    /// <summary>日志列表至少要留出的高度。日志是"一眼看一片"的东西，几行不够用。</summary>
    private const double MinLogHeight = 200.0;

    /// <summary>「正在进行」徽章上的字。自检要在可视树里按这个字找它，所以不能只写在 XAML 里。</summary>
    private const string BadgeText = "正在进行";

    /// <summary>徽章文字压在徽章底色上的最低对比度。WCAG 2.1 正文 AA 就是这个数。</summary>
    private const double MinBadgeContrast = 4.5;

    /// <summary>
    /// 徽章底色与"它背后那块面板"的最小 RGB 距离。
    ///
    /// 这里刻意<b>不用</b> WCAG 对比度：那个公式只看亮度、完全忽略色相，
    /// 而深色主题下的紫罗兰徽章压在深灰卡片上亮度差本来就不大（约 1.5:1），
    /// 但肉眼一看就是个紫色药丸 —— 用亮度去判"能不能区分"会把对的判成错的。
    /// 欧氏 RGB 距离能吃到色相差，48 大约是对角线（441）的 11%，
    /// 是"明显不同色但不刺眼"的下限。
    /// </summary>
    private const double MinBadgeSeparation = 48.0;

    /// <summary>
    /// 徽章底色和强调色"几乎一模一样"的判定线。
    ///
    /// 强调色是用户自己挑的，所以这里不能用上面那条 48 去硬卡 ——
    /// 挑了紫色系强调色的人会莫名其妙构建失败，那是在惩罚错误的对象。
    /// 只有近到肉眼分不出（徽章直接融进选中态和主按钮）才算缺陷。
    /// </summary>
    private const double AccentClashDistance = 24.0;

    /// <summary>选中行上的字压在选中底色上的最低对比度。和徽章同一个标准（WCAG AA 正文）。</summary>
    private const double MinListTextContrast = 4.5;

    /// <summary>
    /// 选中底色与"背后那层面板"、以及与悬停底色之间的最小 RGB 距离。
    ///
    /// 用距离而不是亮度对比度，理由和 <see cref="MinBadgeSeparation"/> 一样：
    /// 这两组颜色是同一色相上的邻居，亮度差本来就该小，用 WCAG 那个公式判
    /// "能不能区分"会把对的判成错的。24 大约是对角线（441）的 5%，
    /// 是"看得出这一行被选中了"的下限 —— 再近就只剩若有若无的一层灰。
    /// </summary>
    private const double MinListSeparation = 24.0;

    /// <summary>
    /// 悬停底色与背后面板的最小距离，比上面那条松。
    ///
    /// 悬停本来就该是"淡淡地亮一下"，浅色主题下更是只有一点余量可用
    /// （白底往上没得走，只能往下压一档），按选中态那条 24 去要求它，
    /// 会逼出一个比选中态还重的悬停色 —— 那是本末倒置。
    /// </summary>
    private const double MinListHoverSeparation = 16.0;

    /// <summary>
    /// 选中态描边压在选中底色上的最低对比度。
    ///
    /// 描边是"焦点在不在这儿"唯一的区别（底色两态相同，见
    /// <see cref="CheckListRowColors"/>），所以它必须真的看得见；
    /// 但它是装饰不是正文，2.0 够了，按 4.5 要求会把描边逼成一条刺眼的亮线。
    /// </summary>
    private const double MinListBorderContrast = 2.0;

    /// <summary>
    /// 普通行（没选中、没悬停）底色与背景的最小距离。刻意比悬停那条还松一半。
    ///
    /// 需求要的是"固定一个淡雅好区分的颜色"——"好区分"这半句要求它别和背景糊在一起
    /// （一行看不出是一行），8 就是这个下限：肉眼刚好认得出有一层底。
    /// </summary>
    private const double MinListRowSeparation = 8.0;

    /// <summary>
    /// 普通行底色与背景的<b>最大</b>距离。整份自检里唯一一条上限。
    ///
    /// "淡雅"这个词只有这半句能变成数字：普通行是界面上<b>数量最多</b>的那层颜色
    /// （左边功能区五项 + 日志区几百行），它一重，整个窗口就被它带跑，
    /// 用户抱怨的"太不协调"就是这么来的。40 大约是对角线的 9%——
    /// 挪得出一层底，又远不到抢眼的程度；显眼的名额留给选中的那一行
    /// （它要求离背景至少 24，实测深浅两套都在 85 以上，档次拉得很开）。
    ///
    /// 这条同时是"背景色不要和按钮一个色"的算术版：主按钮用的强调色离背景动辄两三百，
    /// 普通行只要再继承到它，这里立刻就红 —— 不用等用户截图。
    /// </summary>
    private const double MaxListRowSeparation = 40.0;

    /// <summary>
    /// WPF-UI 摊在应用资源里的强调色。挨个试，谁在就用谁 —— 键名跨版本会变，
    /// 一个都找不到时这条对照就只是不做，不该因此报缺陷。
    /// </summary>
    private static readonly string[] AccentResourceKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorPrimary",
        "SystemAccentColorSecondary",
        "SystemAccentColorTertiary",
        "AccentFillColorDefaultBrush",
    ];

    /// <summary>
    /// 主按钮底色用的那只强调色画刷。<see cref="CheckAccentApplied"/> 拿它当"应该是什么颜色"的基准。
    /// 键名跨版本会变，取不到时那条核对整条跳过而不是报缺陷。
    /// </summary>
    private const string AccentFillKey = "AccentFillColorDefaultBrush";

    internal static string ReportPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");

    /// <summary>返回进程退出码：0 = 没有绑定错误。</summary>
    internal static int Run(IAppLogger log)
    {
        BindingTraceListener listener = new();
        Attach(listener);

        List<string> steps = [];
        int exitCode;

        try
        {
            exitCode = Exercise(listener, steps, log);
        }
        catch (Exception ex)
        {
            steps.Add($"自检本身抛出异常：{ex}");
            exitCode = 2;
        }
        finally
        {
            Detach(listener);
        }

        WriteReport(listener, steps, exitCode, log);
        return exitCode;
    }

    // ==================================================================
    //  实际走一遍界面
    // ==================================================================

    private static int Exercise(BindingTraceListener listener, List<string> steps, IAppLogger log)
    {
        UiLogSink sink = new();
        IAppLogger logger = new CompositeLogger(log, sink);

        EngineHost host = new(logger);

        ThemeService theme = new(logger);
        theme.Apply(host.Settings);

        MainViewModel model = new(host, sink, logger, theme);

        Views.MainWindow window = new(model)
        {
            // 挪到屏幕外，不进任务栏 —— 自检不该在用户脸上闪一个窗口。
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Width = TestWidth,
            Height = TestHeight,
            Left = -30000,
            Top = -30000,
        };

        int defects = 0;

        try
        {
            window.Show();
            Settle(window);
            steps.Add($"主窗口已构建并布局（{TestWidth}×{TestHeight}）。");

            defects += CheckPalette(steps);

            // 六个页面逐个过。折叠的元素不参与布局，不切过去就等于没测。
            for (int page = 0; page < 6; page++)
            {
                ShowPage(model, window, page, withSamples: false);
                steps.Add($"页面 {page} 已显示并布局，累计错误 {listener.ErrorCount}。");
            }

            // 定时器会在下一轮把注入的样例行按空快照同步掉，所以先停掉它。
            model.Dispose();

            InjectSamples(model);
            steps.Add("已注入样例行（文件 / 待上传 / 重试 / 隔离 / 日志）。");

            // 有数据的页面再过一遍 —— 这一轮才真正求值单元格与条目模板里的绑定。
            for (int page = 0; page < 6; page++)
            {
                ShowPage(model, window, page, withSamples: true);
                steps.Add($"页面 {page}（有数据）已布局，累计错误 {listener.ErrorCount}。");
            }

            ExerciseSettings(model, window, steps, listener);

            defects += ExerciseThemes(model, theme, window, steps, listener);
            defects += CheckPipelineSteps(model, theme, window, steps);
            defects += CheckCloudTargetSwitch(model, window, steps);
            defects += CheckCloudQuotaCard(model, window, steps);
            defects += ExerciseBackground(model, theme, window, steps, listener);
            defects += CheckAppearanceNotDirty(steps);
            defects += CheckLayout(model, window, steps);
            defects += CheckTitleBar(model, window, steps);
            defects += CheckTrayMenu(window, steps);
            defects += CheckLogOrder(model, window, steps);

            steps.Add($"自检结束，绑定错误 {listener.ErrorCount} 条，警告 {listener.WarningCount} 条，" +
                      $"主题缺陷 {defects} 项。");

            return listener.ErrorCount == 0 && defects == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                model.Dispose();
                theme.Dispose();
                window.CloseForSelfTest();

                // 引擎从未启动，这里应当立刻返回；仍然设个上限，自检不该有挂住的可能。
                host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                steps.Add($"关闭自检窗口时出错（不影响结论）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 两个主题都走一遍，外加一个自定义强调色。
    ///
    /// 只在深色下自检等于只测了一半：浅色主题下"浅灰字落在白底上"这种问题
    /// 编译期看不出来、深色下也看不出来，只有真的切过去布局一次才会暴露。
    ///
    /// 走的是 <see cref="ThemeService"/> 而不是设置页的绑定属性 —— 后者会触发
    /// "外观即时落盘"，自检是个诊断命令，不该把用户的主题改掉。
    /// </summary>
    private static int ExerciseThemes(
        MainViewModel model,
        ThemeService theme,
        Views.MainWindow window,
        List<string> steps,
        BindingTraceListener listener)
    {
        (string Label, Core.Configuration.ThemeMode Mode, string Accent)[] combos =
        [
            ("浅色 + 系统强调色", Core.Configuration.ThemeMode.Light, ""),
            ("浅色 + 自定义强调色", Core.Configuration.ThemeMode.Light, "#C4314B"),
            ("深色 + 自定义强调色", Core.Configuration.ThemeMode.Dark, "#2E9E6B"),
            ("深色 + 系统强调色", Core.Configuration.ThemeMode.Dark, ""),
        ];

        int defects = 0;

        // 强调色那条单独走：它要连着换几次同主题下的强调色才量得出"慢一步"，
        // 而下面这几组每次连主题模式一起换，量不到。
        defects += CheckAccentApplied(model, theme, window, steps);

        foreach ((string label, Core.Configuration.ThemeMode mode, string accent) in combos)
        {
            theme.Apply(mode, accent);

            // 每个主题都要把六个页面重新布局一遍：切主题会换掉整份控件字典，
            // 只在当前页停留的话另外四页的模板一次都不会重新走。
            Relayout(model, window);

            defects += CheckPaletteValues(theme, label, steps);
            defects += CheckListRowColors(window, theme, label, steps);
            defects += CheckRenderedNotStale(window, theme, label, steps);

            steps.Add($"主题「{label}」六个页面已重新布局，累计错误 {listener.ErrorCount}。");
        }

        return defects;
    }

    /// <summary>
    /// 背景图这条路走一遍：真图、缺失的图、以及模糊/不透明度的两个极值。
    ///
    /// 四件事是编译期看不出来的：
    /// <list type="number">
    ///   <item>那层 <c>Image</c> 上的绑定（Source / Opacity）没有背景图时
    ///         根本不会被求值 —— 不主动塞一张进去等于没测；</item>
    ///   <item>图不在了必须变成一句人话（<c>BackgroundProblem</c>），不能崩、
    ///         也不能静默 —— 用户删掉那张图之后程序还得能起来；</item>
    ///   <item>模糊半径收到 0 和 60 都得照样能烘出图、照样能布局；</item>
    ///   <item>模糊必须是<b>烘进位图</b>的，界面上不能挂实时效果，
    ///         而且只动模糊/不透明度时不能整套重新应用主题 ——
    ///         这两条是"用了背景图之后整个程序极为卡顿"的根因，见 <see cref="CheckBakedBlur"/>。</item>
    /// </list>
    ///
    /// 结束时把外观还原成进来时的样子：自检是诊断命令，不该有副作用。
    /// </summary>
    private static int ExerciseBackground(
        MainViewModel model,
        ThemeService theme,
        Views.MainWindow window,
        List<string> steps,
        BindingTraceListener listener)
    {
        Core.Configuration.AppearanceSpec original = theme.Appearance;
        List<string> bad = [];
        string? png = null;

        try
        {
            png = WriteProbePng();

            // ---- 真图 + 两个极值 ----
            (string Label, int Blur, int Opacity)[] combos =
            [
                ("模糊 0 / 不透明度 5", Core.Configuration.SettingsLimits.BackgroundBlurMin,
                    Core.Configuration.SettingsLimits.BackgroundOpacityMin),
                ("模糊 60 / 不透明度 100", Core.Configuration.SettingsLimits.BackgroundBlurMax,
                    Core.Configuration.SettingsLimits.BackgroundOpacityMax),
                ("模糊 18 / 不透明度 30", 18, 30),
            ];

            foreach ((string label, int blur, int opacity) in combos)
            {
                theme.Apply(original with
                {
                    BackgroundImagePath = png,
                    BackgroundBlur = blur,
                    BackgroundOpacity = opacity,
                });

                if (theme.Background is null)
                {
                    bad.Add($"背景图「{label}」没有载入成功：{theme.BackgroundProblem ?? "没有给出原因"}");
                }

                if (theme.BackgroundProblem is not null)
                {
                    bad.Add($"背景图「{label}」载入成功却仍带着问题描述：{theme.BackgroundProblem}");
                }

                Relayout(model, window);
                steps.Add($"背景图「{label}」六个页面已重新布局，累计错误 {listener.ErrorCount}。");
            }

            // ---- 模糊必须是烘进位图的，且拖滑块不能触发整套主题重应用 ----
            bad.AddRange(CheckBakedBlur(model, theme, window, png, original, steps));

            // ---- 图不在了 ----
            string missing = Path.Combine(Path.GetTempPath(), $"NewAutoZip-不存在的图-{Guid.NewGuid():N}.png");

            theme.Apply(original with { BackgroundImagePath = missing });

            if (theme.Background is not null)
            {
                bad.Add("路径指向一个不存在的文件，却报告载入成功了");
            }

            if (string.IsNullOrWhiteSpace(theme.BackgroundProblem))
            {
                bad.Add("背景图缺失时没有给出原因（设置页会静默地什么都不显示）");
            }

            Relayout(model, window);
            steps.Add($"背景图缺失时已降级并给出原因：{theme.BackgroundProblem}");

            // ---- 不用背景图 ----
            theme.Apply(original with { BackgroundImagePath = string.Empty });

            if (theme.Background is not null)
            {
                bad.Add("清空路径之后那张图还留在界面上");
            }

            Relayout(model, window);
            steps.Add("背景图已清除，窗口回到 Mica 材质。");
        }
        catch (Exception ex)
        {
            bad.Add($"走背景图这条路时抛了异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            try
            {
                theme.Apply(original);
                Relayout(model, window);
            }
            catch (Exception ex)
            {
                steps.Add($"还原外观时出错（不影响结论）：{ex.Message}");
            }

            if (png is not null)
            {
                try
                {
                    File.Delete(png);
                }
                catch (Exception ex)
                {
                    steps.Add($"删除自检用的背景图失败（不影响结论）：{ex.Message}");
                }
            }
        }

        if (bad.Count == 0)
        {
            steps.Add("背景图核对：真图、缺失、极值三种情况都正常。");
            return 0;
        }

        steps.Add($"背景图核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 钉住「用了背景图之后整个程序极为卡顿」的两个根因，防止哪天被改回去。
    ///
    /// 这两条都是<b>性能</b>约束，而性能改坏了不会报错、不会警告，界面看起来一模一样 ——
    /// 只有在软件渲染的机器上拖一下滑块才发现。所以只能在这里用可观察的事实钉死：
    ///
    /// <list type="number">
    ///   <item><b>界面层上不能有 <c>Effect</c>。</b>
    ///         挂一只 <c>BlurEffect</c> 等于让合成器每一帧把整窗底图重新卷积一遍。</item>
    ///   <item><b>半径变了才重烘，其余时候必须复用同一个位图实例。</b>
    ///         用引用相等判断 —— 这是"有没有重新渲染"唯一不含时间因素的证据。</item>
    ///   <item><b>只改模糊/不透明度时 <c>ThemeApplyCount</c> 不能动。</b>
    ///         之前每一次滑块变化都会换掉整份 WPF-UI 控件字典、重刷 26 个语义画刷、
    ///         重新解析六个页面里所有 <c>DynamicResource</c>，还顺带写一次盘。
    ///         滑块按鼠标移动的频率抬 <c>Value</c>，一秒几十次，队列根本排不完 ——
    ///         这也是"左边点导航也卡"的原因：卡的不是导航，是整个消息队列。</item>
    ///   <item><b>但真的换主题时那套活必须照旧干。</b>
    ///         少了这一条，上面三条用一句 <c>return</c> 就能全过 —— 门就空了。</item>
    /// </list>
    /// </summary>
    private static List<string> CheckBakedBlur(
        MainViewModel model,
        ThemeService theme,
        Views.MainWindow window,
        string png,
        Core.Configuration.AppearanceSpec original,
        List<string> steps)
    {
        List<string> bad = [];

        const int max = Core.Configuration.SettingsLimits.BackgroundBlurMax;

        Core.Configuration.AppearanceSpec baseSpec = original with
        {
            BackgroundImagePath = png,
            BackgroundOpacity = 60,
        };

        // ---- 1. 界面上那层不能挂实时效果 ----
        if (window.BackgroundLayer.Effect is not null)
        {
            bad.Add($"背景层挂着实时效果 {window.BackgroundLayer.Effect.GetType().Name}，"
                + "合成器会每一帧重算整窗底图，软件渲染下整个程序会卡到点一下导航都要等");
        }

        // ---- 2. 半径变了要重烘 ----
        int applies = theme.ThemeApplyCount;

        theme.Apply(baseSpec with { BackgroundBlur = 0 });
        System.Windows.Media.ImageSource? sharp = theme.Background;

        theme.Apply(baseSpec with { BackgroundBlur = max });
        System.Windows.Media.ImageSource? blurred = theme.Background;

        if (sharp is null || blurred is null)
        {
            bad.Add("对比模糊前后时背景图没有载入成功，这一项等于没测到");
            return bad;
        }

        if (ReferenceEquals(sharp, blurred))
        {
            bad.Add($"模糊半径从 0 调到 {max}，背景位图却还是同一个实例 —— 模糊没有真的烘进去");
        }

        if (theme.ThemeApplyCount != applies)
        {
            bad.Add($"只改了模糊半径却整套重新应用了主题"
                + $"（ThemeApplyCount {applies} → {theme.ThemeApplyCount}）");
        }

        // ---- 3. 半径没变要复用，不透明度不该引起任何重活 ----
        theme.Apply(baseSpec with { BackgroundBlur = max, BackgroundOpacity = 95 });

        if (!ReferenceEquals(blurred, theme.Background))
        {
            bad.Add("只改了不透明度，背景图却重烘了一张 —— 拖动滑块会一路重新渲染位图");
        }

        if (theme.ThemeApplyCount != applies)
        {
            bad.Add($"只改了不透明度却整套重新应用了主题"
                + $"（ThemeApplyCount {applies} → {theme.ThemeApplyCount}）");
        }

        // ---- 4. 真的换主题时，那套贵活必须照旧干 ----
        Core.Configuration.ThemeMode flipped = theme.IsDark
            ? Core.Configuration.ThemeMode.Light
            : Core.Configuration.ThemeMode.Dark;

        theme.Apply(baseSpec with { Theme = flipped, BackgroundBlur = max, BackgroundOpacity = 95 });

        if (theme.ThemeApplyCount <= applies)
        {
            bad.Add($"切到{(flipped == Core.Configuration.ThemeMode.Dark ? "深色" : "浅色")}时"
                + "没有重新应用主题 —— 分流条件把该干的活也一起挡掉了");
        }

        Relayout(model, window);

        // 有问题时别把那句「都正常」也写进报告 —— 报告自相矛盾比没有报告更难查。
        steps.Add(bad.Count == 0
            ? "模糊烘制核对完成：界面层无实时效果，半径变则重烘、不变则复用，"
                + $"只动外观参数不重应用主题，换主题仍会（ThemeApplyCount={theme.ThemeApplyCount}）。"
            : $"模糊烘制核对发现 {bad.Count} 项问题（ThemeApplyCount={theme.ThemeApplyCount}），详见下面的失败清单。");

        return bad;
    }

    /// <summary>
    /// 造一张自检用的小 PNG。
    ///
    /// 不从磁盘上找现成图片：目标机上不保证有任何一张图，而"找不到图就跳过这段自检"
    /// 等于让这个门在别人机器上是空的。用 <c>PngBitmapEncoder</c> 现编一张最省事，
    /// 顺带证明解码走的是真正的 WPF 图像管线。
    /// </summary>
    private static string WriteProbePng()
    {
        const int size = 64;
        const int stride = size * 3;

        byte[] pixels = new byte[stride * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * stride) + (x * 3);

                pixels[i] = (byte)(x * 4);          // B
                pixels[i + 1] = (byte)(y * 4);      // G
                pixels[i + 2] = 0x80;               // R
            }
        }

        System.Windows.Media.Imaging.BitmapSource source =
            System.Windows.Media.Imaging.BitmapSource.Create(
                size, size, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, pixels, stride);

        System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

        string path = Path.Combine(Path.GetTempPath(), $"NewAutoZip-selftest-{Guid.NewGuid():N}.png");

        using FileStream file = File.Create(path);
        encoder.Save(file);

        return path;
    }

    /// <summary>
    /// 布局体检：<b>文字不许叠在一起，列表不许被压扁。</b>
    ///
    /// 这两件事绑定诊断一条都抓不到 —— 绑定全对、编译全过、日志干净，
    /// 界面上却是"没有内容"的提示压在表头上糊成一片，或者"正在观察的文件"
    /// 被下面那块挤到只剩一条表头。用户看到的就是这个，所以得单独立一道闸门。
    ///
    /// 做法是照着渲染结果查，不是照着 XAML 猜：
    /// <list type="bullet">
    ///   <item>逐页布局后遍历可视树，取<b>真正可见</b>（<see cref="UIElement.IsVisible"/>，
    ///         祖先折叠会一路传下来）且有实际尺寸、有文字的 <see cref="TextBlock"/>，
    ///         换算到窗口坐标两两求交 —— 交集在两个方向都超过容差就是重叠；</item>
    ///   <item>可见的表格与日志列表要拿到能用的高度，低于下限就报出来。</item>
    /// </list>
    ///
    /// 文本框模板内部的占位文字（空框时显示、有内容时折叠）天生就压在输入区上，
    /// 那是控件的正常长相，遇到输入控件就整棵子树跳过。
    /// </summary>
    private static int CheckLayout(MainViewModel model, Views.MainWindow window, List<string> steps)
    {
        List<string> bad = [];
        int seen = 0;

        // 前面几步（改设置、切主题）会让列表被空快照同步掉，而<b>空表格是折叠的</b> ——
        // 不重新塞样例行，这道闸门就只量到了日志列表，表格那几处等于没测。
        InjectSamples(model);

        for (int page = 0; page < 6; page++)
        {
            ShowPage(model, window, page, withSamples: true);

            List<(TextBlock Block, Rect Box)> texts = [];
            List<(Control List, Rect Box)> lists = [];

            Collect(window, window, texts, lists, insideInput: false, clip: new Rect(0, 0, TestWidth, TestHeight));

            for (int i = 0; i < texts.Count; i++)
            {
                for (int j = i + 1; j < texts.Count; j++)
                {
                    Rect hit = Rect.Intersect(texts[i].Box, texts[j].Box);

                    if (hit.IsEmpty || hit.Width <= OverlapSlack || hit.Height <= OverlapSlack)
                    {
                        continue;
                    }

                    bad.Add($"页面 {page}：「{Excerpt(texts[i].Block)}」与「{Excerpt(texts[j].Block)}」" +
                            $"重叠 {hit.Width:0}×{hit.Height:0} 像素");
                }
            }

            foreach ((Control list, Rect box) in lists)
            {
                double floor = list is DataGrid ? MinGridHeight : MinLogHeight;
                seen++;

                if (box.Height + OverlapSlack < floor)
                {
                    bad.Add($"页面 {page}：{list.GetType().Name}「{list.Name}」" +
                            $"只有 {box.Height:0} 像素高，至少要 {floor:0}；" +
                            $"上下的高度是这样分掉的 —— {Chain(list, window)}");
                }
            }
        }

        if (bad.Count == 0)
        {
            steps.Add($"布局体检通过（{TestWidth}×{TestHeight}）：六个页面没有重叠文字，" +
                      $"表格与日志都拿到了够用的高度（共查了 {seen} 个列表）。");
            return 0;
        }

        foreach (string line in bad)
        {
            steps.Add($"布局缺陷：{line}");
        }

        return bad.Count;
    }

    /// <summary>
    /// 左上角真的是"程序名 + 版本号"这个顺序吗。
    ///
    /// 这道闸门看着琐碎，但它挡的是一类会静默失败的写法：程序名挂在
    /// <c>ui:TitleBar.Title</c>、版本号挂在 <c>ui:TitleBar.Header</c> 上，
    /// 而这两个属性到底渲不渲染、渲染在哪一侧，取决于控件模板 ——
    /// 模板里没有对应的 <c>ContentPresenter</c> 时<b>不报错、不警告，内容直接不画</b>。
    /// 绑定诊断也抓不到（绑定本身是成功的）。
    ///
    /// 所以不查 XAML、不查属性值，直接问渲染结果：屏幕上有没有两块可见文字分别
    /// 等于程序名和版本号、都落在窗口左上角那一带、并且<b>程序名在版本号左边</b>。
    /// 最后一条正是用户要的"先名字，后版本"，只查"两个都在"是查不出顺序反了的。
    /// </summary>
    private static int CheckTitleBar(MainViewModel model, Views.MainWindow window, List<string> steps)
    {
        List<(TextBlock Block, Rect Box)> texts = [];
        List<(Control List, Rect Box)> lists = [];

        Collect(window, window, texts, lists, insideInput: false, clip: new Rect(0, 0, TestWidth, TestHeight));

        int bad = 0;

        Rect? name = LocateText(texts, model.AppName);
        Rect? version = LocateText(texts, model.VersionText);

        if (name is null)
        {
            steps.Add($"界面缺陷：标题栏上没有渲染出程序名「{model.AppName}」" +
                      "（TitleBar.Title 没画出来，或者绑的属性名不对）。" +
                      $"顶部 60 像素内实际画出来的文字：{TopStripTexts(texts)}");
            bad++;
        }

        if (version is null)
        {
            steps.Add($"界面缺陷：标题栏上没有渲染出版本号「{model.VersionText}」" +
                      "（TitleBar.Header 没画出来，或者绑的属性名不对）。" +
                      $"顶部 60 像素内实际画出来的文字：{TopStripTexts(texts)}");
            bad++;
        }

        if (bad > 0)
        {
            return bad;
        }

        Rect n = name!.Value;
        Rect v = version!.Value;

        // 左上角的判定放宽到"左半边、且在状态条上方" —— 再严就变成在量像素而不是在查缺陷了。
        foreach ((string what, Rect box) in new[] { ("程序名", n), ("版本号", v) })
        {
            if (box.Left > TestWidth / 2 || box.Top > 60)
            {
                steps.Add($"界面缺陷：{what}画在了 ({box.Left:0}, {box.Top:0})，不在左上角。");
                bad++;
            }
        }

        if (v.Left <= n.Left)
        {
            steps.Add($"界面缺陷：顺序反了 —— 版本号在 x={v.Left:0}，程序名在 x={n.Left:0}，" +
                      "应该是先程序名、后版本号。");
            bad++;
        }

        if (bad > 0)
        {
            return bad;
        }

        steps.Add($"左上角顺序正确：程序名「{model.AppName}」在 ({n.Left:0},{n.Top:0})，" +
                  $"版本号「{model.VersionText}」在 ({v.Left:0},{v.Top:0})，共检查 {texts.Count} 块可见文字。");

        steps.Add($"关于窗口内容齐备：标题「{model.AboutTitle}」，抬头「{model.AboutHeadline}」，" +
                  $"{model.AboutRows.Count} 行明细（{string.Join(" / ", model.AboutRows.Select(r => r.Label))}），" +
                  $"兜底纯文本 {model.AboutText.Length} 字。");

        return 0;
    }

    /// <summary>
    /// 托盘右键菜单还挂着入场动画吗。
    ///
    /// WPF-UI 默认的 ContextMenu 模板给最外层 Border 挂了一条
    /// <c>(Border.RenderTransform).(TranslateTransform.Y)</c> 位移动画，菜单是"滑"出来的。
    /// 托盘菜单本来就贴着任务栏在鼠标位置弹出，再滑一段看起来像卡了一下。
    ///
    /// 这道闸门查三件事，缺一件都不算数：
    /// <list type="number">
    ///   <item>菜单确实套上了 <c>TrayMenu</c> 样式（和资源里那一份<b>引用相等</b>）——
    ///         只查"模板里没有动画"是空检查：<b>WPF 系统自带的 ContextMenu 模板也没有动画</b>，
    ///         样式压根没生效的情况下这一条照样通过。</item>
    ///   <item>菜单实际用的模板就是这个样式里的那一份（没被别处覆盖掉）。</item>
    ///   <item>这份模板里数不出任何入场动画的痕迹。</item>
    /// </list>
    /// WPF-UI 默认模板的动画条数只作为<b>实测参照</b>打进报告：取不到就如实说取不到，
    /// 那是我这边反射的局限，不是产品缺陷，不该记成 defect。
    /// </summary>
    private static int CheckTrayMenu(Views.MainWindow window, List<string> steps)
    {
        if (window.TrayMenu is not { } menu)
        {
            steps.Add("界面缺陷：托盘图标没有右键菜单（NotifyIcon.Menu 是空的）。");
            return 1;
        }

        object? resource = Application.Current?.TryFindResource(TrayMenuStyleKey);

        if (resource is not Style expectedStyle)
        {
            steps.Add($"界面缺陷：应用资源里找不到名为 {TrayMenuStyleKey} 的样式" +
                      $"（找到的是 {resource?.GetType().Name ?? "null"}）。");
            return 1;
        }

        if (!ReferenceEquals(menu.Style, expectedStyle))
        {
            steps.Add($"界面缺陷：托盘右键菜单没有套上 {TrayMenuStyleKey} 样式，" +
                      "弹出动画会退回 WPF-UI 默认的那一套滑动效果。");
            return 1;
        }

        ControlTemplate? ours = menu.Template;

        if (ours is null)
        {
            steps.Add("界面缺陷：托盘右键菜单没有解析出控件模板。");
            return 1;
        }

        if (!ReferenceEquals(ours, TemplateOf(expectedStyle)))
        {
            steps.Add($"界面缺陷：托盘右键菜单实际用的模板不是 {TrayMenuStyleKey} 里那一份，" +
                      "说明被别的样式或就地设置覆盖了。");
            return 1;
        }

        (int oursStoryboards, int oursTransforms) = CountAnimations(ours);

        if (oursStoryboards < 0)
        {
            steps.Add("检查未完成：托盘右键菜单的模板没能实例化，无法判断有没有入场动画。");
            return 1;
        }

        if (oursStoryboards > 0 || oursTransforms > 0)
        {
            steps.Add($"界面缺陷：托盘右键菜单模板里还有入场动画的痕迹（Storyboard {oursStoryboards} 条、" +
                      $"RenderTransform {oursTransforms} 处），应当都为 0（弹出效果交给系统）。");
            return 1;
        }

        steps.Add($"托盘右键菜单已套用 {TrayMenuStyleKey}，模板里 Storyboard {oursStoryboards} 条 / " +
                  $"RenderTransform {oursTransforms} 处，菜单项 {menu.Items.Count} 个；" +
                  $"对照 WPF-UI 默认模板：{DescribeDefaultContextMenuAnimations()}。");

        return 0;
    }

    /// <summary>App.xaml 里那个无动画托盘菜单样式的资源名。</summary>
    private const string TrayMenuStyleKey = "TrayMenu";

    /// <summary>把默认 ContextMenu 模板的动画数量说成一句人话（取不到就如实说）。</summary>
    private static string DescribeDefaultContextMenuAnimations()
    {
        if (DefaultContextMenuTemplate() is not { } template)
        {
            return "没取到（反射不到默认样式的 Template，只能少这一个参照）";
        }

        (int storyboards, int transforms) = CountAnimations(template);

        if (storyboards < 0)
        {
            return "取到了但实例化失败";
        }

        return $"Storyboard {storyboards} 条 / RenderTransform {transforms} 处";
    }

    /// <summary>WPF-UI 为 <see cref="ContextMenu"/> 注册的默认模板（实测参照）。</summary>
    private static ControlTemplate? DefaultContextMenuTemplate() =>
        Application.Current?.TryFindResource(typeof(ContextMenu)) is Style style
            ? TemplateOf(style)
            : null;

    /// <summary>
    /// 取一个样式最终生效的 <see cref="Control.Template"/>。
    ///
    /// <b>必须顺着 BasedOn 往上找。</b>WPF-UI 的隐式样式常常写成
    /// <c>&lt;Style TargetType="ContextMenu" BasedOn="{StaticResource DefaultUiContextMenu…}" /&gt;</c>——
    /// 它自己的 Setters 是空的，只看这一层会得到 null，然后被当成"默认模板里没有动画"。
    /// </summary>
    private static ControlTemplate? TemplateOf(Style? style)
    {
        for (Style? current = style; current is not null; current = current.BasedOn)
        {
            foreach (SetterBase setterBase in current.Setters)
            {
                if (setterBase is Setter { Property.Name: nameof(Control.Template), Value: ControlTemplate template })
                {
                    return template;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 数一个模板里一共有多少"入场动画"的痕迹。
    ///
    /// 关键教训：<b>只看 <c>ControlTemplate.Triggers</c> 是数不到的。</b>
    /// WPF-UI 的动画挂在模板<i>内部那个 Border</i> 的 <c>FrameworkElement.Triggers</c> 上
    /// （<c>&lt;Border.Triggers&gt;&lt;EventTrigger RoutedEvent="Loaded"&gt;</c>），
    /// 那是元素自己的触发器集合，不属于模板的触发器集合 ——
    /// 第一版检查就是这么把"其实有动画"数成了 0，反而报成"这道检查失效了"。
    ///
    /// 所以这里用 <see cref="FrameworkTemplate.LoadContent"/> 把模板真的实例化一份
    /// （不需要把菜单弹出来），再走一遍这棵树，两样东西都数：
    /// 元素触发器里的 <see cref="BeginStoryboard"/>，以及被安上 RenderTransform 的元素
    /// （位移动画必须先有一个可动的 Transform 才能改）。
    /// </summary>
    private static (int Storyboards, int Transforms) CountAnimations(ControlTemplate? template)
    {
        if (template is null)
        {
            return (0, 0);
        }

        DependencyObject root;

        try
        {
            root = template.LoadContent();
        }
        catch (Exception)
        {
            // 实例化不了就没法下结论，返回 -1 让调用方如实报告"没能检查"。
            return (-1, -1);
        }

        int storyboards = CountStoryboards(template.Triggers);
        int transforms = 0;

        Queue<DependencyObject> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();

            if (node is FrameworkElement element)
            {
                storyboards += CountStoryboards(element.Triggers);

                if (element.RenderTransform is { } transform && !transform.Value.IsIdentity)
                {
                    transforms++;
                }

                // TranslateTransform(0,0) 的 Value 就是单位矩阵，但它就是给动画准备的靶子，
                // 单独认一下 —— 漏掉它等于漏掉最典型的"滑进来"写法。
                if (element.RenderTransform is TranslateTransform or TransformGroup)
                {
                    transforms++;
                }
            }

            foreach (object? child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dependencyChild)
                {
                    queue.Enqueue(dependencyChild);
                }
            }
        }

        return (storyboards, transforms);
    }

    /// <summary>
    /// 数一个触发器集合里一共要启动几条 Storyboard。
    ///
    /// 四类触发器的动作挂在不同属性上，漏掉任何一类都会把"其实有动画"数成 0，
    /// 那正好是这道闸门最不该出的错。
    /// </summary>
    private static int CountStoryboards(IEnumerable<TriggerBase> triggers)
    {
        int count = 0;

        foreach (TriggerBase trigger in triggers)
        {
            switch (trigger)
            {
                case EventTrigger eventTrigger:
                    count += eventTrigger.Actions.OfType<BeginStoryboard>().Count();
                    break;

                case Trigger t:
                    count += t.EnterActions.OfType<BeginStoryboard>().Count()
                             + t.ExitActions.OfType<BeginStoryboard>().Count();
                    break;

                case MultiTrigger mt:
                    count += mt.EnterActions.OfType<BeginStoryboard>().Count()
                             + mt.ExitActions.OfType<BeginStoryboard>().Count();
                    break;

                case DataTrigger dt:
                    count += dt.EnterActions.OfType<BeginStoryboard>().Count()
                             + dt.ExitActions.OfType<BeginStoryboard>().Count();
                    break;

                case MultiDataTrigger mdt:
                    count += mdt.EnterActions.OfType<BeginStoryboard>().Count()
                             + mdt.ExitActions.OfType<BeginStoryboard>().Count();
                    break;
            }
        }

        return count;
    }

    /// <summary>
    /// 顶部那一条里到底画出了什么文字。
    ///
    /// "没找到"这种结论光说没找到是没法排查的 —— 把实际画出来的东西连坐标一起摊开，
    /// 才能一眼分清是"根本没画"还是"画了但文本不一样"。
    /// </summary>
    private static string TopStripTexts(List<(TextBlock Block, Rect Box)> texts)
    {
        List<string> shown = texts
            .Where(t => t.Box.Top <= 60)
            .OrderBy(t => t.Box.Left)
            .Take(12)
            .Select(t => $"「{Excerpt(t.Block)}」@{t.Box.Left:0},{t.Box.Top:0}")
            .ToList();

        return shown.Count == 0 ? "（一块都没有）" : string.Join("、", shown);
    }

    /// <summary>在收集到的文字块里找第一块文字正好等于 <paramref name="text"/> 的。</summary>
    private static Rect? LocateText(List<(TextBlock Block, Rect Box)> texts, string text)
    {
        foreach ((TextBlock block, Rect box) in texts)
        {
            if (block.Text.Trim() == text)
            {
                return box;
            }
        }

        return null;
    }

    /// <summary>
    /// 收集可见的文字块与列表。
    ///
    /// <paramref name="insideInput"/> 一旦置上就不再收文字 —— 输入控件模板里的占位符
    /// 本来就压在输入区上，那是控件的正常长相。
    ///
    /// <paramref name="clip"/> 是"这块地方到底还能看见多少"，随着遇到会裁剪的容器
    /// （<see cref="ScrollViewer"/> / <see cref="ScrollContentPresenter"/> /
    /// <c>ClipToBounds</c> / 显式 <c>Clip</c>）一路收窄。<b>没有它这套检查会全是假警报</b>：
    /// 设置页那一长条内容滚出可视区之后坐标仍然落在下面的校验卡片上，
    /// 只看坐标就会把"滚上去看不见的东西"报成重叠。
    /// </summary>
    private static void Collect(
        DependencyObject node,
        Visual root,
        List<(TextBlock Block, Rect Box)> texts,
        List<(Control List, Rect Box)> lists,
        bool insideInput,
        Rect clip)
    {
        if (node is UIElement { IsVisible: false })
        {
            return;
        }

        if (node is UIElement clipper
            && (clipper.ClipToBounds
                || clipper.Clip is not null
                || clipper is ScrollViewer or ScrollContentPresenter)
            && Locate(clipper, root) is { } own)
        {
            clip = Rect.Intersect(clip, own);

            if (clip.IsEmpty)
            {
                return;
            }
        }

        switch (node)
        {
            case System.Windows.Controls.Primitives.TextBoxBase
                 or PasswordBox
                 or ComboBox
                 or Wpf.Ui.Controls.NumberBox:
                insideInput = true;
                break;

            case TextBlock text when !insideInput
                                     && text.ActualWidth > 0.5
                                     && text.ActualHeight > 0.5
                                     && !string.IsNullOrWhiteSpace(text.Text):
                {
                    if (Visible(text, root, clip) is { } box)
                    {
                        texts.Add((text, box));
                    }

                    break;
                }

            // 导航栏那个 ListBox 是靠自己的内容撑高的，不该按日志列表的下限去要求它。
            case DataGrid or ListBox when node is Control listControl
                                          && !ReferenceEquals(node, (root as Views.MainWindow)?.NavRail):
                {
                    if (Locate(listControl, root) is { } box)
                    {
                        lists.Add((listControl, box));
                    }

                    break;
                }
        }

        int count = VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), root, texts, lists, insideInput, clip);
        }
    }

    /// <summary>元素真正露在外面的那部分；被祖先裁光了就返回 null（用户看不见的不算重叠）。</summary>
    private static Rect? Visible(UIElement element, Visual root, Rect clip)
    {
        if (Locate(element, root) is not { } box)
        {
            return null;
        }

        Rect shown = Rect.Intersect(box, clip);

        return shown.IsEmpty || shown.Width <= OverlapSlack || shown.Height <= OverlapSlack
            ? null
            : shown;
    }

    /// <summary>元素在窗口坐标系里的位置与尺寸；拿不到（还没接进可视树）就返回 null。</summary>
    private static Rect? Locate(UIElement element, Visual root)
    {
        try
        {
            Point origin = element.TransformToAncestor(root).Transform(new Point(0, 0));

            return new Rect(origin, element.RenderSize);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从元素往上一直数到窗口，把每一层的类型与高度串起来。
    ///
    /// "列表只有 72 像素高"本身没法定位 —— 是行定义写错了、还是上面某一块把高度吃掉了？
    /// 把这条链子打进报告，看一眼就知道该改哪一层。
    /// </summary>
    private static string Chain(DependencyObject node, Visual root)
    {
        List<string> hops = [];

        for (DependencyObject? cursor = node; cursor is not null; cursor = VisualTreeHelper.GetParent(cursor))
        {
            if (cursor is FrameworkElement fe)
            {
                string tag = string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : $"{fe.GetType().Name}({fe.Name})";
                string rows = cursor is Grid { RowDefinitions.Count: > 1 } grid
                    ? "[行高 " + string.Join("/", grid.RowDefinitions.Select(r => r.ActualHeight.ToString("0"))) + "]"
                    : string.Empty;

                hops.Add($"{tag} {fe.ActualWidth:0}×{fe.ActualHeight:0}{rows}");
            }

            if (ReferenceEquals(cursor, root))
            {
                break;
            }
        }

        return string.Join(" ← ", hops);
    }

    /// <summary>报告里用的文字摘要 —— 整段贴出来会把一行撑得没法读。</summary>
    private static string Excerpt(TextBlock text)
    {
        string flat = text.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return flat.Length <= 16 ? flat : flat[..16] + "…";
    }

    /// <summary>六个页面全部重新布局一遍。切外观会换掉整份控件字典，只停在当前页等于只测了六分之一。</summary>
    private static void Relayout(MainViewModel model, Views.MainWindow window)
    {
        for (int page = 0; page < 6; page++)
        {
            ShowPage(model, window, page, withSamples: true);
        }
    }

    /// <summary>
    /// 换了强调色之后，界面上<b>真的用上了新颜色</b> —— 不能慢一步。
    ///
    /// 这一条是踩出来的。<c>ThemeService</c> 原来的顺序是"先换控件字典、后贴强调色"，
    /// 而 WPF-UI 的主按钮样式在换字典的那一刻就把<b>当时</b>那只强调色画刷取走锁死了，
    /// 之后再改应用资源它不会回头看。表现是设置页上选红色、按钮还是上一次的黄色，
    /// 再选绿色按钮才变红 —— 资源字典里的值<b>全程都是对的</b>，只有渲染永远落后一次。
    /// 没有任何报错，唯一的症状就是"我选的颜色不是这个"。
    ///
    /// 所以判据不是"资源对不对"（那个骗不了人也测不出这个坑），而是连着换几次强调色，
    /// 每次都去可视树上量主按钮真正画出来的底色：
    /// 撞上<b>上一次</b>那个颜色就是没跟上。反向也卡一道 ——
    /// 一个都没画成当前颜色时同样算失败，否则哪天主按钮不再用这个键，
    /// 这条核对会变成一句永远通过的空话。
    /// </summary>
    private static int CheckAccentApplied(
        MainViewModel model,
        ThemeService theme,
        Views.MainWindow window,
        List<string> steps)
    {
        (string Label, Core.Configuration.ThemeMode Mode, string Hex)[] seq =
        [
            ("深色 / 绛红", Core.Configuration.ThemeMode.Dark, "#C4314B"),
            ("深色 / 常青", Core.Configuration.ThemeMode.Dark, "#2E9E6B"),
            ("浅色 / 紫罗兰", Core.Configuration.ThemeMode.Light, "#8B5CF6"),
            ("浅色 / 琥珀", Core.Configuration.ThemeMode.Light, "#D98324"),
            ("深色 / 默认蓝", Core.Configuration.ThemeMode.Dark, "#0078D4"),
        ];

        List<string> bad = [];
        System.Windows.Media.Color? previous = null;

        foreach ((string label, Core.Configuration.ThemeMode mode, string hex) in seq)
        {
            theme.Apply(mode, hex);
            Relayout(model, window);

            if (Application.Current?.TryFindResource(AccentFillKey) is not SolidColorBrush accent)
            {
                steps.Add($"强调色即时生效核对跳过：应用资源里没有 {AccentFillKey}（键名跨版本会变）。");
                return 0;
            }

            int matched = 0;
            int stale = 0;

            foreach (DependencyObject node in Descendants(window))
            {
                if (node is not Wpf.Ui.Controls.Button
                        { Appearance: Wpf.Ui.Controls.ControlAppearance.Primary } button
                    || button.Background is not SolidColorBrush painted)
                {
                    continue;
                }

                if (painted.Color == accent.Color)
                {
                    matched++;
                }
                else if (previous is { } old && painted.Color == old && old != accent.Color)
                {
                    stale++;
                }
            }

            if (stale > 0)
            {
                bad.Add($"{label}：{stale} 个主按钮还画着上一个强调色 {Hex(previous!.Value)}，" +
                        $"应当是 {Hex(accent.Color)}（资源里的值是对的，是界面没跟上）");
            }
            else if (matched == 0)
            {
                bad.Add($"{label}：一个主按钮都没有画成 {Hex(accent.Color)}，" +
                        $"这条核对失去意义（{AccentFillKey} 可能已经不是主按钮的底色了）");
            }

            steps.Add($"「{label}」强调色即时生效：{AccentFillKey} = {Hex(accent.Color)}，" +
                      $"{matched} 个主按钮已跟上，{stale} 个停在上一个颜色。");

            previous = accent.Color;
        }

        if (bad.Count == 0)
        {
            return 0;
        }

        steps.Add($"强调色即时生效核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 核对语义配色的两层。对每个键：
    /// <list type="bullet">
    ///   <item><c>*Color</c> 资源要存在且是 <see cref="System.Windows.Media.Color"/>；</item>
    ///   <item>画刷要存在、且是 <see cref="System.Windows.Media.SolidColorBrush"/> ——
    ///         <see cref="ThemeService"/> 切主题时按键换掉这只画刷，键不在就换不到。</item>
    /// </list>
    ///
    /// <b>这里刻意不查 IsFrozen。</b>进过 <c>Style</c> / <c>DataTrigger</c> 的 <c>Setter</c>
    /// 的画刷一定会被 Freeze（样式为了共享值会冻掉可冻的值），那是 WPF 的正常行为，
    /// 不是缺陷 —— 主题跟得上是因为 <c>ThemeService</c> 换的是<b>实例</b>，
    /// 冻不冻都无所谓。真正要盯的是"界面上渲染出来的颜色对不对"，那件事交给
    /// <see cref="CheckPaletteValues"/>：它逐键比实际值和该主题应有的值，
    /// 比 IsFrozen 严格得多，也不会把正常行为报成缺陷。
    ///
    /// 键缺了的话，切主题时那个颜色就会<b>静静地停在上一个主题的值上</b> ——
    /// 不报错、不崩，只是白底上出现一行看不见的字。所以在这里主动查出来，
    /// 让它变成 <c>publish.cmd</c> 能挡住的失败。
    /// </summary>
    private static int CheckPalette(List<string> steps)
    {
        if (Application.Current is not { } app)
        {
            steps.Add("语义配色核对：Application.Current 为空，已跳过。");
            return 0;
        }

        List<string> bad = [];

        foreach (string key in ThemeService.PaletteKeys)
        {
            string colorKey = ThemeService.ColorKeyOf(key);

            if (app.TryFindResource(colorKey) is not System.Windows.Media.Color)
            {
                bad.Add($"{colorKey}（颜色资源不存在，主题切换时改不到这个色）");
            }

            object? found = app.TryFindResource(key);

            if (found is not System.Windows.Media.SolidColorBrush)
            {
                bad.Add($"{key}（{(found is null ? "画刷不存在" : $"类型是 {found.GetType().Name}")}）");
            }
        }

        if (bad.Count == 0)
        {
            steps.Add($"语义配色核对：{ThemeService.PaletteKeys.Count} 个键的颜色与画刷两层全部可用。");
            return 0;
        }

        steps.Add($"语义配色核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 切完主题之后，语义色<b>真的</b>换过来了吗。
    ///
    /// <see cref="CheckPalette"/> 只查结构（键在不在、类型对不对），
    /// 查不出"切到浅色了但某个颜色还停在深色值上" —— 那种缺陷不报错、不崩，
    /// 只是白底上出现一行看不见的字，而且<b>只有那一个键</b>出问题时更难发现：
    /// 旁边的颜色都换对了，看起来一切正常。
    ///
    /// 所以这里把资源字典里的两层和 <see cref="ThemeService.Expected"/> 逐个对值。
    /// 顺带核对深浅两套覆盖的键完全一致 —— <c>PaletteKeys</c> 是从深色那套推出来的，
    /// 浅色少写一个键的话前面那道闸门根本看不见。
    ///
    /// 字典里对了还不等于界面上对，那一步交给 <see cref="CheckRenderedNotStale"/>。
    /// </summary>
    private static int CheckPaletteValues(ThemeService theme, string label, List<string> steps)
    {
        if (Application.Current is not { } app)
        {
            return 0;
        }

        List<string> bad = [];

        HashSet<string> dark = [.. ThemeService.Expected(Wpf.Ui.Appearance.ApplicationTheme.Dark).Select(e => e.Key)];
        HashSet<string> light = [.. ThemeService.Expected(Wpf.Ui.Appearance.ApplicationTheme.Light).Select(e => e.Key)];

        foreach (string missing in dark.Except(light))
        {
            bad.Add($"{missing} 只在深色那套里有，浅色下会停在深色值上");
        }

        foreach (string extra in light.Except(dark))
        {
            bad.Add($"{extra} 只在浅色那套里有，深色下会停在浅色值上");
        }

        foreach ((string key, System.Windows.Media.Color want) in ThemeService.Expected(theme.Effective))
        {
            if (app.TryFindResource(ThemeService.ColorKeyOf(key)) is System.Windows.Media.Color got && got != want)
            {
                bad.Add($"{ThemeService.ColorKeyOf(key)} 是 {Hex(got)}，应当是 {Hex(want)}");
            }

            if (app.TryFindResource(key) is SolidColorBrush brush && brush.Color != want)
            {
                bad.Add($"画刷 {key} 停在 {Hex(brush.Color)}，应当是 {Hex(want)}");
            }
        }

        if (bad.Count == 0)
        {
            steps.Add($"「{label}」语义配色取值核对通过：{ThemeService.PaletteKeys.Count} 个键的实际画刷都等于该主题的值。");
            return 0;
        }

        steps.Add($"「{label}」语义配色取值核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>列表行配色一共就这八个键。少一个都说明调色板被改漏了。</summary>
    private static readonly string[] ListRowKeys =
    [
        "ListRow",
        "ListHover",
        "ListHoverBorder",
        "ListSelected",
        "ListSelectedBorder",
        "ListSelectedText",
        "SelectionInactive",
        "SelectionInactiveText",
    ];

    /// <summary>
    /// 列表行的配色规矩。钉住的是需求原话：<b>「颜色要淡雅、固定的」</b>、
    /// <b>「失去焦点后，也要能正常看清，底色不要太暗」</b>、<b>「背景色不要和按钮一个色」</b>。
    ///
    /// 导航栏和日志列表共用 <c>SelectableListBoxItem</c> 那份模板，
    /// <see cref="ListRowKeys"/> 那八个键就是它的全部配色。
    ///
    /// <b>三档明度，一路拉开</b>：普通行 → 悬停 → 选中，离背景越来越远。
    /// 普通行还额外有个<b>上限</b>（<see cref="MaxListRowSeparation"/>）—— 那就是"淡雅"这个词
    /// 唯一能量化的部分：一行的底色只许比背景挪一点点，显眼的名额留给选中的那一行。
    /// 这一条同时也是"不要和按钮一个色"的算术版：强调色离背景通常有两三百，
    /// 普通行要是又继承到它，这里立刻就红。
    ///
    /// <b>失焦这一条最要紧</b>：失焦态和聚焦态<b>共用同一个底色</b>，只靠描边区分。
    /// 这样"焦点一走底色就变暗"在结构上就不可能发生 —— 不是把它调到某个刚好够亮的值，
    /// 而是根本没有第二个值可调。剩下几条守的是"看得清"：字压在底色上够对比、
    /// 底色和背后的面板、和悬停色都分得开、描边确实看得见。
    ///
    /// 这里<b>不设绝对亮度下限</b>。浅色主题下选中色只能比白底更深（往上没有余量），
    /// 一条"不许低于某个亮度"的规则会让浅色那套永远过不去；
    /// "不要太暗"真正要的是<b>字还看得清、行还认得出</b>，那正是下面几条量的东西。
    ///
    /// 和 <see cref="CheckPaletteValues"/> 的分工：那个查"资源字典里的值对不对"，
    /// 这个查"这组值本身合不合规矩"，最后再加两条界面上的实测（选中的那一行 + 没选中的那一行）——
    /// 自检里导航栏没有键盘焦点，所以量到的正是用户抱怨的那个失焦态。
    /// 实测那两条是<b>唯一</b>能发现"某个状态的底色压根没设、于是从基样式继承成强调色"的检查：
    /// 算术只看得见我们自己写下的值，看不见我们<b>没</b>写下的那个。
    /// </summary>
    private static int CheckListRowColors(
        Views.MainWindow window, ThemeService theme, string label, List<string> steps)
    {
        Dictionary<string, System.Windows.Media.Color> palette =
            ThemeService.Expected(theme.Effective).ToDictionary(e => e.Key, e => e.Color);

        if (ListRowKeys.FirstOrDefault(k => !palette.ContainsKey(k)) is { } missing)
        {
            steps.Add($"「{label}」选中行配色核对未完成：调色板里没有 {missing}。");
            return 1;
        }

        System.Windows.Media.Color row = palette["ListRow"];
        System.Windows.Media.Color hover = palette["ListHover"];
        System.Windows.Media.Color selected = palette["ListSelected"];
        System.Windows.Media.Color border = palette["ListSelectedBorder"];
        System.Windows.Media.Color text = palette["ListSelectedText"];
        System.Windows.Media.Color idle = palette["SelectionInactive"];
        System.Windows.Media.Color idleText = palette["SelectionInactiveText"];

        List<string> bad = [];

        // 失焦态必须和聚焦态同底色同字色 —— 这是"底色不要太暗"的结构性保证。
        if (idle != selected)
        {
            bad.Add($"失焦选中底色 {Hex(idle)} 和聚焦时的 {Hex(selected)} 不是同一个色，"
                + "焦点一走这一行就会变色");
        }

        if (idleText != text)
        {
            bad.Add($"失焦选中文字色 {Hex(idleText)} 和聚焦时的 {Hex(text)} 不是同一个色");
        }

        double onFill = Contrast(text, selected);

        if (onFill < MinListTextContrast)
        {
            bad.Add($"选中行的字压在底色上只有 {onFill:0.00}:1，低于 {MinListTextContrast:0.0}:1");
        }

        System.Windows.Media.Color surface = BaseSurface();
        double fromSurface = Distance(selected, surface);

        if (fromSurface < MinListSeparation)
        {
            bad.Add($"选中底色 {Hex(selected)} 和背景 {Hex(surface)} 只差 {fromSurface:0.0}，"
                + $"低于 {MinListSeparation:0.0}，看不出哪一行被选中");
        }

        double fromHover = Distance(selected, hover);

        if (fromHover < MinListSeparation)
        {
            bad.Add($"选中底色和悬停底色 {Hex(hover)} 只差 {fromHover:0.0}，"
                + $"低于 {MinListSeparation:0.0}");
        }

        double hoverFromSurface = Distance(hover, surface);

        if (hoverFromSurface < MinListHoverSeparation)
        {
            bad.Add($"悬停底色 {Hex(hover)} 和背景 {Hex(surface)} 只差 {hoverFromSurface:0.0}，"
                + $"低于 {MinListHoverSeparation:0.0}");
        }

        // 普通行：既要认得出是一行，又不许抢眼。上限那一条就是"淡雅"。
        double rowFromSurface = Distance(row, surface);

        if (rowFromSurface < MinListRowSeparation)
        {
            bad.Add($"普通行底色 {Hex(row)} 和背景 {Hex(surface)} 只差 {rowFromSurface:0.0}，"
                + $"低于 {MinListRowSeparation:0.0}，一行看不出是一行");
        }

        if (rowFromSurface > MaxListRowSeparation)
        {
            bad.Add($"普通行底色 {Hex(row)} 离背景 {Hex(surface)} 有 {rowFromSurface:0.0}，"
                + $"超过 {MaxListRowSeparation:0.0} —— 界面上行数最多的就是它，这么重会抢眼，"
                + "不再是\"淡雅\"，也容易和主按钮那种实色块混同");
        }

        // 三档明度必须一路往外走。顺序反了的话，"选了哪行"就要靠猜。
        if (rowFromSurface >= hoverFromSurface || hoverFromSurface >= fromSurface)
        {
            bad.Add($"三档底色离背景的距离没有依次拉开（普通 {rowFromSurface:0.0}、"
                + $"悬停 {hoverFromSurface:0.0}、选中 {fromSurface:0.0}），"
                + "普通 → 悬停 → 选中应当越来越远");
        }

        double edge = Contrast(border, selected);

        if (edge < MinListBorderContrast)
        {
            bad.Add($"选中描边 {Hex(border)} 压在底色上只有 {edge:0.00}:1，"
                + $"低于 {MinListBorderContrast:0.0}:1 —— 底色两态相同，描边看不见就等于"
                + "聚焦和失焦长得一模一样");
        }

        // 文本框选中的那段字、表格的内建部件都不走上面那份模板，它们从 SystemColors
        // 那两个键取色。ThemeService 每次换主题都把语义色投过去，投漏了就会出现
        // "同一个失焦选中，界面上两个颜色"。
        if (Application.Current is { } app)
        {
            if (Solid(app.TryFindResource(SystemColors.InactiveSelectionHighlightBrushKey) as Brush) is { } sysFill
                && sysFill != idle)
            {
                bad.Add($"SystemColors 的失焦选中底色停在 {Hex(sysFill)}，"
                    + $"和语义色 {Hex(idle)} 不一致（文本框、表格会用它）");
            }

            if (Solid(app.TryFindResource(SystemColors.InactiveSelectionHighlightTextBrushKey) as Brush) is { } sysText
                && sysText != idleText)
            {
                bad.Add($"SystemColors 的失焦选中文字色停在 {Hex(sysText)}，"
                    + $"和语义色 {Hex(idleText)} 不一致");
            }
        }

        // 界面实测：上面全是算术，这两条量的是真画在导航栏上的那一层。
        string measured;

        if (RenderedNavFills(window) is not { } painted)
        {
            measured = "（导航栏选中项的底色没量到，模板里那个 Bd 可能改了名字）";
            bad.Add("量不到导航栏选中项界面上的底色");
        }
        else
        {
            // 两态同色，所以不管焦点在不在，画出来的都该是这一个值。
            string state = painted.Active ? "焦点在导航栏" : "焦点不在导航栏";
            string normalText = painted.Normal is { } shown
                ? $"，没选中的行 {Hex(shown)}"
                : "，没选中的行没量到";

            measured = $"界面实测：{state}时选中行底色 {Hex(painted.Selected)}{normalText}。";

            if (painted.Selected != selected)
            {
                bad.Add($"导航栏选中项画出来的底色是 {Hex(painted.Selected)}（{state}），"
                    + $"应当是 {Hex(selected)}");
            }

            if (painted.Normal is not { } normal)
            {
                bad.Add("量不到导航栏没选中那几项界面上的底色 —— 普通态底色多半压根没设，"
                    + "从基样式继承成了透明或非纯色画刷");
            }
            else if (normal != row)
            {
                bad.Add($"导航栏没选中的行画出来的底色是 {Hex(normal)}，应当是 {Hex(row)}"
                    + " —— 普通态底色没在样式里钉住，从基样式继承到别的颜色了"
                    + "（历史上继承到的就是强调色，于是整条功能区和主按钮一个色）");
            }
        }

        if (bad.Count == 0)
        {
            steps.Add($"「{label}」列表行配色核对通过：普通 {Hex(row)}（离背景 {rowFromSurface:0.0}）"
                + $"→ 悬停 {hoverFromSurface:0.0} → 选中 {Hex(selected)} {fromSurface:0.0} 依次拉开，"
                + $"失焦与聚焦同底色，字 {onFill:0.0}:1、离悬停 {fromHover:0.0}、"
                + $"描边 {edge:0.0}:1。{measured}");
            return 0;
        }

        steps.Add($"「{label}」列表行配色核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 导航栏<b>界面上真正画出来</b>的两个底色：选中那一项、以及没选中的某一项，
    /// 外加当时算不算"焦点在这儿"。
    ///
    /// 取的是模板里那个名为 <c>Bd</c> 的 Border —— 样式和四个触发器改的都是它的
    /// Background / BorderBrush。取不到就返回 <c>null</c>（或 <c>Normal = null</c>），
    /// 让调用方如实报"这条没量到"，而不是当成通过悄悄放过去。
    ///
    /// 挑"没选中的那一项"时会<b>跳过鼠标底下的那一项</b>：自检跑的时候鼠标正好停在
    /// 导航栏上是完全可能的，那一项画的是悬停色，拿它去比普通色就会冤枉一次构建。
    /// </summary>
    private static (System.Windows.Media.Color Selected, System.Windows.Media.Color? Normal, bool Active)?
        RenderedNavFills(Views.MainWindow window)
    {
        if (window.NavRail.SelectedItem is not ListBoxItem item)
        {
            return null;
        }

        if (NavItemFill(item) is not { } fill)
        {
            return null;
        }

        bool active = item.GetValue(
            System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty) is true;

        System.Windows.Media.Color? normal = window.NavRail.Items
            .OfType<ListBoxItem>()
            .Where(i => !i.IsSelected && !i.IsMouseOver && i.IsEnabled)
            .Select(NavItemFill)
            .FirstOrDefault(c => c is not null);

        return (fill, normal, active);
    }

    /// <summary>某个导航项模板里那层 Border 画出来的纯色底；量不到就是 <c>null</c>。</summary>
    private static System.Windows.Media.Color? NavItemFill(ListBoxItem item)
    {
        item.ApplyTemplate();

        Border? bd = item.Template?.FindName("Bd", item) as Border
            ?? Descendants(item).OfType<Border>().FirstOrDefault(b => Solid(b.Background) is not null);

        return Solid(bd?.Background);
    }

    /// <summary>
    /// 界面上有没有哪块颜色<b>停在另一套主题的值上</b>。
    ///
    /// 上面那个 <see cref="CheckPaletteValues"/> 查的是资源字典里的值，
    /// 而字典里对不等于界面上对：主题切换要真正到位，还得靠
    /// <c>DynamicResource</c> 把"资源换了"这件事通知到每一个引用它的元素。
    /// 中间任何一环用了 <c>StaticResource</c>、或者某个值被样式缓存住，
    /// 界面就会安静地保留上一个主题的颜色 —— 白底上一行看不见的字，程序不报任何错。
    ///
    /// 判据很直接：把<b>另一套</b>主题的调色板当成"陈旧色"的指纹（两套同色的键要剔掉，
    /// 那种颜色本来就不该变），然后扫一遍可视树上所有画上去的纯色。
    /// 撞上指纹就说明那个元素没跟上这次切换。
    /// </summary>
    private static int CheckRenderedNotStale(
        Views.MainWindow window, ThemeService theme, string label, List<string> steps)
    {
        Wpf.Ui.Appearance.ApplicationTheme other =
            theme.Effective == Wpf.Ui.Appearance.ApplicationTheme.Light
                ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                : Wpf.Ui.Appearance.ApplicationTheme.Light;

        Dictionary<System.Windows.Media.Color, string> fingerprints = [];

        foreach ((string key, System.Windows.Media.Color color) in ThemeService.Expected(other))
        {
            fingerprints[color] = key;
        }

        foreach ((_, System.Windows.Media.Color color) in ThemeService.Expected(theme.Effective))
        {
            fingerprints.Remove(color);
        }

        // 纯白和纯黑当不了指纹：它们是半个界面的默认文字色和默认底色，
        // 撞上了什么也说明不了（实测浅色徽章文字改成纯白后，深色主题下
        // 605 处普通文字全被当成"没跟上主题切换"）。
        // 代价是"某个键的另一套值恰好是纯白/纯黑"这一种情况这道闸门查不到 ——
        // 如实记在报告里，别假装覆盖了。
        List<string> blind = [];

        foreach (System.Windows.Media.Color plain in
                 (System.Windows.Media.Color[])[Colors.White, Colors.Black])
        {
            if (fingerprints.Remove(plain, out string? key))
            {
                blind.Add(key);
            }
        }

        Dictionary<string, int> hits = [];
        int painted = 0;

        foreach (DependencyObject node in Descendants(window))
        {
            foreach (Brush? brush in Painted(node))
            {
                if (Solid(brush) is not { } color)
                {
                    continue;
                }

                painted++;

                if (fingerprints.TryGetValue(color, out string? key))
                {
                    hits[key] = hits.GetValueOrDefault(key) + 1;
                }
            }
        }

        if (hits.Count == 0)
        {
            steps.Add($"「{label}」界面渲染核对通过：{painted} 处纯色里没有一处停在另一套主题的值上" +
                      $"（{fingerprints.Count} 个键可查" +
                      $"{(blind.Count == 0 ? string.Empty : $"，{string.Join(" / ", blind)} 的另一套值是纯白或纯黑，查不了")}）。");
            return 0;
        }

        steps.Add($"「{label}」界面渲染核对失败 {hits.Count} 项：" +
                  string.Join("；", hits.Select(h => $"{h.Key} 的另一套主题色出现了 {h.Value} 处，说明没跟上主题切换")));
        return hits.Count;
    }

    /// <summary>整棵可视树，含自己。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            foreach (DependencyObject node in Descendants(VisualTreeHelper.GetChild(root, i)))
            {
                yield return node;
            }
        }
    }

    /// <summary>这个元素实际画到屏幕上的那些画刷。</summary>
    private static IEnumerable<Brush?> Painted(DependencyObject node)
    {
        switch (node)
        {
            case System.Windows.Shapes.Shape shape:
                yield return shape.Fill;
                yield return shape.Stroke;
                break;
            case TextBlock text:
                yield return text.Foreground;
                break;
            case Border border:
                yield return border.Background;
                yield return border.BorderBrush;
                break;
            case Control control:
                yield return control.Foreground;
                yield return control.Background;
                yield return control.BorderBrush;
                break;
        }
    }

    // ==================================================================
    //  「执行到哪一步了」
    // ==================================================================

    /// <summary>
    /// 步骤列表与「正在进行」徽章。
    ///
    /// 自检时引擎是停着的，<see cref="PipelineStepMap.ActiveIndex"/> 返回 -1，
    /// 徽章那个 Border 从头到尾都是 Collapsed —— <b>不主动点亮它，这道闸门就是空跑</b>：
    /// 配色写错、模板里少个 presenter、被别的元素挤成零宽，全都测不出来。
    /// 所以这里逐个把 <see cref="PipelineStepView.IsActive"/> 掀成 true，
    /// 每次真的布局一遍，再从可视树里把徽章找出来量它。
    ///
    /// 三条判据：
    /// <list type="number">
    ///   <item>阶段 → 下标的映射在两种云盘模式下都闭合（见 <see cref="CheckStepMapping"/>）；</item>
    ///   <item>点亮一步之后，界面上<b>不多不少正好一个</b>可见的「正在进行」，且有实际尺寸；</item>
    ///   <item>徽章文字与底色的对比度够读，底色与背后那块面板足够区分 ——
    ///         这就是"背景色要和主题色区分开、注意要能看清文字"的可检验版本。</item>
    /// </list>
    ///
    /// 深浅两套都跑：浅色下"淡紫药丸压在近白卡片上"这种问题深色下完全看不出来。
    /// 跑完把外观还原，自检不该有副作用。
    /// </summary>
    private static int CheckPipelineSteps(
        MainViewModel model,
        ThemeService theme,
        Views.MainWindow window,
        List<string> steps)
    {
        List<string> bad = [.. CheckStepMapping()];

        Core.Configuration.AppearanceSpec original = theme.Appearance;

        try
        {
            (string Label, Core.Configuration.ThemeMode Mode)[] modes =
            [
                ("深色", Core.Configuration.ThemeMode.Dark),
                ("浅色", Core.Configuration.ThemeMode.Light),
            ];

            foreach ((string label, Core.Configuration.ThemeMode mode) in modes)
            {
                theme.Apply(original with { Theme = mode });
                bad.AddRange(CheckBadge(model, window, label, steps));
            }
        }
        finally
        {
            theme.Apply(original);
            Relayout(model, window);
        }

        if (bad.Count == 0)
        {
            steps.Add("「执行到哪一步了」核对通过：阶段映射闭合，「正在进行」徽章深浅两套都显示且文字读得清。");
            return 0;
        }

        steps.Add($"「执行到哪一步了」核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 「保存后立刻切换云盘模式」的守门检查。
    ///
    /// 用户报的问题：设置页把云盘类型改成普通目录、点保存，总览页上的
    /// 「打开 OneDrive 目录」和右边的步骤列表却一点不变，非得再点一次"开始"才跟上。
    /// 根因是那几个属性读的是<b>引擎快照</b>里的类型，而
    /// <see cref="Core.Pipeline.EngineSnapshot.Stopped"/> 里写死的是 OneDrive ——
    /// 引擎停着时那个值永远不变，等它是等不到的。
    ///
    /// 这里不点界面上的"保存"，而是把两个输入直接喂给
    /// <see cref="MainViewModel.SyncCloudTarget(Core.Pipeline.EngineSnapshot, Core.Configuration.CloudTarget)"/>：
    /// 走"保存"会真的往 settings.json 里写，而自检是诊断命令，不该改用户的配置
    /// （同 <see cref="CheckAppearanceNotDirty"/> 的理由）。代价是"保存里那句
    /// SyncCloudTarget() 被删掉"这种改动它挡不住 —— 所以下面头两格特意用
    /// <b>合成的旧 bug 场景</b>（快照说一种、磁盘说另一种）把"引擎停着就得跟磁盘"钉死，
    /// 那正是当初出错的那一格。
    ///
    /// 六格按顺序走，最后两格连起来就是"引擎停止后会自动切换"那句承诺：
    /// <list type="number">
    ///   <item>停止 + 磁盘是普通目录 → 按普通目录显示；</item>
    ///   <item>停止 + 磁盘是 OneDrive → 按 OneDrive 显示；</item>
    ///   <item>按 OneDrive 在跑 + 磁盘改成了普通目录 → 显示<b>不</b>动，但提示要出来；</item>
    ///   <item>按 OneDrive 在跑 + 磁盘也是 OneDrive → 不提示；</item>
    ///   <item>按普通目录在跑 + 磁盘改成了 OneDrive → 反方向同样不许提前切；</item>
    ///   <item>引擎停下来 → 自动切到磁盘那份，提示消失。</item>
    /// </list>
    /// </summary>
    private static int CheckCloudTargetSwitch(
        MainViewModel model,
        Views.MainWindow window,
        List<string> steps)
    {
        const Core.Configuration.CloudTarget one = Core.Configuration.CloudTarget.OneDrive;
        const Core.Configuration.CloudTarget dir = Core.Configuration.CloudTarget.Folder;

        List<string> bad = [];

        Views.DashboardPage? dash = Descendants(window).OfType<Views.DashboardPage>().FirstOrDefault();

        if (dash is null)
        {
            steps.Add("检查未完成：可视树里找不到总览页，云盘模式切换核对已跳过。");
            return 0;
        }

        // 两种模式的步数必须不同，否则下面那条步数断言就成了摆设。
        int oneSteps = PipelineStepMap.Build(one).Count;
        int dirSteps = PipelineStepMap.Build(dir).Count;

        if (oneSteps == dirSteps)
        {
            bad.Add($"两种云盘模式的步数一样（都是 {oneSteps} 步），"
                + "步骤列表有没有跟着重建就查不出来了");
        }

        // 期望值全部写死，不许用被测的那套规则去算 —— 那样等于拿答案对答案。
        (string Label, bool Running, Core.Configuration.CloudTarget Engine,
            Core.Configuration.CloudTarget Saved, Core.Configuration.CloudTarget Shown, bool Pending)[] cases =
        [
            ("引擎停止、磁盘上是普通目录",              false, one, dir, dir, false),
            ("引擎停止、磁盘上是 OneDrive",             false, dir, one, one, false),
            ("引擎按 OneDrive 在跑、磁盘改成了普通目录", true,  one, dir, one, true),
            ("引擎按 OneDrive 在跑、磁盘也是 OneDrive",  true,  one, one, one, false),
            ("引擎按普通目录在跑、磁盘改成了 OneDrive",  true,  dir, one, dir, true),
            ("引擎停下来了、磁盘上仍是 OneDrive",        false, dir, one, one, false),
        ];

        try
        {
            foreach ((string label, bool running, Core.Configuration.CloudTarget engine,
                Core.Configuration.CloudTarget saved, Core.Configuration.CloudTarget shown, bool pending) in cases)
            {
                model.SyncCloudTarget(
                    Core.Pipeline.EngineSnapshot.Stopped with { Running = running, CloudTarget = engine },
                    saved);

                model.SelectedPage = 0;
                Settle(window);

                bad.AddRange(CheckShownTarget(model, dash, label, shown, saved, pending));
            }
        }
        finally
        {
            // 回到真实状态：往后还有布局体检等着，不能让它们看见自检摆出来的假场景。
            model.SyncCloudTarget();
            Relayout(model, window);
        }

        if (bad.Count == 0)
        {
            steps.Add($"云盘模式切换核对通过：{cases.Length} 种情形下按钮文字、步数（{oneSteps} ⇄ {dirSteps} 步）、"
                + "送达措辞与待切换提示全部一致；引擎停着跟磁盘、在跑跟引擎并给出提示。");
            return 0;
        }

        steps.Add($"云盘模式切换核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 容量卡片：三种情形各摆一份快照，核对卡片该藏的时候藏、该红的时候红。
    ///
    /// 必须自己喂快照 —— 卡片整张的可见性绑在 <c>CloudQuotaConfigured</c> 上，
    /// 而 <see cref="Core.Pipeline.EngineSnapshot.Stopped"/> 里那个值是 0。
    /// 不喂就永远折叠，而<b>折叠元素不参与布局</b>，里面那几条绑定一条都测不到。
    ///
    /// 三格：
    /// <list type="number">
    ///   <item>没填总容量 → 整张卡片藏起来；</item>
    ///   <item>填了、剩余还宽裕 → 卡片出现、不红；</item>
    ///   <item>填了、剩余已低于警戒线 → 卡片出现且变红。</item>
    /// </list>
    ///
    /// 进度条那一格顺带核对"已用超过预算也不许超过 100"——
    /// 用户把预算填得比现有占用还小是很常见的，而进度条吃到 130 会画到框外面去。
    /// </summary>
    private static int CheckCloudQuotaCard(
        MainViewModel model,
        Views.MainWindow window,
        List<string> steps)
    {
        Views.DashboardPage? dash = Descendants(window).OfType<Views.DashboardPage>().FirstOrDefault();

        if (dash is null)
        {
            steps.Add("检查未完成：可视树里找不到总览页，容量卡片核对已跳过。");
            return 0;
        }

        const long gb = 1024L * 1024 * 1024;

        List<string> bad = [];

        // 期望值全部写死，不拿被测的那套算式去算。
        (string Label, long Quota, long Used, long Free, long Warn, bool Shown, bool Low, double Percent)[] cases =
        [
            ("没填总容量",              0,        0,        0,       0,        false, false, 0),
            ("填了 100 GB、剩余 60 GB", 100 * gb, 40 * gb,  60 * gb, 10 * gb,  true,  false, 40),
            ("剩余 5 GB、警戒线 10 GB", 100 * gb, 95 * gb,  5 * gb,  10 * gb,  true,  true,  95),
            ("已用超过预算",            10 * gb,  30 * gb,  0,       gb,       true,  true,  100),
        ];

        try
        {
            foreach ((string label, long quota, long used, long free, long warn,
                bool shown, bool low, double percent) in cases)
            {
                model.ApplySnapshotForSelfTest(Core.Pipeline.EngineSnapshot.Stopped with
                {
                    CloudQuotaBytes = quota,
                    CloudUsedBytes = used,
                    CloudFreeBytes = free,
                    CloudQuotaWarnBytes = warn,
                });

                model.SelectedPage = 0;
                Settle(window);

                bool visible = dash.CloudQuotaCard.Visibility == Visibility.Visible;

                if (visible != shown)
                {
                    bad.Add($"{label}：卡片{(visible ? "出现了" : "没出现")}，"
                        + $"应当{(shown ? "出现" : "不出现")}");
                }

                if (model.CloudQuotaLow != low)
                {
                    bad.Add($"{label}：CloudQuotaLow 是 {model.CloudQuotaLow}，应当是 {low}");
                }

                if (Math.Abs(model.CloudUsedPercent - percent) > 0.01)
                {
                    bad.Add($"{label}：进度条 {model.CloudUsedPercent:0.##}%，应当是 {percent:0.##}%");
                }

                if (!shown)
                {
                    continue;
                }

                // 界面上那两行必须和属性一字不差 —— 不然就是 XAML 没绑（或者绑到别处去了）。
                if (!string.Equals(dash.CloudQuotaUsageLine.Text, model.CloudQuotaUsageText, StringComparison.Ordinal))
                {
                    bad.Add($"{label}：界面上用量那行是「{dash.CloudQuotaUsageLine.Text}」，"
                        + $"属性值却是「{model.CloudQuotaUsageText}」（XAML 没绑 CloudQuotaUsageText）");
                }

                if (!string.Equals(dash.CloudQuotaWarnLine.Text, model.CloudQuotaWarnText, StringComparison.Ordinal))
                {
                    bad.Add($"{label}：界面上警戒线那行是「{dash.CloudQuotaWarnLine.Text}」，"
                        + $"属性值却是「{model.CloudQuotaWarnText}」（XAML 没绑 CloudQuotaWarnText）");
                }

                if (Math.Abs(dash.CloudQuotaBar.Value - model.CloudUsedPercent) > 0.01)
                {
                    bad.Add($"{label}：进度条显示 {dash.CloudQuotaBar.Value:0.##}，"
                        + $"属性值却是 {model.CloudUsedPercent:0.##}（XAML 没绑 CloudUsedPercent）");
                }
            }
        }
        finally
        {
            // 回到真实状态：往后还有布局体检等着，不能让它们看见自检摆出来的假场景。
            model.ApplySnapshotForSelfTest(Core.Pipeline.EngineSnapshot.Stopped);
            Relayout(model, window);
        }

        bad.AddRange(CheckQuotaSurvivesStop(model, dash, window));

        if (bad.Count == 0)
        {
            steps.Add($"容量卡片核对通过：{cases.Length} 种情形下卡片的显隐、变红与进度条"
                + "（含已用超预算时夹在 100%）全部一致，且引擎停止后卡片不会消失。");
            return 0;
        }

        steps.Add($"容量卡片核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 引擎停止后，配了预算的容量卡片<b>必须还在</b>。
    ///
    /// 这一格补的是上面那批测不到的地方：那批直接把容量摆进快照，
    /// 而真实的停止快照里容量四个字段全是 0
    /// （<see cref="Core.Pipeline.EngineSnapshot.Stopped"/>），
    /// 卡片当初就是这么整张消失的。这里走一遍
    /// <see cref="Core.Pipeline.EngineSnapshot.QuotaSourceFor"/> ——
    /// 界面停机时用的正是它。
    ///
    /// 容量账是<b>现编</b>的，不去扫用户真实的云盘目录：自检是诊断命令，
    /// 跑一趟不该依赖用户当下的磁盘内容，否则结论会随目录里有几个包而变。
    /// </summary>
    private static List<string> CheckQuotaSurvivesStop(
        MainViewModel model,
        Views.DashboardPage dash,
        Views.MainWindow window)
    {
        List<string> bad = [];
        const long gb = 1024L * 1024 * 1024;

        Core.Storage.CloudQuotaStatus saved = new(
            Configured: true,
            QuotaBytes: 100 * gb,
            UsedBytes: 40 * gb,
            FreeBytes: 60 * gb,
            DiskLimited: false);

        try
        {
            Core.Pipeline.EngineSnapshot stopped = Core.Pipeline.EngineSnapshot.Stopped;

            // 前提先钉住：停止快照本来就不该带容量。哪天它带上了，
            // 这一格就测不到东西了，得有人知道。
            if (stopped.CloudQuotaConfigured)
            {
                bad.Add("前提变了：停止快照里居然带着容量，这一格已经测不到停机消失那个毛病了");
            }

            model.ApplySnapshotForSelfTest(
                Core.Pipeline.EngineSnapshot.QuotaSourceFor(stopped, saved, savedWarnBytes: 10 * gb));

            model.SelectedPage = 0;
            Settle(window);

            if (dash.CloudQuotaCard.Visibility != Visibility.Visible)
            {
                bad.Add("引擎停止后卡片消失了 —— 用户配了 100 GB 预算却什么都看不到");
            }

            if (!model.CloudQuotaConfigured)
            {
                bad.Add("引擎停止后 CloudQuotaConfigured 是 false，应当是 true");
            }

            if (Math.Abs(model.CloudUsedPercent - 40) > 0.01)
            {
                bad.Add($"引擎停止后进度条 {model.CloudUsedPercent:0.##}%，应当是 40%");
            }

            if (model.CloudQuotaLow)
            {
                bad.Add("引擎停止后卡片变红了 —— 剩余 60 GB 还在警戒线 10 GB 之上");
            }
        }
        finally
        {
            model.ApplySnapshotForSelfTest(Core.Pipeline.EngineSnapshot.Stopped);
            Relayout(model, window);
        }

        return bad;
    }

    /// <summary>
    /// 一格场景里所有跟云盘模式有关的显示必须<b>互相一致</b>。
    ///
    /// 逐个查而不是只查一个：当初的 bug 正是"按钮说 OneDrive、步骤列表却只有 4 步"，
    /// 只查一处的话另一处照样能悄悄说错话。界面上那两个控件也一起查 ——
    /// 属性对了但 XAML 没绑上去，用户看到的还是旧文字。
    /// </summary>
    private static List<string> CheckShownTarget(
        MainViewModel model,
        Views.DashboardPage dash,
        string label,
        Core.Configuration.CloudTarget shown,
        Core.Configuration.CloudTarget saved,
        bool pending)
    {
        List<string> bad = [];

        bool wantOneDrive = shown == Core.Configuration.CloudTarget.OneDrive;
        string wantLabel = Core.Configuration.CloudTargetText.PathLabel(shown);
        string wantNoun = Core.Configuration.CloudTargetText.DeliverNoun(shown);
        int wantSteps = PipelineStepMap.Build(shown).Count;

        if (model.IsOneDriveMode != wantOneDrive)
        {
            bad.Add($"{label}：IsOneDriveMode 是 {model.IsOneDriveMode}，应当是 {wantOneDrive}");
        }

        // 查"里面有没有这种模式的目录称呼"而不是整句相等：措辞（空格、动词）以后还可能改，
        // 但两种模式的称呼「OneDrive 目录」与「指定目录」互不包含，足够分辨模式对不对。
        if (!model.OpenCloudFolderText.Contains(wantLabel, StringComparison.Ordinal))
        {
            bad.Add($"{label}：按钮文字是「{model.OpenCloudFolderText}」，里面应当出现「{wantLabel}」");
        }

        // 界面上那个按钮必须和属性一字不差 —— 不然就是 XAML 没绑（或者绑到别处去了）。
        if (dash.OpenCloudFolderButton.Content as string is not { } rendered
            || !string.Equals(rendered, model.OpenCloudFolderText, StringComparison.Ordinal))
        {
            bad.Add($"{label}：界面上那个按钮显示的是「{dash.OpenCloudFolderButton.Content}」，"
                + $"属性值却是「{model.OpenCloudFolderText}」（XAML 没绑 OpenCloudFolderText）");
        }

        if (!string.Equals(model.DeliverNoun, wantNoun, StringComparison.Ordinal))
        {
            bad.Add($"{label}：送达措辞是「{model.DeliverNoun}」，应当是「{wantNoun}」");
        }

        if (model.PipelineSteps.Count != wantSteps)
        {
            bad.Add($"{label}：步骤列表 {model.PipelineSteps.Count} 步，应当是 {wantSteps} 步（没有跟着重建）");
        }

        if (model.CloudTargetPending != pending)
        {
            bad.Add($"{label}：CloudTargetPending 是 {model.CloudTargetPending}，应当是 {pending}");
        }

        bool visible = dash.CloudTargetPendingHint.Visibility == Visibility.Visible;

        if (visible != pending)
        {
            bad.Add($"{label}：待切换提示{(visible ? "出现了" : "没出现")}，"
                + $"应当{(pending ? "出现" : "不出现")}");
        }

        if (!pending)
        {
            return bad;
        }

        // 提示必须把三件事都说清：保存成了哪种、引擎在按哪种跑、什么时候会自动切。
        string hint = dash.CloudTargetPendingHint.Text;
        string savedName = Core.Configuration.CloudTargetText.ShortName(saved);
        string engineName = Core.Configuration.CloudTargetText.ShortName(shown);

        if (!hint.Contains(savedName, StringComparison.Ordinal))
        {
            bad.Add($"{label}：提示里没说保存成了「{savedName}」（原文：{hint}）");
        }

        if (!hint.Contains(engineName, StringComparison.Ordinal))
        {
            bad.Add($"{label}：提示里没说引擎还在按「{engineName}」跑（原文：{hint}）");
        }

        if (!hint.Contains("停止", StringComparison.Ordinal))
        {
            bad.Add($"{label}：提示里没有「停止引擎后自动切换」这句承诺（原文：{hint}）");
        }

        return bad;
    }

    /// <summary>
    /// 纯逻辑那半：阶段 → 下标的映射必须闭合，且和说明文字互补。
    ///
    /// 三件事都会让界面静默地说错话：
    /// <list type="bullet">
    ///   <item>下标越界（比如普通目录只有 4 步却映射到第 5 步）→ 整列一个高亮都没有；</item>
    ///   <item>某一步没有任何阶段能点亮 → 那一行永远灰着，看起来像永远不会发生；</item>
    ///   <item>有下标又有说明、或者两个都没有 → 要么"正在进行"和"引擎未启动"同时出现，
    ///         要么一片空白的沉默。两者必须恰好互补。</item>
    /// </list>
    ///
    /// 普通目录模式下 <see cref="EnginePhase.WaitingUpload"/> / <see cref="EnginePhase.Releasing"/>
    /// 走不到，所以它们的下标不参与那种模式的核对 —— 但仍然要求 OneDrive 模式下能点亮。
    /// </summary>
    private static List<string> CheckStepMapping()
    {
        List<string> bad = [];

        (Core.Configuration.CloudTarget Target, string Label, EnginePhase[] Skip)[] modes =
        [
            (Core.Configuration.CloudTarget.OneDrive, "OneDrive 模式", []),
            (Core.Configuration.CloudTarget.Folder, "普通目录模式",
                [EnginePhase.WaitingUpload, EnginePhase.Releasing]),
        ];

        foreach ((Core.Configuration.CloudTarget target, string label, EnginePhase[] skip) in modes)
        {
            List<PipelineStepView> built = PipelineStepMap.Build(target);
            HashSet<int> reachable = [];

            if (built.Count == 0)
            {
                bad.Add($"{label}：一步都没有");
                continue;
            }

            foreach (PipelineStepView step in built)
            {
                if (step.Title.Length == 0 || step.Hint.Length == 0 || step.Ordinal.Length == 0)
                {
                    bad.Add($"{label}：有一步的序号 / 标题 / 说明是空的");
                }
            }

            foreach (EnginePhase phase in Enum.GetValues<EnginePhase>())
            {
                int index = PipelineStepMap.ActiveIndex(phase);
                bool explained = PipelineStepMap.Note(phase).Length > 0;

                if ((index < 0) != explained)
                {
                    bad.Add(index < 0
                        ? $"阶段 {phase} 不点亮任何步骤，却也没有说明文字（界面上是一片沉默的空白）"
                        : $"阶段 {phase} 既点亮了第 {index + 1} 步、又带着说明文字（两句话互相矛盾）");
                }

                if (index < 0 || skip.Contains(phase))
                {
                    continue;
                }

                if (index >= built.Count)
                {
                    bad.Add($"{label}：阶段 {phase} 映射到第 {index + 1} 步，而这种模式只有 {built.Count} 步");
                    continue;
                }

                reachable.Add(index);
            }

            for (int i = 0; i < built.Count; i++)
            {
                if (!reachable.Contains(i))
                {
                    bad.Add($"{label}：第 {i + 1} 步「{built[i].Title}」没有任何阶段能点亮它");
                }
            }
        }

        // 互补关系那条会在两种模式里各查一遍，去重后再报，否则同一处缺陷计两次。
        return [.. bad.Distinct()];
    }

    /// <summary>
    /// 渲染那半：逐个点亮，量徽章。
    ///
    /// 每点亮一步都重新走一遍布局并重新收集可视树 —— 只测第一步的话，
    /// "第 6 步的徽章被卡片底边裁掉了"这种只在某一行发生的问题就漏了。
    /// 配色只在第一步量一次（同一个样式，量六遍是同一个答案）。
    /// </summary>
    private static List<string> CheckBadge(
        MainViewModel model,
        Views.MainWindow window,
        string themeLabel,
        List<string> steps)
    {
        List<string> bad = [];

        model.SelectedPage = 0;
        Settle(window);

        int total = model.PipelineSteps.Count;

        if (total == 0)
        {
            bad.Add($"{themeLabel}：步骤列表是空的，「执行到哪一步了」那块什么都不会显示");
            return bad;
        }

        for (int i = 0; i < total; i++)
        {
            Light(model, i);
            Settle(window);

            List<(TextBlock Block, Rect Box)> texts = [];
            List<(Control List, Rect Box)> lists = [];

            Collect(window, window, texts, lists, insideInput: false, clip: new Rect(0, 0, TestWidth, TestHeight));

            List<(TextBlock Block, Rect Box)> found =
                [.. texts.Where(t => t.Block.Text.Trim() == BadgeText)];

            if (found.Count != 1)
            {
                bad.Add($"{themeLabel}：点亮第 {i + 1} 步之后，界面上有 {found.Count} 个可见的「{BadgeText}」" +
                        "（应当正好 1 个）");
                continue;
            }

            (TextBlock block, Rect box) = found[0];

            // 徽章文字 11 号字四个汉字，怎么算都不该窄于 24 像素 ——
            // 低于这个数说明它被挤成了一条缝或者被祖先裁掉了大半，等于没显示。
            if (box.Width < 24 || box.Height < 10)
            {
                bad.Add($"{themeLabel}：第 {i + 1} 步的「{BadgeText}」只露出 {box.Width:0}×{box.Height:0} 像素" +
                        $"，等于看不见。{Chain(block, window)}");
            }

            if (i == 0)
            {
                bad.AddRange(CheckBadgeColors(block, themeLabel, steps));
            }
        }

        // 留第一步亮着交给后面的布局体检：徽章要是压住了旁边的标题，那道闸门会当场报出来。
        Light(model, 0);
        Settle(window);

        steps.Add($"「{themeLabel}」下 {total} 个步骤逐个点亮，「{BadgeText}」徽章每次都只出现一个。");
        return bad;
    }

    /// <summary>只点亮第 <paramref name="index"/> 步。引擎停着不会来覆盖，所以直接改就行。</summary>
    private static void Light(MainViewModel model, int index)
    {
        for (int i = 0; i < model.PipelineSteps.Count; i++)
        {
            model.PipelineSteps[i].IsActive = i == index;
            model.PipelineSteps[i].IsDone = i < index;
        }
    }

    /// <summary>
    /// 徽章的配色判据 —— 量的是<b>实际渲染出来的画刷</b>，不是 App.xaml 里写的那个值。
    ///
    /// 两者会不一样：样式里的 Setter 可能被别的 Trigger 盖掉、画刷可能被 Freeze 在
    /// 上一个主题的颜色上（这正是 <see cref="CheckPalette"/> 存在的原因）。
    /// 从可视树上的元素读回来才是用户眼睛看到的东西。
    ///
    /// 底色的参照物由 <see cref="SurfaceBehind"/> 算出来：徽章是压在卡片上的，
    /// 跟窗口背景比等于比错了对象。
    /// </summary>
    private static List<string> CheckBadgeColors(
        TextBlock block,
        string themeLabel,
        List<string> steps)
    {
        List<string> bad = [];

        Border? pill = Ancestors(block).OfType<Border>().FirstOrDefault(b => Solid(b.Background) is not null);

        System.Windows.Media.Color? ink = Solid(block.Foreground);
        System.Windows.Media.Color? fill = pill is null ? null : Solid(pill.Background);

        if (ink is null || fill is null)
        {
            bad.Add($"{themeLabel}：量不到徽章配色（文字画刷{(ink is null ? "不是纯色或全透明" : "正常")}、" +
                    $"底色{(fill is null ? "找不到有实底的外层 Border" : "正常")}）");
            return bad;
        }

        double readable = Contrast(ink.Value, fill.Value);

        if (readable < MinBadgeContrast)
        {
            bad.Add($"{themeLabel}：徽章文字 {Hex(ink.Value)} 压在底色 {Hex(fill.Value)} 上只有 " +
                    $"{readable:0.0}:1 对比度，低于 {MinBadgeContrast:0.0}:1，字看不清");
        }

        (System.Windows.Media.Color Color, string Trail) behind = SurfaceBehind(pill!);

        double apart = Distance(fill.Value, behind.Color);

        if (apart < MinBadgeSeparation)
        {
            bad.Add($"{themeLabel}：徽章底色 {Hex(fill.Value)} 和背后那块面板 {Hex(behind.Color)} 的 RGB 距离" +
                    $"只有 {apart:0}（下限 {MinBadgeSeparation:0}），徽章会融进背景里");
        }

        steps.Add($"「{themeLabel}」徽章：文字 {Hex(ink.Value)} / 底色 {Hex(fill.Value)} " +
                  $"对比度 {readable:0.0}:1，与背后面板 {Hex(behind.Color)} 相距 {apart:0}" +
                  $"（叠色 {behind.Trail}）。");

        bad.AddRange(CheckAccentClash(fill.Value, themeLabel, steps));
        return bad;
    }

    /// <summary>
    /// 徽章底色和强调色撞不撞。
    ///
    /// 用户的要求是"背景色需要和当前主题色进行区分"，而强调色恰恰是用户自己能改的东西 ——
    /// 所以这里只在<b>近到肉眼分不出</b>时才算缺陷（见 <see cref="AccentClashDistance"/>），
    /// 其余情况把最近距离写进报告就够了。真正该硬卡的是"和背后面板区分得开"，那条在上面。
    /// </summary>
    private static List<string> CheckAccentClash(
        System.Windows.Media.Color fill,
        string themeLabel,
        List<string> steps)
    {
        if (Application.Current is not { } app)
        {
            return [];
        }

        List<(string Key, System.Windows.Media.Color Color, double Apart)> refs = [];

        foreach (string key in AccentResourceKeys)
        {
            System.Windows.Media.Color? found = app.TryFindResource(key) switch
            {
                System.Windows.Media.Color color => color,
                SolidColorBrush brush => Solid(brush),
                _ => null,
            };

            if (found is not null)
            {
                refs.Add((key, found.Value, Distance(fill, found.Value)));
            }
        }

        if (refs.Count == 0)
        {
            steps.Add($"「{themeLabel}」强调色对照跳过：应用资源里没找到 WPF-UI 的强调色键。");
            return [];
        }

        (string nearestKey, System.Windows.Media.Color nearest, double apart) = refs.MinBy(r => r.Apart);

        steps.Add($"「{themeLabel}」徽章底色与最接近的强调色（{nearestKey} = {Hex(nearest)}）相距 {apart:0}。");

        return apart < AccentClashDistance
            ? [$"{themeLabel}：徽章底色 {Hex(fill)} 和强调色 {nearestKey}（{Hex(nearest)}）几乎一模一样" +
               $"（相距 {apart:0}），徽章会融进选中态和主按钮里"]
            : [];
    }

    // ==================================================================
    //  配色与可视树的小工具
    // ==================================================================

    /// <summary>纯色且不全透明才算量到了颜色；渐变、图片、null、全透明都返回 null。</summary>
    private static System.Windows.Media.Color? Solid(Brush? brush) =>
        brush is SolidColorBrush { Color.A: > 0 } solid ? solid.Color : null;

    /// <summary>这个元素自己刷的底色。三种控件基类各有一个 Background，没有共同基类可用。</summary>
    private static Brush? BackgroundOf(DependencyObject node) => node switch
    {
        Border border => border.Background,
        Panel panel => panel.Background,
        Control control => control.Background,
        _ => null,
    };

    /// <summary>从父级开始往上数（<b>不含</b>自己），一直到可视树的根。</summary>
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (DependencyObject? cursor = VisualTreeHelper.GetParent(node);
             cursor is not null;
             cursor = VisualTreeHelper.GetParent(cursor))
        {
            yield return cursor;
        }
    }

    /// <summary>
    /// 徽章背后<b>实际</b>是什么颜色。
    ///
    /// 不能只找"第一块有底色的面板"就完事：Fluent 的卡片底色是半透明的
    /// （<c>CardBackgroundFillColorDefault</c> 深浅两套都是低透明度的白），
    /// 直接拿它的 RGB 会得到 <c>#FFFFFF</c> —— 深色主题下卡片明明是深灰的，
    /// 却报成纯白，"徽章和背景区分得开吗"这个判断就完全建立在一个假数上。
    ///
    /// 所以一路往上把每层自己刷的底色收集下来，遇到不透明的那层就停（再往外的看不见了），
    /// 然后从最外层往里按 <c>source-over</c> 逐层合成到窗口底色上。
    /// 得到的才是人眼在那块像素上看到的颜色。
    /// </summary>
    private static (System.Windows.Media.Color Color, string Trail) SurfaceBehind(DependencyObject pill)
    {
        List<System.Windows.Media.Color> layers = [];

        foreach (DependencyObject node in Ancestors(pill))
        {
            if (BackgroundOf(node) is not SolidColorBrush { Color.A: > 0 } brush)
            {
                continue;
            }

            layers.Add(brush.Color);

            if (brush.Color.A == 255)
            {
                break;
            }
        }

        layers.Reverse();

        System.Windows.Media.Color surface = BaseSurface();
        List<string> trail = [Hex(surface)];

        foreach (System.Windows.Media.Color layer in layers)
        {
            surface = Over(layer, surface);
            trail.Add($"{Hex(layer)}@{layer.A * 100 / 255}%");
        }

        return (surface, string.Join(" ← ", trail) + $" = {Hex(surface)}");
    }

    /// <summary>
    /// 窗口那层底色，合成时的最底下一层。
    ///
    /// 优先问主题要（<c>ApplicationBackgroundColor</c>），取不到再退回 Mica 近似值 ——
    /// 云母背景是系统按桌面壁纸算的，程序里读不到真值，而 Fluent 那两个常数
    /// 就是它在深浅两套下的中位近似，用来判"徽章融不融进背景"足够了。
    /// </summary>
    private static System.Windows.Media.Color BaseSurface()
    {
        if (Application.Current is { } app)
        {
            foreach (string key in (string[])["ApplicationBackgroundColor", "SolidBackgroundFillColorBase"])
            {
                switch (app.TryFindResource(key))
                {
                    case System.Windows.Media.Color color when color.A == 255:
                        return color;
                    case SolidColorBrush { Color.A: 255 } brush:
                        return brush.Color;
                }
            }
        }

        return Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Light
            ? System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3)
            : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20);
    }

    /// <summary>把 <paramref name="src"/> 按自己的 alpha 压在不透明的 <paramref name="dst"/> 上。</summary>
    private static System.Windows.Media.Color Over(
        System.Windows.Media.Color src, System.Windows.Media.Color dst)
    {
        double a = src.A / 255.0;
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round((src.R * a) + (dst.R * (1 - a))),
            (byte)Math.Round((src.G * a) + (dst.G * (1 - a))),
            (byte)Math.Round((src.B * a) + (dst.B * (1 - a))));
    }

    /// <summary>WCAG 2.1 对比度。亮度公式借 Core 里那份，两处算法不该各写一遍。</summary>
    private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        double la = Core.Configuration.AccentColorSpec.RelativeLuminance(a.R, a.G, a.B);
        double lb = Core.Configuration.AccentColorSpec.RelativeLuminance(b.R, b.G, b.B);

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>欧氏 RGB 距离。用来判"能不能区分"，理由见 <see cref="MinBadgeSeparation"/>。</summary>
    private static double Distance(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;

        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    /// <summary>报告里写十六进制，比 "sc#0.1,0.2,0.3" 好认。</summary>
    private static string Hex(System.Windows.Media.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// 外观那几个字段<b>不能把设置页标成"未保存"</b>。
    ///
    /// 它们自己就落盘了（<c>EngineHost.SaveAppearance</c>），从来没有"未保存"这个状态。
    /// 一旦被标成脏，<c>MainViewModel.Start</c> 里那句"有未保存改动就先保存"就会被激活 ——
    /// 于是"点一下标题栏的深浅切换，再点开始"会把设置页上打了一半的字段一起提交。
    /// 这个后果隔了两个文件，光看 setter 完全想不到，所以在这里钉住。
    ///
    /// 用独立的 <see cref="SettingsViewModel"/> 探针，不碰界面上那个 ——
    /// 走界面那个会触发即时落盘，把用户真实的主题改掉；自检是诊断命令，不该有副作用。
    /// 顺带做一次反向对照：普通设置项必须<b>仍然</b>标脏，不然"保存"提示就永远不出现了。
    /// </summary>
    private static int CheckAppearanceNotDirty(List<string> steps)
    {
        List<string> bad = [];

        SettingsViewModel probe = new();
        ChoiceItem<Core.Configuration.ThemeMode>? other =
            probe.Themes.FirstOrDefault(t => t.Value != probe.ThemeChoice.Value);

        if (other is null)
        {
            bad.Add("主题选项少于两项，没法验证");
        }
        else
        {
            probe.ThemeChoice = other;

            if (probe.IsDirty)
            {
                bad.Add("改主题把设置页标成了未保存（点开始会连带提交没填完的字段）");
            }
        }

        // 每个外观字段单独一个探针：合在一起测的话，只要有一个不标脏就掩盖了另外几个。
        (string Label, Action<SettingsViewModel> Change)[] appearance =
        [
            ("强调色", p => p.AccentColor = "#C4314B"),
            ("背景图路径", p => p.BackgroundImagePath = @"D:\pic\wall.png"),
            ("背景图模糊", p => p.BackgroundBlur = 42),
            ("背景图不透明度", p => p.BackgroundOpacity = 66),
        ];

        foreach ((string label, Action<SettingsViewModel> change) in appearance)
        {
            SettingsViewModel one = new();
            change(one);

            if (one.IsDirty)
            {
                bad.Add($"改{label}把设置页标成了未保存（同上）");
            }
        }

        probe = new() { ArchivePrefix = "SelfTestPrefix" };

        if (!probe.IsDirty)
        {
            bad.Add("改普通设置项没有标成未保存（保存提示不会出现，改动会被静默丢掉）");
        }

        if (bad.Count == 0)
        {
            steps.Add("外观脏标记核对：外观四项都不标脏、普通设置项照常标脏。");
            return 0;
        }

        steps.Add($"外观脏标记核对失败 {bad.Count} 项：{string.Join("；", bad)}");
        return bad.Count;
    }

    /// <summary>
    /// 设置页里大半控件默认是折叠的（每日时段、日期范围、各渠道自己的字段）。
    /// 挨个打开一遍，否则这些绑定一次也不会被求值。
    /// </summary>
    private static void ExerciseSettings(
        MainViewModel model,
        Views.MainWindow window,
        List<string> steps,
        BindingTraceListener listener)
    {
        model.SelectedPage = 4;
        SettingsViewModel settings = model.Settings;

        settings.AllDay = false;
        settings.LimitDates = true;
        settings.EffectiveFrom = DateTime.Today;
        settings.EffectiveTo = DateTime.Today.AddDays(30);
        settings.DailyStartText = "22:00";
        settings.DailyEndText = "06:00";
        Settle(window);
        steps.Add($"设置页：日期范围与跨夜时段已展开，累计错误 {listener.ErrorCount}。");

        foreach (ChoiceItem<Core.Notifications.NotifierKind> channel in settings.NotifyChannels)
        {
            settings.NotifyChannel = channel;
            Settle(window);
            steps.Add($"设置页：通知渠道「{channel.Text}」已展开，累计错误 {listener.ErrorCount}。");
        }

        // 压缩级别的每一档都有自己的说明文字，顺手全过一遍。
        for (int level = 0; level <= 9; level++)
        {
            settings.CompressionLevel = level;
        }

        Settle(window);
        steps.Add($"设置页：压缩级别 0–9 说明文字已求值，累计错误 {listener.ErrorCount}。");
    }

    /// <summary>日志页那个列表的名字。核对显示顺序要靠它取到界面上真正排在第一位的那一项。</summary>
    private const string LogListName = "LogList";

    /// <summary>日志页在导航里的序号（MainWindow.xaml 里 ConverterParameter=3 那一页）。</summary>
    private const int LogPageIndex = 3;

    /// <summary>
    /// 「最新的在最上面」这条要求，从默认值一路查到界面上真正的显示顺序。
    ///
    /// 分四步，每一步都能独立坏掉：
    /// <list type="number">
    ///   <item>默认就得是倒序（要求原文是「且默认选择」）；</item>
    ///   <item>集合里的时间戳必须严格递减 —— 查的是 <c>AppendLog</c> 有没有插错那一头；</item>
    ///   <item>关掉开关后必须变成严格递增 —— 查的是那个 setter 有没有忘了把<b>已有的行</b>翻过来。
    ///         只管后续新增的话，列表会变成「前半段顺着、后半段倒着」，比两种排法都难读，
    ///         而且不报任何错；</item>
    ///   <item>开回来之后，<b>界面上</b>第 0 项得是时间最新那条 —— 前三步查的都是集合，
    ///         集合对了不等于显示对了：<c>ItemsSource</c> 上挂一个带排序的视图就能把顺序再翻一遍。</item>
    /// </list>
    /// 样例行的时间戳是 <see cref="InjectSamples"/> 特意排开的，否则「严格递减」无从判断。
    /// </summary>
    private static int CheckLogOrder(MainViewModel model, Views.MainWindow window, List<string> steps)
    {
        model.SelectedPage = LogPageIndex;
        Settle(window);

        if (Descendants(window).OfType<ListBox>().FirstOrDefault(b => b.Name == LogListName) is not { } list)
        {
            steps.Add($"检查未完成：日志页里找不到名为 {LogListName} 的列表，无法核对显示顺序。");
            return 1;
        }

        if (model.LogLines.Count < 2)
        {
            steps.Add($"检查未完成：日志样例行只有 {model.LogLines.Count} 条，判断不出顺序。");
            return 1;
        }

        int defects = 0;

        if (!model.NewestLogFirst)
        {
            steps.Add("界面缺陷：日志默认不是倒序（NewestLogFirst 的初值应当是 true）。");
            defects++;
        }

        defects += CheckLogDirection(model, newestFirst: true, "默认（倒序）的", steps);

        model.NewestLogFirst = false;
        Settle(window);
        defects += CheckLogDirection(model, newestFirst: false, "切成正序后", steps);

        model.NewestLogFirst = true;
        Settle(window);
        defects += CheckLogDirection(model, newestFirst: true, "切回倒序后", steps);

        defects += CheckLogTopItem(model, list, steps);

        if (defects == 0)
        {
            steps.Add($"日志顺序核对通过：默认倒序，{model.LogLines.Count} 行时间戳严格递减，" +
                      "开关能把已有的行整体翻向，界面上第一项就是时间最新的那条。");
        }

        return defects;
    }

    /// <summary>核对整份列表的时间戳方向。第一处排错就返回，报出行号和两边的时间。</summary>
    private static int CheckLogDirection(
        MainViewModel model,
        bool newestFirst,
        string label,
        List<string> steps)
    {
        for (int i = 1; i < model.LogLines.Count; i++)
        {
            DateTimeOffset previous = model.LogLines[i - 1].TimestampLocal;
            DateTimeOffset current = model.LogLines[i].TimestampLocal;

            if (newestFirst ? current < previous : current > previous)
            {
                continue;
            }

            steps.Add($"界面缺陷：{label}日志第 {i} 行的时间 {current:HH:mm:ss} 相对上一行 " +
                      $"{previous:HH:mm:ss} 排错了方向（应当{(newestFirst ? "越往下越旧" : "越往下越新")}）。");
            return 1;
        }

        return 0;
    }

    /// <summary>界面上排在第一位的那一项，必须就是时间最新的那条记录。</summary>
    private static int CheckLogTopItem(MainViewModel model, ListBox list, List<string> steps)
    {
        if (list.Items.Count == 0)
        {
            steps.Add("检查未完成：日志列表的 Items 是空的，取不到显示顺序。");
            return 1;
        }

        DateTimeOffset newest = model.LogLines.Max(r => r.TimestampLocal);
        object? first = list.Items[0];

        if (first is LogRecord shown && shown.TimestampLocal == newest)
        {
            return 0;
        }

        string firstText = first is LogRecord record
            ? record.TimestampLocal.ToString("HH:mm:ss")
            : first?.GetType().Name ?? "null";

        steps.Add($"界面缺陷：日志列表显示的第一项不是最新那条（第一项是 {firstText}，" +
                  $"最新的是 {newest:HH:mm:ss}）。");
        return 1;
    }

    private static void InjectSamples(MainViewModel model)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        model.TrackedFiles.Add(new TrackedFileView(
            @"D:\Camera\IMG_0001.MOV", "IMG_0001.MOV", 1_234_567_890, TrackedFileState.Observing, now, 1));
        model.TrackedFiles.Add(new TrackedFileView(
            @"D:\Camera\IMG_0002.MOV", "IMG_0002.MOV", 987_654_321, TrackedFileState.Ready, now, 2));
        model.TrackedFiles.Add(new TrackedFileView(
            @"D:\Camera\locked.psd", "locked.psd", 0, TrackedFileState.Unreadable, now, 0));

        model.BatchFiles.Add(new BatchFileView(
            @"D:\Camera\IMG_0002.MOV", "IMG_0002.MOV", 987_654_321));

        model.PendingUploads.Add(new PendingUploadView(
            @"C:\ZipTemp\Backup_20260901_2312.7z", "Backup_20260901_2312.7z",
            555_000_000, 3, now, true, 7));

        model.Retries.Add(new RetryView(
            "retry-1", 4, 2, now.AddMinutes(2), "7za 退出码 2：无法创建输出文件"));

        model.Quarantines.Add(new QuarantineView(
            "quarantine-1", 2, 2_000_000_000,
            "连续 6 次打包失败：临时目录所在卷剩余空间不足", 6, now,
            [@"D:\Camera\IMG_0003.MOV", @"D:\Camera\IMG_0004.MOV"]));

        // 四个级别各来一条，把日志行的级别配色也走到。
        //
        // 走 AppendLog 而不是直接 LogLines.Add：插入方向只有那一份实现，
        // 自检才算真的在验生产逻辑（见 MainViewModel.AppendLog 的注释）。
        //
        // 时间戳接着列表里已有的最后一条往后排 —— InjectSamples 会被调用两次，
        // 每次都从 now 起算的话第二批整批落在第一批之前，倒序核对就会误报。
        DateTimeOffset stamp = model.LogLines.Count == 0
            ? now
            : model.LogLines.Max(r => r.TimestampLocal);

        model.AppendLog(new LogRecord(stamp.AddSeconds(1), LogLevel.Debug, "自检：调试级别样例行。"));
        model.AppendLog(new LogRecord(stamp.AddSeconds(2), LogLevel.Info, "自检：普通级别样例行。"));
        model.AppendLog(new LogRecord(stamp.AddSeconds(3), LogLevel.Warn, "自检：警告级别样例行。"));
        model.AppendLog(new LogRecord(stamp.AddSeconds(4), LogLevel.Error, "自检：错误级别样例行。"));
    }

    /// <summary>
    /// 切到某一页并布局。<b>恢复页的样例行必须在切页之后才塞</b>。
    ///
    /// 别的页面的列表是被定时器按快照同步的，停掉定时器（<c>model.Dispose()</c>）
    /// 就不会再被清空；恢复页不一样 —— 它的列表是在 <c>SelectedPage</c> 的 setter 里
    /// 现扫磁盘填的（见 <c>MainViewModel.RefreshRestoreArchives</c>）。
    /// 先塞样例再切页，切页那一下就把样例全换成真实扫描结果（自检环境下基本是空的），
    /// 于是表格整片折叠，单元格模板里的绑定一条都测不到 —— 这正是这个方法要防的事。
    /// </summary>
    private static void ShowPage(MainViewModel model, Views.MainWindow window, int page, bool withSamples)
    {
        model.SelectedPage = page;

        if (withSamples && page == MainViewModel.RestorePageIndex)
        {
            InjectRestoreSamples(model);
        }

        Settle(window);
    }

    /// <summary>
    /// 恢复页的样例行。
    ///
    /// 两个列表都要有内容：包列表空着的话下面那块操作区里的按钮全是灰的，
    /// 条目表也是折叠的 —— 那样这一页等于只测了标题和几行说明文字。
    /// </summary>
    private static void InjectRestoreSamples(MainViewModel model)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        model.RestoreArchives.Clear();

        model.RestoreArchives.Add(new RestoreArchiveView(
            @"C:\ZipTemp\Backup_20260901_2312.7z", "Backup_20260901_2312.7z",
            555_000_000, now.AddHours(-2), true, "临时目录"));

        // 第二条特意没有旁挂清单：选中它才会让那条黄色警告显示出来，
        // 而那条警告本身也是一处绑定，不显示就等于没测。
        model.RestoreArchives.Add(new RestoreArchiveView(
            @"D:\OneDrive\备份\Backup_20260830_0100.7z", "Backup_20260830_0100.7z",
            2_400_000_000, now.AddDays(-3), false, "云盘目录"));

        // 选中要放在填条目之前：SelectedArchive 的 setter 会清空条目列表
        // （换了包，上一个包的条目就不作数了）。反过来写，条目会被立刻清掉。
        model.SelectedArchive = model.RestoreArchives[1];

        model.RestoreEntries.Add(new ArchiveEntry(
            @"Camera\IMG_0001.MOV", 1_234_567_890, now.AddDays(-4), false));
        model.RestoreEntries.Add(new ArchiveEntry(
            @"Camera\IMG_0002.MOV", 987_654_321, now.AddDays(-4), false));
        model.RestoreEntries.Add(new ArchiveEntry(
            @"Camera", 0, null, true));
    }

    // ==================================================================
    //  布局 + 消息泵
    // ==================================================================

    /// <summary>
    /// 强制走完一轮布局，然后把消息队列抽干到 <c>SystemIdle</c>。
    /// 绑定是在布局与渲染阶段求值的，不抽干队列就读不到错误。
    /// </summary>
    private static void Settle(Window window)
    {
        for (int round = 0; round < 3; round++)
        {
            window.Measure(new Size(TestWidth, TestHeight));
            window.Arrange(new Rect(0, 0, TestWidth, TestHeight));
            window.UpdateLayout();
            Pump();
        }
    }

    private static void Pump()
    {
        DispatcherFrame frame = new();

        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle,
            new Action(() => frame.Continue = false));

        Dispatcher.PushFrame(frame);
    }

    // ==================================================================
    //  绑定诊断源
    // ==================================================================

    private static void Attach(BindingTraceListener listener)
    {
        // Refresh 必须在设置级别之前调用，否则 WPF 用的还是启动时读到的旧配置。
        PresentationTraceSources.Refresh();

        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        PresentationTraceSources.ResourceDictionarySource.Listeners.Add(listener);
        PresentationTraceSources.ResourceDictionarySource.Switch.Level = SourceLevels.Warning;

        PresentationTraceSources.MarkupSource.Listeners.Add(listener);
        PresentationTraceSources.MarkupSource.Switch.Level = SourceLevels.Warning;
    }

    private static void Detach(BindingTraceListener listener)
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        PresentationTraceSources.ResourceDictionarySource.Listeners.Remove(listener);
        PresentationTraceSources.MarkupSource.Listeners.Remove(listener);
    }

    // ==================================================================
    //  报告
    // ==================================================================

    private static void WriteReport(
        BindingTraceListener listener,
        List<string> steps,
        int exitCode,
        IAppLogger log)
    {
        StringBuilder text = new();

        text.AppendLine($"{AppPaths.ProductName} 界面自检报告");
        text.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"版本：{App.AppVersion()}");
        text.AppendLine($"结论：{(exitCode == 0 ? "通过" : "未通过")}（退出码 {exitCode}）");
        text.AppendLine($"绑定错误 {listener.ErrorCount} 条，警告 {listener.WarningCount} 条。");
        text.AppendLine();

        text.AppendLine("---- 步骤 ----");
        foreach (string step in steps)
        {
            text.AppendLine(step);
        }

        AppendGroup(text, "---- 错误 ----", listener.Errors);
        AppendGroup(text, "---- 警告 ----", listener.Warnings);

        string report = text.ToString();

        try
        {
            File.WriteAllText(ReportPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            log.Warn($"自检报告写入失败：{ex.Message}");
        }

        // WinExe 没有控制台，写了也大概率看不见 —— 报告文件才是主渠道。
        // 但日志一定要留下，这样 --selftest 的结果事后也查得到。
        log.Info($"界面自检完成：{(exitCode == 0 ? "通过" : "未通过")}，" +
                 $"错误 {listener.ErrorCount}，警告 {listener.WarningCount}，报告：{ReportPath}");

        foreach (KeyValuePair<string, int> item in listener.Errors)
        {
            log.Error($"绑定错误 ×{item.Value}：{item.Key}");
        }
    }

    /// <summary>同一处绑定错误在每一行上都会报一次，所以按内容合并计数。</summary>
    private static void AppendGroup(
        StringBuilder text,
        string title,
        IReadOnlyDictionary<string, int> items)
    {
        text.AppendLine();
        text.AppendLine(title);

        if (items.Count == 0)
        {
            text.AppendLine("（无）");
            return;
        }

        foreach (KeyValuePair<string, int> item in items.OrderByDescending(i => i.Value))
        {
            text.AppendLine($"×{item.Value}  {item.Key}");
        }
    }

    /// <summary>
    /// 收集 WPF 绑定诊断输出。
    /// 按消息内容合并：一个写错的列绑定会在每一行上各报一次，逐条列出没有意义。
    /// </summary>
    private sealed class BindingTraceListener : TraceListener
    {
        private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _warnings = new(StringComparer.Ordinal);
        private readonly StringBuilder _pending = new();

        internal IReadOnlyDictionary<string, int> Errors => _errors;

        internal IReadOnlyDictionary<string, int> Warnings => _warnings;

        internal int ErrorCount => _errors.Values.Sum();

        internal int WarningCount => _warnings.Values.Sum();

        public override void Write(string? message)
        {
            // WPF 会把一条诊断分成好几次 Write，最后才 WriteLine。先攒起来。
            if (!string.IsNullOrEmpty(message))
            {
                _pending.Append(message);
            }
        }

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            string line = _pending.ToString().Trim();
            _pending.Clear();

            if (line.Length == 0)
            {
                return;
            }

            // 没走 TraceEvent 的裸文本按警告记 —— 归类拿不准时宁可不误判成错误。
            Count(_warnings, line);
        }

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string? message)
        {
            _pending.Clear();
            Record(eventType, message ?? string.Empty);
        }

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string? format,
            params object?[]? args)
        {
            _pending.Clear();

            string message = format is null
                ? string.Empty
                : args is null or { Length: 0 }
                    ? format
                    : SafeFormat(format, args);

            Record(eventType, message);
        }

        private void Record(TraceEventType eventType, string message)
        {
            string text = message.Trim();

            if (text.Length == 0)
            {
                return;
            }

            switch (eventType)
            {
                case TraceEventType.Critical:
                case TraceEventType.Error:
                    Count(_errors, text);
                    break;

                case TraceEventType.Warning:
                    Count(_warnings, text);
                    break;

                default:
                    break;      // Information / Verbose 是正常的绑定活动记录，不是问题
            }
        }

        private static void Count(Dictionary<string, int> bucket, string key) =>
            bucket[key] = bucket.TryGetValue(key, out int n) ? n + 1 : 1;

        private static string SafeFormat(string format, object?[] args)
        {
            try
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                // 格式串与参数不匹配也不能让自检自己挂掉。
                return $"{format} [{string.Join(", ", args)}]";
            }
        }
    }
}
