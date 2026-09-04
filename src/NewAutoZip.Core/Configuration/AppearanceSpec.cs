namespace NewAutoZip.Core.Configuration;

/// <summary>
/// 外观的完整快照：主题模式 + 强调色 + 背景图（路径 / 模糊 / 不透明度）。
///
/// <para><b>为什么要单独打成一个包</b></para>
/// 外观走的是和其它设置<b>不同的一条路</b>：改完立刻生效、立刻落盘，
/// 不参与设置页的"未保存"状态（原因见 <c>SettingsViewModel.ThemeChoice</c> 的注释）。
/// 打成一个包之后，界面层只需要传一个参数、只需要比一次"变没变"；
/// 否则每加一项外观设置就得同时改 <c>SaveAppearance</c> 的签名、改那串比较条件、
/// 改应用的地方 —— 漏掉任何一处的后果都是"选了但不生效"，而且完全静默。
///
/// <para><b>为什么放在 Core</b></para>
/// 这里的规范化（枚举越界、颜色写错、路径带空格、模糊/透明度超出区间）
/// 全是"用户能直接手改 settings.json"带来的，必须能不启动 WPF 就测干净。
/// </summary>
public sealed record AppearanceSpec(
    ThemeMode Theme,
    string AccentColor,
    string BackgroundImagePath,
    int BackgroundBlur,
    int BackgroundOpacity)
{
    /// <summary>出厂外观。取自 <see cref="AppSettings"/> 的属性初始值，只有一处定义默认值。</summary>
    public static AppearanceSpec Default { get; } = From(new AppSettings());

    /// <summary>从配置里取出外观部分，顺手规范化。</summary>
    public static AppearanceSpec From(AppSettings settings) => new AppearanceSpec(
        settings.Theme,
        settings.AccentColor,
        settings.BackgroundImagePath,
        settings.BackgroundBlur,
        settings.BackgroundOpacity)
        .Normalized();

    /// <summary>用了背景图。注意这里只看"填了路径没有"，文件在不在是界面层的事。</summary>
    public bool HasBackground => BackgroundImagePath.Length > 0;

    /// <summary>
    /// 把每一项夹回合法范围。
    /// 幂等，所以可以在任何拿不准来源的地方随手调一次。
    /// </summary>
    public AppearanceSpec Normalized() => new(
        Enum.IsDefined(Theme) ? Theme : ThemeMode.System,
        AccentColorSpec.Sanitize(AccentColor),
        (BackgroundImagePath ?? string.Empty).Trim(),
        Math.Clamp(BackgroundBlur, SettingsLimits.BackgroundBlurMin, SettingsLimits.BackgroundBlurMax),
        Math.Clamp(BackgroundOpacity, SettingsLimits.BackgroundOpacityMin, SettingsLimits.BackgroundOpacityMax));

    /// <summary>写进一份配置（就地改）。写入的是规范化后的值。</summary>
    public void WriteTo(AppSettings settings)
    {
        AppearanceSpec n = Normalized();

        settings.Theme = n.Theme;
        settings.AccentColor = n.AccentColor;
        settings.BackgroundImagePath = n.BackgroundImagePath;
        settings.BackgroundBlur = n.BackgroundBlur;
        settings.BackgroundOpacity = n.BackgroundOpacity;
    }

    /// <summary>
    /// 和某份配置里的外观是否一致 —— 用来判断"这次要不要落盘"。
    ///
    /// 路径按大小写敏感比：Windows 上路径本身不区分大小写，但这里比错的代价只是
    /// 多写一次 settings.json（下一次就相等了），而按不区分比反而会漏掉
    /// "用户只改了大小写、期待界面记住新写法"这种情况。
    /// </summary>
    public bool Matches(AppSettings settings) => Normalized() == From(settings);
}
