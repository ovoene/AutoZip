using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NewAutoZip.App.Services;

/// <summary>
/// 主题（深色 / 浅色 / 跟随系统）、强调色，以及自定义背景图。
///
/// 三者合起来就是<see cref="AppearanceSpec"/>：一次传进来、一次比较、一次应用。
/// 拆成多个参数的话，每加一项就得同时改签名、改比较条件、改应用顺序，
/// 漏一处的表现是"选了但不生效"且完全静默。
///
/// <para><b>语义色怎么跟着主题变（这一段是踩出来的，别照直觉改）</b></para>
/// App.xaml 里每个语义色有两层：一个 <c>Color</c> 资源和一只引它的
/// <c>SolidColorBrush</c>。切主题时 <see cref="SyncPalette"/> 把两层<b>都换掉</b>——
/// 颜色是赋新值，画刷是<b>整只换成一只新的</b>。
///
/// 直觉做法是"画刷实例不换，只改它的 Color，这样所有拿着这只画刷的地方一起更新"。
/// 试过，不行，而且失败是静默的：
/// <list type="number">
///   <item>资源字典里的 <c>Freezable</c> 既不在可视树也不在逻辑树上，拿不到稳定的
///         inheritance context，画刷 <c>Color="{DynamicResource XxxColor}"</c> 那条引用
///         会不会重新求值取决于此刻是谁在读、字典刚被谁换过 ——
///         实测切到浅色后 13 只画刷可能一只都没变。</item>
///   <item>就算改成在代码里直接给 <c>brush.Color</c> 赋值也救不回来：
///         <c>Style</c> / <c>DataTrigger</c> 的 <c>Setter</c> 一旦用到某只画刷，
///         这只画刷就会被 <b>Freeze</b>（样式为了跨元素共享值会冻掉可冻的值），
///         从此赋值静默失效。实测 13 个键里凡是出现在某个 Setter 里的都被冻上了，
///         其余只走转换器的一个都没冻 —— 分界线干净得没有别的解释。
///         把 Setter 里的 <c>StaticResource</c> 换成 <c>DynamicResource</c> 也不行，
///         被冻的是解析出来的那只画刷，不是那条引用。</item>
/// </list>
///
/// 所以结论反过来：<b>谁都不许依赖画刷实例的身份。</b>换实例是唯一可靠的一步 ——
/// <c>DynamicResource</c> 的全部意义就是"资源被换掉时通知到每一个引用它的元素"，
/// 这条路 WPF 保证得死死的。相应的规矩写在 App.xaml 的语义配色那一段：
/// 界面里引用这些画刷一律 <c>DynamicResource</c>，一处 <c>StaticResource</c> 就是一处
/// 停在上一个主题颜色上的死角。<c>--selftest</c> 的 <c>CheckPaletteValues</c>
/// 会把渲染出来的实际颜色和该主题应有的值逐键对一遍，钉住这条规矩。
///
/// 转换器（按键名取画刷）也随之退役了 —— 它按键名取到的是"当时"那只画刷，
/// 主题变了而 ViewModel 的键名没变，绑定不会重新求值，颜色就停住。
/// 现在状态圆点和日志行都改成 <c>Style</c> + <c>DataTrigger</c> + <c>DynamicResource</c>，
/// 走资源系统而不是走绑定值。
///
/// <para><b>系统主题从注册表读，不猜</b></para>
/// WPF-UI 的 <c>SystemTheme</c> 有 12 个值（含 Glow / CapturedMotion / Sunrise / Flow），
/// 把它们映射成"亮还是暗"是在猜。<c>AppsUseLightTheme</c> 是 Windows 自己的那一个开关，
/// 直接读它，没有猜的余地也不依赖系统语言 —— 和 OneDrive 检测不去匹配中文列名是同一个道理。
///
/// <para><b>背景图的模糊是<u>烘进位图</u>的，不是界面上的实时效果</b></para>
/// 这一段是性能上踩出来的，别改回去：
/// <list type="number">
///   <item>界面上挂一只 <c>BlurEffect</c>，等于让合成器在<b>每一帧</b>都把整窗底图重新卷积一遍。
///         有显卡时勉强撑住，落到软件渲染（虚拟机、远程桌面、老显卡驱动）就是整个程序
///         「极为卡顿」—— 连左边点一下导航都要等。</item>
///   <item>所以模糊在 <see cref="Bake"/> 里<b>一次性烘进位图</b>，界面上那层只是一张冻结的图，
///         合成器把它当普通位图画，稳态开销归零。只有半径真的变了才重烘一次。</item>
///   <item>不透明度<b>不</b>参与烘制 —— 它在合成阶段生效，本来就是免费的，
///         烘进去反而会让拖动不透明度滑块也触发重烘。</item>
/// </list>
///
/// <para><b><see cref="Apply"/> 里的贵活只在真的换主题时干</b></para>
/// <c>ApplicationThemeManager.Apply</c> 会换掉整份控件字典并重设窗口材质，
/// <c>ApplyAccent</c> 要重算一整套强调色变体，<see cref="SyncPalette"/> 要换掉 30 个资源 ——
/// 每一项都会让全窗（五个页面全在树上）的 <c>DynamicResource</c> 重新求值。
/// 拖一下模糊滑块会连着触发几十次 <c>Apply</c>，把这些活干几十遍，界面必然卡死。
/// 所以现在按「到底哪一项变了」分流：只有主题模式 / 强调色变了才走贵活，
/// 背景图那三项只走解码与烘制。<see cref="ThemeApplyCount"/> 把这条规矩钉给自检。
///
/// <para><b>强调色要在换控件字典<u>之前</u>就摊进资源</b></para>
/// 换字典的那一刻，WPF-UI 的主按钮样式会就地取走当时的强调色画刷并锁死。
/// 所以顺序是「贴强调色 → 换字典 → 再贴一遍强调色」，两遍都不能省 ——
/// 详见 <see cref="ApplySkin"/>。写反的后果是按钮颜色<b>永远慢一步</b>：
/// 资源里的值全对，只有渲染停在上一次选的颜色上。
/// </summary>
public sealed class ThemeService : IDisposable
{
    /// <summary>与 MainWindow 上的 <c>WindowBackdropType</c> 保持一致，否则切主题时背景会跳一下。</summary>
    private const WindowBackdropType Backdrop = WindowBackdropType.Mica;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>
    /// 背景图解码后的最大宽度。
    /// 一张 6000×4000 的壁纸按原分辨率留在内存里是 96 MB，而它最终只是一层被模糊过的底图 ——
    /// 缩到 2560 宽，肉眼分辨不出，内存差一个数量级。
    /// </summary>
    private const int MaxBackgroundWidth = 2560;

    /// <summary>背景图文件大小上限。挡住"手滑选了个几百 MB 的 PSD/TIFF"这种情况。</summary>
    private const long MaxBackgroundFileBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 烘模糊时的工作宽度。
    ///
    /// 比 <see cref="ReferenceWidth"/> 小一档：模糊本来就把细节抹掉了，
    /// 在小一点的位图上卷积再放大回去，肉眼分辨不出，而开销只有一半。
    /// 半径会按 <c>半径 × 工作宽度 ÷ 参照宽度</c> 一起缩，观感才对得上。
    /// </summary>
    private const int BakeWidth = 1024;

    /// <summary>
    /// 参照显示宽度：默认窗口 1240 加上背景层两边各 80 的负边距。
    ///
    /// 半径要换算就得有个参照 —— 原来那只实时 <c>BlurEffect</c> 的半径是<b>屏幕上的</b>
    /// 设备无关像素，而烘制发生在<b>位图</b>坐标里，两者差着 <c>UniformToFill</c> 的缩放。
    /// 窗口拉大之后底图放得更开，模糊也跟着显得更重一点 —— 一层底图，不值得为此每次重烘。
    /// </summary>
    private const double ReferenceWidth = 1400;

    private readonly IAppLogger _log;

    private Window? _window;

    /// <summary>已经尝试载入过的背景图路径。用来避免每拖一下模糊滑块就重新解码一次图片。</summary>
    private string _loadedBackgroundPath = string.Empty;

    /// <summary>解码后<b>没有</b>模糊的原图。烘制的输入，换半径时不用重新读盘。</summary>
    private BitmapSource? _source;

    /// <summary>当前 <see cref="Background"/> 是按哪个半径烘出来的。-1 = 还没烘过。</summary>
    private int _bakedBlur = -1;

    /// <summary>至少完整应用过一次外观。第一次必须把贵活全干一遍，不能被"没变化"挡掉。</summary>
    private bool _applied;

    private bool _watching;
    private bool _disposed;

    public ThemeService(IAppLogger log)
    {
        _log = log;

        // 系统主题变化时 SystemThemeWatcher 会自己调 ApplicationThemeManager.Apply，
        // 我们从这个事件里跟着把语义画刷和自定义强调色补上 ——
        // 那次 Apply 是 WPF-UI 发起的，不经过我们的 Apply()。
        ApplicationThemeManager.Changed += OnUnderlyingThemeChanged;
    }

    /// <summary>当前生效的整套外观（主题模式、强调色、背景图三项）。</summary>
    public AppearanceSpec Appearance { get; private set; } = AppearanceSpec.Default;

    /// <summary>用户选的模式（可能是"跟随系统"）。</summary>
    public ThemeMode Mode => Appearance.Theme;

    /// <summary>自定义强调色，空 = 跟随系统强调色。</summary>
    public string Accent => Appearance.AccentColor;

    /// <summary>
    /// 可以直接贴到界面上的背景图：<b>模糊已经烘进去了</b>，界面上不要再挂 <c>Effect</c>。
    /// 没设或载入失败时为 null，界面上就是没有背景图。
    /// </summary>
    public ImageSource? Background { get; private set; }

    /// <summary>背景图为什么没用上（人话）。null = 没有问题。设置页要把它显示出来，不能静默。</summary>
    public string? BackgroundProblem { get; private set; }

    /// <summary>背景图不透明度，0–1，直接绑到 <c>Image.Opacity</c>。</summary>
    public double BackgroundOpacity => Appearance.BackgroundOpacity / 100.0;

    /// <summary>背景图模糊半径。已经烘进 <see cref="Background"/> 了，界面不需要它，留着是为了自检与日志。</summary>
    public double BackgroundBlur => Appearance.BackgroundBlur;

    /// <summary>
    /// 换主题那套贵活跑过几次。
    ///
    /// 给 <c>--selftest</c> 用：它要证明「只拖模糊/不透明度滑块不会触发整套换主题」。
    /// 这条规矩没有任何编译期或运行时的报错来保护 —— 写回去就是又一次「整个程序极为卡顿」，
    /// 而界面看上去完全正常，只有手感变差。所以用一个计数器把它钉住。
    /// </summary>
    public int ThemeApplyCount { get; private set; }


    /// <summary>把"跟随系统"解析掉之后<b>真正生效</b>的主题。界面上的图标要看这个。</summary>
    public ApplicationTheme Effective { get; private set; } = ApplicationTheme.Dark;

    public bool IsDark => Effective != ApplicationTheme.Light;

    /// <summary>主题、强调色或背景图真的变了。界面用它刷新切换按钮的图标与文字、刷新背景层。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 绑定主窗口，用于"跟随系统"的实时监听。
    ///
    /// 监听需要窗口句柄，而开机自启动 + 最小化启动时窗口从未 Show 过、句柄还不存在，
    /// 所以这种情况下推迟到 <see cref="Window.SourceInitialized"/> 再挂 ——
    /// 不是必须现在挂上：启动时的主题已经由 <see cref="Apply"/> 读注册表定好了，
    /// 这里挂的只是"用户中途改系统设置"的实时跟随。
    /// </summary>
    public void Attach(Window window)
    {
        _window = window;

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            UpdateWatcher();
            return;
        }

        window.SourceInitialized += OnWindowSourceInitialized;
    }

    /// <summary>
    /// 应用整套外观。可以随时重复调用，幂等。
    ///
    /// <b>按「到底哪一项变了」分流</b>：换主题那套活（换控件字典、重设窗口材质、
    /// 重算强调色、换 30 个语义资源）会让全窗重新求值一遍，只有主题模式或强调色
    /// 真的变了才值得干。背景图那三项只走解码与烘制 —— 拖一下滑块会连着触发
    /// 几十次本方法，每次都把贵活干一遍的话界面就卡死了（见类注释最后一段）。
    /// </summary>
    public void Apply(AppearanceSpec spec)
    {
        if (_disposed)
        {
            return;
        }

        AppearanceSpec previous = Appearance;
        Appearance = (spec ?? AppearanceSpec.Default).Normalized();

        ApplicationTheme target = Resolve(Appearance.Theme);

        // 模式变了也要走贵活，哪怕解析出来的主题没变 —— UpdateWatcher 得跟着改，
        // 否则从"跟随系统"切成固定深色之后，系统一变还是会把用户的选择推翻。
        bool themeWork = !_applied
            || target != Effective
            || previous.Theme != Appearance.Theme
            || !string.Equals(previous.AccentColor, Appearance.AccentColor, StringComparison.Ordinal);

        if (themeWork)
        {
            ThemeApplyCount++;

            // 先落定 Effective：下面换字典时 WPF-UI 会回调 OnUnderlyingThemeChanged，
            // 落定之后那次回调会自己判重返回，不会把同一套活重入着再干一遍。
            Effective = target;

            ApplySkin(target);
            SyncPalette(target);
            UpdateWatcher();
        }

        _applied = true;

        LoadBackgroundIfNeeded();
        BakeBackgroundIfNeeded();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只改主题与强调色，背景图沿用当前那张。</summary>
    public void Apply(ThemeMode mode, string? accentHex) =>
        Apply(Appearance with { Theme = mode, AccentColor = AccentColorSpec.Sanitize(accentHex) });

    /// <summary>供设置页应用配置后调用。</summary>
    public void Apply(AppSettings settings) => Apply(AppearanceSpec.From(settings));

    /// <summary>
    /// 标题栏上那个一键切换。
    ///
    /// 跟随系统时点一下 → 切到"和当前系统相反"的固定模式，而不是原地不动 ——
    /// 用户点它就是想立刻看到变化。返回切换后的模式，由调用方存进配置。
    /// </summary>
    public ThemeMode Toggle()
    {
        ThemeMode next = IsDark ? ThemeMode.Light : ThemeMode.Dark;
        Apply(Appearance with { Theme = next });
        return next;
    }

    // ==================================================================
    //  内部
    // ==================================================================

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnWindowSourceInitialized;
        }

        UpdateWatcher();
    }

    /// <summary>只有"跟随系统"才需要监听；固定深色/浅色时必须撤掉，否则系统一变就把用户的选择推翻。</summary>
    private void UpdateWatcher()
    {
        if (_window is not { } window || new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            return;
        }

        bool shouldWatch = Mode == ThemeMode.System;

        if (shouldWatch == _watching)
        {
            return;
        }

        try
        {
            if (shouldWatch)
            {
                // updateAccents: false —— 同上，系统主题变化不该顺手把自定义皮肤冲掉。
                SystemThemeWatcher.Watch(window, Backdrop, updateAccents: false);
            }
            else
            {
                SystemThemeWatcher.UnWatch(window);
            }

            _watching = shouldWatch;
        }
        catch (Exception ex)
        {
            _log.Warn($"{(shouldWatch ? "开始" : "停止")}监听系统主题失败，跟随系统可能不会实时生效。{ex.Message}");
        }
    }

    /// <summary>
    /// WPF-UI 自己换了主题（系统主题变化触发）。
    ///
    /// 重新走一遍 <see cref="ApplySkin"/>，而不是只补一次强调色：WPF-UI 那次换字典
    /// 发生在<b>旧强调色还摊在资源里</b>的时候，主按钮当场就把旧的取走了 ——
    /// 强调色的深浅变体是按主题算的，只补资源的话按钮会停在"上一个主题算出来的"那一档
    /// （和设置页选色慢一步是同一个坑，见 <see cref="ApplySkin"/>）。
    /// 系统主题变化很罕见，为此多换一次字典完全划得来。
    /// </summary>
    private void OnUnderlyingThemeChanged(ApplicationTheme currentTheme, Color systemAccent)
    {
        if (_disposed || currentTheme == Effective)
        {
            return;
        }

        // 先落定，否则下面那次换字典的回调会重入。
        Effective = currentTheme;

        ApplySkin(currentTheme);
        SyncPalette(currentTheme);

        _log.Debug($"系统主题变化，界面已切到{(IsDark ? "深色" : "浅色")}。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 换控件字典 + 贴强调色，一次做完。
    ///
    /// <b>强调色要在换字典之前先摊进应用资源，换完再摊一遍 —— 两遍都不能省。</b>
    /// 这是踩出来的，别"整理"成一遍：
    /// <list type="number">
    ///   <item><b>换字典之前那一遍</b>是修"按钮颜色慢一步"的那一遍。
    ///         <c>ApplicationThemeManager.Apply</c> 把整份控件字典换掉时，WPF-UI 的主按钮样式
    ///         会<b>就地取走</b>当时的强调色画刷并锁进 <c>Setter</c> 里，之后再改应用资源
    ///         它不会回头看。所以原来"先换字典、后设强调色"的顺序下，选了红色按钮还是上一次的黄色，
    ///         再选绿色按钮才变红 —— 资源里的值一直是对的，只有渲染永远落后一次。</item>
    ///   <item><b>换字典之后那一遍</b>是兜底：那份新字典自己也带着一套强调色键，
    ///         少这一遍的话凡是在换字典之后才第一次求值的地方会取到主题自带的蓝色。</item>
    /// </list>
    ///
    /// <c>updateAccent: false</c>：强调色由我们自己接管，交给 WPF-UI 的话
    /// 它会拿系统强调色把用户自定义的那个盖掉。
    /// </summary>
    private void ApplySkin(ApplicationTheme target)
    {
        ApplyAccent(target);

        try
        {
            ApplicationThemeManager.Apply(target, Backdrop, updateAccent: false);
        }
        catch (Exception ex)
        {
            // 主题失败不能拖垮程序：界面难看总比起不来好。
            _log.Warn($"应用主题失败（{target}），界面维持当前配色。{ex.Message}");
        }

        ApplyAccent(target);
    }

    private void ApplyAccent(ApplicationTheme theme)
    {
        try
        {
            if (AccentColorSpec.TryParseRgb(Accent, out byte r, out byte g, out byte b))
            {
                ApplicationAccentColorManager.Apply(
                    Color.FromRgb(r, g, b),
                    theme,
                    systemGlassColor: false,
                    systemAccentColor: false);
            }
            else
            {
                ApplicationAccentColorManager.ApplySystemAccent();
            }
        }
        catch (Exception ex)
        {
            // 自定义色算不出配套的深浅变体时也不能崩，退回主题自带的强调色。
            _log.Warn($"应用强调色失败（{(Accent.Length == 0 ? "系统强调色" : Accent)}），沿用主题默认色。{ex.Message}");
        }
    }

    /// <summary>
    /// 需要的时候才重新解码背景图。
    ///
    /// 判断依据是"请求的路径和已经载入的那张不一样"，而不是每次 <see cref="Apply"/> 都解一遍 ——
    /// 拖动模糊或不透明度滑块会连着触发几十次 Apply，每次都去解码一张 4K 图片会直接卡住界面。
    ///
    /// 上一次载入失败（<see cref="Background"/> 为 null 而路径不空）时会重试：
    /// 用户很可能刚把那个文件放回去，让他为此再选一遍图说不通。
    /// </summary>
    private void LoadBackgroundIfNeeded()
    {
        string wanted = Appearance.BackgroundImagePath;

        bool samePath = string.Equals(wanted, _loadedBackgroundPath, StringComparison.Ordinal);
        bool alreadyGood = _source is not null || wanted.Length == 0;

        if (samePath && alreadyGood)
        {
            return;
        }

        _loadedBackgroundPath = wanted;
        _source = null;
        Background = null;
        _bakedBlur = -1;
        BackgroundProblem = null;

        if (wanted.Length == 0)
        {
            return;
        }

        try
        {
            _source = Decode(wanted);
            _log.Debug($"背景图已载入：{wanted}");
        }
        catch (Exception ex)
        {
            // 一张图载不进来绝不能影响备份。只记下原因，界面照常，设置页会把原因显示出来。
            BackgroundProblem = Explain(ex);
            _log.Warn($"背景图载入失败（{wanted}）：{BackgroundProblem}");
        }
    }

    /// <summary>
    /// 半径变了才重烘一次，其余时候直接沿用上一张。
    ///
    /// 拖动<b>不透明度</b>滑块也会一路走到这里，但半径没变，所以什么都不做 ——
    /// 不透明度是合成阶段的事，本来就免费。
    /// </summary>
    private void BakeBackgroundIfNeeded()
    {
        if (_source is null)
        {
            Background = null;
            _bakedBlur = -1;
            return;
        }

        int blur = Appearance.BackgroundBlur;

        if (Background is not null && _bakedBlur == blur)
        {
            return;
        }

        try
        {
            Background = Bake(_source, blur);
            _bakedBlur = blur;
        }
        catch (Exception ex)
        {
            // 烘不出来就退回原图：底图不模糊只是不好看，比没有背景图或者崩掉都好。
            Background = _source;
            _bakedBlur = 0;
            _log.Warn($"背景图模糊处理失败，改用原图。{ex.Message}");
        }
    }

    /// <summary>
    /// 把模糊<b>烘进</b>一张位图，返回冻结好、可以跨线程用的结果。
    ///
    /// 三个要点：
    /// <list type="bullet">
    ///   <item>半径 0 直接把原图还回去 —— 不该为「不模糊」白走一遍渲染，
    ///         也不该让原图凭空过一次缩放。</item>
    ///   <item>在 <see cref="BakeWidth"/> 宽的工作位图上卷积，半径按同一比例缩小。
    ///         模糊已经把细节抹掉了，放大回去看不出差别，开销却少一半。</item>
    ///   <item>裁掉四边一圈：<c>BlurEffect</c> 在位图边缘会把外面的透明色卷进来，
    ///         留着就是一圈渐隐的边。原来那只实时效果靠 <c>Margin="-80"</c> 把这圈藏到窗外，
    ///         现在直接裁掉，那个负边距只剩"给缩放留余量"这一个作用。</item>
    /// </list>
    /// </summary>
    private static ImageSource Bake(BitmapSource source, int radius)
    {
        if (radius <= 0)
        {
            return source;
        }

        double scale = Math.Min(1.0, BakeWidth / (double)source.PixelWidth);
        int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

        double baked = Math.Max(1.0, radius * width / ReferenceWidth);

        // 用 Image 而不是 DrawingVisual：Effect 是 UIElement 上的公开属性，
        // 而 DrawingVisual 那一层拿不到它。这棵元素不入可视树，量一次排一次就够。
        System.Windows.Controls.Image layer = new()
        {
            Source = source,
            Stretch = Stretch.Fill,
            Width = width,
            Height = height,
            Effect = new BlurEffect
            {
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance,
                Radius = baked,
            },
        };

        layer.Measure(new Size(width, height));
        layer.Arrange(new Rect(0, 0, width, height));

        RenderTargetBitmap rendered = new(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(layer);
        rendered.Freeze();

        int margin = (int)Math.Ceiling(baked);
        margin = Math.Min(margin, Math.Min((width - 1) / 2, (height - 1) / 2));

        if (margin <= 0)
        {
            return rendered;
        }

        CroppedBitmap cropped = new(
            rendered,
            new Int32Rect(margin, margin, width - (margin * 2), height - (margin * 2)));

        cropped.Freeze();

        return cropped;
    }

    /// <summary>
    /// 解码一张背景图。
    ///
    /// 两个要点：
    /// <list type="bullet">
    ///   <item>先整份读进内存再解码 —— 这样文件句柄立刻放开。
    ///         直接把路径交给 <c>BitmapImage</c> 会让程序一直占着这个文件，
    ///         用户随后想删掉或换掉这张图就会被"文件正在使用中"挡住。</item>
    ///   <item>超宽的图缩到 <see cref="MaxBackgroundWidth"/> —— 见那个常量的说明。</item>
    /// </list>
    ///
    /// 返回 <see cref="BitmapSource"/> 而不是 <see cref="ImageSource"/>：
    /// <see cref="Bake"/> 需要读 <c>PixelWidth</c> 来算缩放比和模糊半径，
    /// 而这两个属性只在 <c>BitmapSource</c> 上有。两个返回分支本来就都是位图。
    /// </summary>
    private static BitmapSource Decode(string path)
    {
        FileInfo info = new(path);

        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到这个文件。", path);
        }

        if (info.Length == 0)
        {
            throw new InvalidDataException("这个文件是空的。");
        }

        if (info.Length > MaxBackgroundFileBytes)
        {
            throw new InvalidDataException(
                $"文件太大（{ByteSize.Format(info.Length)}），背景图请控制在 {ByteSize.Format(MaxBackgroundFileBytes)} 以内。");
        }

        using MemoryStream stream = new(File.ReadAllBytes(path), writable: false);

        BitmapFrame frame = BitmapDecoder
            .Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad)
            .Frames[0];

        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        if (frame.PixelWidth <= MaxBackgroundWidth)
        {
            return frame;
        }

        double scale = MaxBackgroundWidth / (double)frame.PixelWidth;

        ScaleTransform shrink = new(scale, scale);
        shrink.Freeze();

        TransformedBitmap scaled = new(frame, shrink);

        if (scaled.CanFreeze)
        {
            scaled.Freeze();
        }

        return scaled;
    }

    /// <summary>把异常翻成用户看得懂的一句话。默认分支保留原始消息，别把真正的原因吞掉。</summary>
    private static string Explain(Exception ex) => ex switch
    {
        FileNotFoundException => "找不到这个文件，它可能被删掉或改名了。",
        DirectoryNotFoundException => "找不到这个文件所在的目录。",
        UnauthorizedAccessException => "没有读取这个文件的权限。",
        FileFormatException => "这不是一张有效的图片，或者文件已经损坏。",
        NotSupportedException => "认不出这个图片格式，请用 PNG、JPG、BMP 之类常见格式。",
        _ => ex.Message,
    };

    /// <summary>
    /// 把语义色改成当前主题的值：<c>*Color</c> 换值，画刷<b>换实例</b>。
    ///
    /// 为什么必须换实例而不是改 <c>brush.Color</c>，见类注释那一段 ——
    /// 简短版：进过 <c>Setter</c> 的画刷会被 Freeze，赋值静默失效；
    /// 而换实例走的是 <c>DynamicResource</c> 的通知，WPF 保证每个引用都收到。
    ///
    /// 缺键就跳过（不新建）：新建的键谁也没引用，改了没人看，
    /// 静默无效比报错更难查。<c>--selftest</c> 里的 <c>CheckPalette</c> 会把缺失当场报出来，
    /// <c>CheckPaletteValues</c> 还会逐键核对界面上真正渲染出来的颜色。
    /// </summary>
    private void SyncPalette(ApplicationTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        Palette palette = theme == ApplicationTheme.Light ? Palette.Light : Palette.Dark;

        foreach ((string key, Color color) in palette.Entries)
        {
            string colorKey = ColorKeyOf(key);

            if (app.Resources[colorKey] is Color)
            {
                app.Resources[colorKey] = color;
            }
            else
            {
                _log.Debug($"主题颜色 {colorKey} 不存在或不是 Color，已跳过。");
            }

            if (app.Resources[key] is SolidColorBrush)
            {
                app.Resources[key] = new SolidColorBrush(color);
            }
            else
            {
                _log.Debug($"语义画刷 {key} 不存在或不是 SolidColorBrush，已跳过。");
            }
        }

        // 失焦选中的语义色同时投到 SystemColors 键上。
        //
        // 导航栏和日志列表走的是 App.xaml 里的 SelectableListBoxItem，那份模板自己
        // 引用语义色，不经过这里。<b>但另一批控件没有自定义模板</b>：文本框/密码框选中的那段字、
        // DataGrid 的内建部件、以及任何将来直接扔上界面的 ListBox —— 它们在
        // IsSelected && !IsSelectionActive 这条分支上从 SystemColors.InactiveSelectionHighlightBrushKey
        // 取画刷，而那个键的系统默认值是 #FFF0F0F0（浅灰），深色主题下就是一条白亮的疤。
        // 把同名语义色复制到 SystemColors 键上，这些模板下一次 DynamicResource 取值
        // 就会拿到新色，整只换实例走的是上面同一套 DynamicResource 通知机制。
        //
        // 两边必须一致，否则同一个"失焦选中"在界面上会有两个颜色 ——
        // SelfTest.CheckListRowColors 钉住了这条。
        SyncSystemBrush(app, "SelectionInactive", SystemColors.InactiveSelectionHighlightBrushKey);
        SyncSystemBrush(app, "SelectionInactiveText", SystemColors.InactiveSelectionHighlightTextBrushKey);
    }

    private static void SyncSystemBrush(Application app, string paletteKey, object systemKey)
    {
        if (app.Resources[paletteKey] is not SolidColorBrush source)
        {
            return;
        }

        Color color = source.Color;

        if (app.Resources[systemKey] is SolidColorBrush existing && existing.Color == color)
        {
            return;
        }

        app.Resources[systemKey] = new SolidColorBrush(color);
    }

    /// <summary>画刷键 → 颜色键。约定就是加个 <c>Color</c> 后缀，App.xaml 里一一对应。</summary>
    public static string ColorKeyOf(string brushKey) => brushKey + "Color";

    /// <summary>把"跟随系统"落成一个确定的主题。</summary>
    private ApplicationTheme Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ApplicationTheme.Light,
        ThemeMode.Dark => ApplicationTheme.Dark,
        _ => ReadSystemTheme(),
    };

    /// <summary>
    /// 读 Windows 的"应用模式"开关。取不到就按深色 ——
    /// 这个程序常驻托盘，深色更不刺眼，也和 Mica 背景更搭。
    /// </summary>
    private ApplicationTheme ReadSystemTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

            if (key?.GetValue(AppsUseLightThemeValue) is int flag)
            {
                return flag == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"读系统主题设置失败，按深色处理。{ex.Message}");
        }

        return ApplicationTheme.Dark;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ApplicationThemeManager.Changed -= OnUnderlyingThemeChanged;

        if (_watching && _window is { } window)
        {
            try
            {
                SystemThemeWatcher.UnWatch(window);
            }
            catch (Exception ex)
            {
                _log.Debug($"停止监听系统主题失败。{ex.Message}");
            }

            _watching = false;
        }
    }

    /// <summary>
    /// 一套主题下的语义色。
    ///
    /// 深色那一套的值是原来 App.xaml 里硬编码的那些；浅色是新配的 ——
    /// 直接拿深色值放到白底上完全不能用：<c>MutedText #A6AEBB</c>、
    /// <c>LogInfo #D6DBE0</c> 这种浅灰在白底上基本看不见。
    /// 两套都按正文 4.5:1 的对比度挑过。
    /// </summary>
    private sealed record Palette(IReadOnlyList<(string Key, Color Color)> Entries)
    {
        public static Palette Dark { get; } = new(
        [
            ("DotOk", Rgb(0x4A, 0xDE, 0x80)),
            ("DotWarn", Rgb(0xFF, 0xC8, 0x3D)),
            ("DotError", Rgb(0xFF, 0x6B, 0x6B)),
            ("DotIdle", Rgb(0x8A, 0x93, 0xA0)),

            ("MutedText", Rgb(0xA6, 0xAE, 0xBB)),
            ("WarnText", Rgb(0xFF, 0xC8, 0x3D)),
            ("ErrorText", Rgb(0xFF, 0x8A, 0x8A)),

            ("LogDebug", Rgb(0x86, 0x8D, 0x96)),
            ("LogInfo", Rgb(0xD6, 0xDB, 0xE0)),
            ("LogWarn", Rgb(0xFF, 0xC8, 0x3D)),
            ("LogError", Rgb(0xFF, 0x8A, 0x8A)),

            ("RunningBadgeBg", Rgb(0x4C, 0x3A, 0x82)),
            ("RunningBadgeText", Rgb(0xE7, 0xDE, 0xFF)),

            // ===== 列表行：左边功能区 + 日志区共用一套（SelectableListBoxItem 模板）=====
            //
            // 固定色，不跟用户能改的强调色 —— 强调色就是主按钮的颜色，列表行跟着它走，
            // 左边功能区会和右上角那排按钮糊成一片，分不出哪个能点；一饱和更是刺眼。
            // 三个状态的底色<b>全部</b>在这里给全（普通行以前没给，于是从 WPF-UI 的
            // 基样式继承到强调色，就是那个"背景色和按钮一个色"的毛病）。
            // 色相跟着面板走，只动明度。深色面板 #2B2B2B、窗口 #202020：
            //   普通 #2A303A：离窗口 32.1（RGB 距离）；
            //   悬停 #39414D：离普通 29.6；
            //   选中 #4A5769：离悬停 39.5、离窗口 100.6；
            //   描边 #88A0BC：压在选中底上 2.73:1（WCAG）；
            //   文字 #F2F5F9：压在选中底上 6.72:1。
            // 文字用 #F2F5F9 而不是纯白：自检的 CheckRenderedNotStale 会把纯白纯黑
            // 从"另一个主题的指纹"里剔掉（两边都可能出现，认不出来），避开就多一个键被看着。
            ("ListRow", Rgb(0x2A, 0x30, 0x3A)),
            ("ListHover", Rgb(0x39, 0x41, 0x4D)),
            ("ListHoverBorder", Rgb(0x48, 0x51, 0x5F)),
            ("ListSelected", Rgb(0x4A, 0x57, 0x69)),
            ("ListSelectedBorder", Rgb(0x88, 0xA0, 0xBC)),
            ("ListSelectedText", Rgb(0xF2, 0xF5, 0xF9)),

            // 选中行失焦后（Selector.IsSelectionActive=false）的底色与文字色。
            // DataGridCell 样式上的 MultiTrigger、以及所有没套我们模板、走 WPF 默认
            // 选中机制的控件都取这两只画刷（App.xaml 里把它们钉到了 SystemColors 键上）。
            //
            // 值和上面的 ListSelected / ListSelectedText <b>完全相同</b>，这是有意的：
            // 失焦只让描边退回底色本身，底色一点不变。这样"失去焦点后底色太暗看不清"
            // 在结构上就不可能发生 —— 没有第二个更暗的底色可以掉进去。
            ("SelectionInactive", Rgb(0x4A, 0x57, 0x69)),
            ("SelectionInactiveText", Rgb(0xF2, 0xF5, 0xF9)),
        ]);

        public static Palette Light { get; } = new(
        [
            ("DotOk", Rgb(0x12, 0x83, 0x3F)),
            ("DotWarn", Rgb(0xA4, 0x5B, 0x00)),
            ("DotError", Rgb(0xC4, 0x2B, 0x1C)),
            ("DotIdle", Rgb(0x6B, 0x72, 0x80)),

            ("MutedText", Rgb(0x5A, 0x64, 0x72)),
            ("WarnText", Rgb(0x8A, 0x4B, 0x00)),
            ("ErrorText", Rgb(0xB0, 0x1E, 0x12)),

            ("LogDebug", Rgb(0x6E, 0x76, 0x81)),
            ("LogInfo", Rgb(0x1F, 0x23, 0x28)),
            ("LogWarn", Rgb(0x8A, 0x4B, 0x00)),
            ("LogError", Rgb(0xB0, 0x1E, 0x12)),

            // 浅色下这枚徽章<b>也是</b>实心紫罗兰，不是淡紫底＋深紫字。
            // 淡紫（#EADDFF）试过，不行：卡片本身就近乎纯白，两者 RGB 只差 40，
            // 徽章看上去像一小块颜色略脏的空白，"哪一步正在跑"这个唯一有用的信息就没了。
            // 实心紫离白底 267，一眼就是个药丸；白字压上去 7.7:1，比 AA 要求还宽裕。
            ("RunningBadgeBg", Rgb(0x5B, 0x3F, 0xA8)),
            ("RunningBadgeText", Rgb(0xFF, 0xFF, 0xFF)),

            // 列表行，浅色一套。为什么不跟强调色、为什么普通行也要给全，见深色那一套的注释。
            // 浅色只能往<b>深</b>的方向走 —— 白底上面没有余量了，再亮就和白纸一样。
            // 自检里的"背景"取 ApplicationBackgroundColor，浅色下实测 #FAFAFA：
            //   普通 #E4EBF6：离背景 26.9（RGB 距离），黑字压上去 17.5:1；
            //   悬停 #CFDDEF：离普通 26.2；
            //   选中 #B4CCE9：离悬停 32.5、离背景 85.5；
            //   描边 #4E7BB4：压在选中底上 2.65:1（WCAG）；
            //   文字 #12233A：压在选中底上 9.61:1。
            //
            // "底色不要太暗"这条在浅色下靠可读性兑现：9.61:1 的字，
            // 比原先 #5B9DD3 底上那 5.72:1 清楚得多，而底色本身只比背景沉两档。
            ("ListRow", Rgb(0xE4, 0xEB, 0xF6)),
            ("ListHover", Rgb(0xCF, 0xDD, 0xEF)),
            ("ListHoverBorder", Rgb(0xC2, 0xD2, 0xE8)),
            ("ListSelected", Rgb(0xB4, 0xCC, 0xE9)),
            ("ListSelectedBorder", Rgb(0x4E, 0x7B, 0xB4)),
            ("ListSelectedText", Rgb(0x12, 0x23, 0x3A)),

            // 与 ListSelected / ListSelectedText 同值，理由见深色那一套的注释：
            // 失焦不换底色，只让描边退回底色本身。
            ("SelectionInactive", Rgb(0xB4, 0xCC, 0xE9)),
            ("SelectionInactiveText", Rgb(0x12, 0x23, 0x3A)),
        ]);

        /// <summary>所有主题都必须覆盖的键。自检用它核对，漏一个就是浅色下看不见的文字。</summary>
        public static IReadOnlyList<string> Keys { get; } = [.. Dark.Entries.Select(e => e.Key)];

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    }

    /// <summary>供界面自检核对：所有语义画刷的键名。</summary>
    public static IReadOnlyList<string> PaletteKeys => Palette.Keys;

    /// <summary>
    /// 某个主题下每个键<b>应该</b>是什么颜色。
    ///
    /// 自检要拿它和界面上真正渲染出来的画刷比。只查"键在不在、类型对不对"是不够的 ——
    /// 那样查不出"切到浅色了但某个颜色还停在深色值上"，而这恰恰是最容易发生、
    /// 也最难用眼睛发现的那一类缺陷（白底上一行看不见的字，程序不报任何错）。
    /// </summary>
    public static IReadOnlyList<(string Key, Color Color)> Expected(ApplicationTheme theme) =>
        (theme == ApplicationTheme.Light ? Palette.Light : Palette.Dark).Entries;
}
