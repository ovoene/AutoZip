using NewAutoZip.Core.Configuration;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 强调色的解析与容错。
///
/// 这个字段有两个入口都不受控：设置页上的文本框（用户边打字边求值），
/// 以及用户直接手改 <c>settings.json</c>。所以一个坏值绝不能让程序起不来，
/// 也绝不能被当成有效色去算配套的深浅变体。
/// </summary>
public class AccentColorSpecTests
{
    [Theory]
    [InlineData("#0078D4", "#0078D4")]
    [InlineData("0078D4", "#0078D4")]        // 缺 # 也收：手输时最常见的漏字符
    [InlineData("#0078d4", "#0078D4")]       // 统一成大写，免得两个等价值比较出不相等
    [InlineData("  #0078D4  ", "#0078D4")]   // 复制粘贴常带空白
    [InlineData("#ABC", "#AABBCC")]          // 三位缩写展开
    [InlineData("abc", "#AABBCC")]
    [InlineData("#000", "#000000")]
    [InlineData("#FFF", "#FFFFFF")]
    public void TryNormalize_规范化各种写法(string input, string expected)
    {
        Assert.True(AccentColorSpec.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_空值合法且表示跟随系统(string? input)
    {
        // 空不是"错"，它是"用系统强调色"这个明确选项。判成错的话
        // 用户清空输入框的那一刻就会看到一条红字报错，而他什么也没做错。
        Assert.True(AccentColorSpec.TryNormalize(input, out string normalized));
        Assert.Equal(AccentColorSpec.FollowSystem, normalized);
    }

    [Theory]
    [InlineData("#FF0078D4")]    // 带 Alpha 的 8 位：强调色是不透明色，收下只会让界面莫名发灰
    [InlineData("#0078D4FF")]
    [InlineData("#12345")]       // 长度不对
    [InlineData("#1234567")]
    [InlineData("#GGHHII")]      // 不是十六进制
    [InlineData("#00 78 D4")]    // 中间的空格不算空白修剪范围
    [InlineData("rgb(0,120,212)")]
    [InlineData("蓝色")]
    [InlineData("#")]
    public void TryNormalize_拒绝无效值并给出跟随系统(string input)
    {
        Assert.False(AccentColorSpec.TryNormalize(input, out string normalized));
        Assert.Equal(AccentColorSpec.FollowSystem, normalized);
    }

    [Fact]
    public void TryNormalize_是幂等的()
    {
        // 界面上这个值会被反复规范化（属性 setter、落盘、读回、传给 ThemeService）。
        // 不幂等就会出现"存进去和读出来不一样"，进而每次启动都白写一次配置。
        Assert.True(AccentColorSpec.TryNormalize("abc", out string once));
        Assert.True(AccentColorSpec.TryNormalize(once, out string twice));
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("#GGHHII")]
    [InlineData("#FF0078D4")]
    [InlineData("垃圾值")]
    public void Sanitize_坏值退回跟随系统而不是抛异常(string input)
    {
        // 这条是反序列化的兜底：手改坏了的 settings.json 必须还能把程序启起来。
        Assert.Equal(AccentColorSpec.FollowSystem, AccentColorSpec.Sanitize(input));
    }

    [Fact]
    public void Sanitize_好值原样规范化() =>
        Assert.Equal("#AABBCC", AccentColorSpec.Sanitize(" abc "));

    [Theory]
    [InlineData("#0078D4", 0x00, 0x78, 0xD4)]
    [InlineData("0078d4", 0x00, 0x78, 0xD4)]
    [InlineData("#ABC", 0xAA, 0xBB, 0xCC)]
    [InlineData("#FFFFFF", 0xFF, 0xFF, 0xFF)]
    [InlineData("#000000", 0x00, 0x00, 0x00)]
    public void TryParseRgb_分量拆解正确(string input, byte r, byte g, byte b)
    {
        Assert.True(AccentColorSpec.TryParseRgb(input, out byte pr, out byte pg, out byte pb));
        Assert.Equal(r, pr);
        Assert.Equal(g, pg);
        Assert.Equal(b, pb);
    }

    [Theory]
    [InlineData("")]             // 跟随系统：规范化成功但没有颜色可拆，必须返回 false
    [InlineData(null)]
    [InlineData("#GGHHII")]
    public void TryParseRgb_没有具体颜色时返回假(string? input) =>
        Assert.False(AccentColorSpec.TryParseRgb(input, out _, out _, out _));

    [Fact]
    public void RelativeLuminance_按感知加权而不是三通道平均()
    {
        double green = AccentColorSpec.RelativeLuminance(0x00, 0xFF, 0x00);
        double blue = AccentColorSpec.RelativeLuminance(0x00, 0x00, 0xFF);
        double red = AccentColorSpec.RelativeLuminance(0xFF, 0x00, 0x00);

        // (R+G+B)/3 会把这三个都算成 85，于是纯绿被判成暗色、上面放白字看不清。
        // WCAG 的加权里绿最亮、蓝最暗，差着一个数量级。
        Assert.True(green > red, $"绿({green:F4}) 应比红({red:F4}) 亮");
        Assert.True(red > blue, $"红({red:F4}) 应比蓝({blue:F4}) 亮");
    }

    [Fact]
    public void RelativeLuminance_黑白两端落在_0_和_1()
    {
        Assert.Equal(0.0, AccentColorSpec.RelativeLuminance(0, 0, 0), 6);
        Assert.Equal(1.0, AccentColorSpec.RelativeLuminance(0xFF, 0xFF, 0xFF), 6);
    }

    [Theory]
    [InlineData("#000000", true)]    // 纯黑底：白字
    [InlineData("#0078D4", true)]    // 默认蓝也偏暗
    [InlineData("#FFFFFF", false)]   // 纯白底：白字等于隐身，必须换黑字
    [InlineData("#FFFF00", false)]   // 明黄 —— 这是"白字看不见"最典型的例子
    [InlineData("#00FF00", false)]   // 纯绿：naive 平均会误判成暗色
    public void PrefersLightForeground_按亮度决定文字颜色(string hex, bool expected) =>
        Assert.Equal(expected, AccentColorSpec.PrefersLightForeground(hex));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#GGHHII")]
    public void PrefersLightForeground_算不出来时按白字兜底(string? input)
    {
        // 主题默认深色，拿不到颜色时白字更可能是对的；而且这个函数不该有第三种返回。
        Assert.True(AccentColorSpec.PrefersLightForeground(input));
    }

    [Fact]
    public void Presets_每个预置色都已经是规范形式()
    {
        // 色块上显示的和存进配置的必须是同一个字符串，否则点了色块之后
        // SyncSwatchSelection 比不出相等，选中环不会亮 —— 看起来就是"点了没反应"。
        foreach ((string name, string hex) in AccentColorSpec.Presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(hex, AccentColorSpec.Sanitize(hex));
            Assert.True(AccentColorSpec.TryParseRgb(hex, out _, out _, out _), $"{name} 的 {hex} 解析失败");
        }
    }

    [Fact]
    public void Presets_没有重复色也没有空列表()
    {
        Assert.NotEmpty(AccentColorSpec.Presets);
        Assert.Equal(
            AccentColorSpec.Presets.Count,
            AccentColorSpec.Presets.Select(p => p.Hex).Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>外观字段的兜底：手改坏了的配置必须还能把程序启起来。</summary>
public class AppearanceSettingsTests
{
    [Fact]
    public void ClampToLimits_修掉越界的主题枚举()
    {
        // JSON 里写 "Theme": 99 或一个没见过的名字，反序列化后就是这个越界值。
        // 不拦住的话 switch 会落到 default，用户选的"浅色"变成"跟随系统"，还查不出原因。
        AppSettings s = new() { Theme = (ThemeMode)99 };
        s.ClampToLimits();
        Assert.Equal(ThemeMode.System, s.Theme);
    }

    [Theory]
    [InlineData(ThemeMode.System)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void ClampToLimits_合法主题原样保留(ThemeMode mode)
    {
        AppSettings s = new() { Theme = mode };
        s.ClampToLimits();
        Assert.Equal(mode, s.Theme);
    }

    [Fact]
    public void ClampToLimits_规范化强调色()
    {
        AppSettings s = new() { AccentColor = " abc " };
        s.ClampToLimits();
        Assert.Equal("#AABBCC", s.AccentColor);
    }

    [Fact]
    public void ClampToLimits_坏强调色退回跟随系统()
    {
        AppSettings s = new() { AccentColor = "#FF0078D4" };
        s.ClampToLimits();
        Assert.Equal(AccentColorSpec.FollowSystem, s.AccentColor);
    }

    [Fact]
    public void ClampToLimits_背景图路径去空白()
    {
        // 从资源管理器复制的路径经常带前后空格甚至引号里的空白。
        AppSettings s = new() { BackgroundImagePath = "  D:\\pic\\wall.png  " };
        s.ClampToLimits();
        Assert.Equal("D:\\pic\\wall.png", s.BackgroundImagePath);
    }

    [Theory]
    [InlineData(-5, SettingsLimits.BackgroundBlurMin)]
    [InlineData(999, SettingsLimits.BackgroundBlurMax)]
    [InlineData(18, 18)]
    public void ClampToLimits_夹回模糊半径(int input, int expected)
    {
        AppSettings s = new() { BackgroundBlur = input };
        s.ClampToLimits();
        Assert.Equal(expected, s.BackgroundBlur);
    }

    [Theory]
    [InlineData(0, SettingsLimits.BackgroundOpacityMin)]     // 0 = 选了图却完全看不见，等于功能是坏的
    [InlineData(-1, SettingsLimits.BackgroundOpacityMin)]
    [InlineData(500, SettingsLimits.BackgroundOpacityMax)]
    [InlineData(30, 30)]
    public void ClampToLimits_夹回不透明度(int input, int expected)
    {
        AppSettings s = new() { BackgroundOpacity = input };
        s.ClampToLimits();
        Assert.Equal(expected, s.BackgroundOpacity);
    }

    [Fact]
    public void 默认值是跟随系统且不带背景图()
    {
        AppSettings s = new();
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.Equal(AccentColorSpec.FollowSystem, s.AccentColor);
        Assert.Equal(string.Empty, s.BackgroundImagePath);
    }

    [Fact]
    public void Clone_把外观字段一起带过去()
    {
        // 标题栏那个一键切换走的是 Clone + 只改外观字段的路子（EngineHost.SaveAppearance），
        // 漏带就等于每次换肤都把别的外观字段悄悄重置 —— 换个主题背景图就没了。
        AppSettings a = new()
        {
            Theme = ThemeMode.Light,
            AccentColor = "#C4314B",
            BackgroundImagePath = "D:\\pic\\wall.png",
            BackgroundBlur = 42,
            BackgroundOpacity = 66,
        };

        AppSettings b = a.Clone();

        Assert.Equal(ThemeMode.Light, b.Theme);
        Assert.Equal("#C4314B", b.AccentColor);
        Assert.Equal("D:\\pic\\wall.png", b.BackgroundImagePath);
        Assert.Equal(42, b.BackgroundBlur);
        Assert.Equal(66, b.BackgroundOpacity);
    }
}

/// <summary>
/// <see cref="AppearanceSpec"/>：外观的完整快照。
///
/// 它存在的唯一理由是"加一项外观设置不会静默漏掉某个调用点"，
/// 所以这里测的重点就是三件事：规范化能兜住手改坏的值、
/// <c>WriteTo</c> 一项都不漏、<c>Matches</c> 判得准（它决定要不要落盘）。
/// </summary>
public class AppearanceSpecTests
{
    private static AppSettings FullyCustom() => new()
    {
        Theme = ThemeMode.Light,
        AccentColor = "#C4314B",
        BackgroundImagePath = "D:\\pic\\wall.png",
        BackgroundBlur = 42,
        BackgroundOpacity = 66,
    };

    [Fact]
    public void Default_就是出厂配置的外观()
    {
        // 默认值只在 AppSettings 的属性初始值里定义一次。这条盯住"别处又抄了一份"。
        Assert.Equal(AppearanceSpec.From(new AppSettings()), AppearanceSpec.Default);
    }

    [Fact]
    public void From_原样取出五个字段()
    {
        AppearanceSpec spec = AppearanceSpec.From(FullyCustom());

        Assert.Equal(ThemeMode.Light, spec.Theme);
        Assert.Equal("#C4314B", spec.AccentColor);
        Assert.Equal("D:\\pic\\wall.png", spec.BackgroundImagePath);
        Assert.Equal(42, spec.BackgroundBlur);
        Assert.Equal(66, spec.BackgroundOpacity);
    }

    [Fact]
    public void From_顺手规范化坏值()
    {
        AppSettings s = new()
        {
            Theme = (ThemeMode)99,
            AccentColor = "#GGHHII",
            BackgroundImagePath = "  D:\\pic\\wall.png  ",
            BackgroundBlur = 999,
            BackgroundOpacity = 0,
        };

        AppearanceSpec spec = AppearanceSpec.From(s);

        Assert.Equal(ThemeMode.System, spec.Theme);
        Assert.Equal(AccentColorSpec.FollowSystem, spec.AccentColor);
        Assert.Equal("D:\\pic\\wall.png", spec.BackgroundImagePath);
        Assert.Equal(SettingsLimits.BackgroundBlurMax, spec.BackgroundBlur);
        Assert.Equal(SettingsLimits.BackgroundOpacityMin, spec.BackgroundOpacity);
    }

    [Fact]
    public void Normalized_是幂等的()
    {
        // ThemeService.Apply 每次都会 Normalized 一遍，设置页也会。
        // 不幂等就会出现"应用一次和应用两次不一样"，表现为拖完滑块松手时值又跳一下。
        AppearanceSpec once = new AppearanceSpec((ThemeMode)99, "abc", "  x.png  ", -3, 500).Normalized();
        Assert.Equal(once, once.Normalized());
    }

    [Fact]
    public void HasBackground_只看有没有填路径()
    {
        // 文件在不在是界面层的事：这里判"文件存在"会让 Core 依赖文件系统，也测不干净。
        Assert.False(AppearanceSpec.Default.HasBackground);
        Assert.True((AppearanceSpec.Default with { BackgroundImagePath = "缺失的文件.png" }).HasBackground);
        Assert.False((AppearanceSpec.Default with { BackgroundImagePath = "   " }).Normalized().HasBackground);
    }

    [Fact]
    public void WriteTo_五个字段全部写进配置()
    {
        AppSettings target = new();
        AppearanceSpec.From(FullyCustom()).WriteTo(target);

        Assert.Equal(ThemeMode.Light, target.Theme);
        Assert.Equal("#C4314B", target.AccentColor);
        Assert.Equal("D:\\pic\\wall.png", target.BackgroundImagePath);
        Assert.Equal(42, target.BackgroundBlur);
        Assert.Equal(66, target.BackgroundOpacity);
    }

    [Fact]
    public void WriteTo_不碰外观以外的字段()
    {
        // SaveAppearance 的整个前提：换个主题不能顺手把设置页上没提交的编辑一起写下去。
        AppSettings target = new() { MonitorPath = "D:\\watch", Password = "秘密", ZipTempKeepDays = 9 };

        AppearanceSpec.From(FullyCustom()).WriteTo(target);

        Assert.Equal("D:\\watch", target.MonitorPath);
        Assert.Equal("秘密", target.Password);
        Assert.Equal(9, target.ZipTempKeepDays);
    }

    [Fact]
    public void WriteTo_写进去的是规范化后的值()
    {
        AppSettings target = new();
        new AppearanceSpec((ThemeMode)99, "abc", "  x.png  ", -3, 500).WriteTo(target);

        Assert.Equal(ThemeMode.System, target.Theme);
        Assert.Equal("#AABBCC", target.AccentColor);
        Assert.Equal("x.png", target.BackgroundImagePath);
        Assert.Equal(SettingsLimits.BackgroundBlurMin, target.BackgroundBlur);
        Assert.Equal(SettingsLimits.BackgroundOpacityMax, target.BackgroundOpacity);
    }

    [Fact]
    public void WriteTo_之后_Matches_成立()
    {
        // 这一对必须闭环：写完立刻比，比出"还不一样"的话 MainViewModel 会每次都落一次盘。
        AppSettings target = new();
        AppearanceSpec spec = AppearanceSpec.From(FullyCustom());

        spec.WriteTo(target);

        Assert.True(spec.Matches(target));
    }

    [Theory]
    [InlineData(ThemeMode.Dark, "#C4314B", "D:\\pic\\wall.png", 42, 66, true)]
    [InlineData(ThemeMode.Light, "#C4314B", "D:\\pic\\wall.png", 42, 66, false)]   // 主题变了
    [InlineData(ThemeMode.Dark, "#0078D4", "D:\\pic\\wall.png", 42, 66, false)]    // 强调色变了
    [InlineData(ThemeMode.Dark, "#C4314B", "D:\\pic\\other.png", 42, 66, false)]   // 换了图
    [InlineData(ThemeMode.Dark, "#C4314B", "D:\\pic\\wall.png", 43, 66, false)]    // 模糊变了
    [InlineData(ThemeMode.Dark, "#C4314B", "D:\\pic\\wall.png", 42, 67, false)]    // 不透明度变了
    public void Matches_任何一项不同都要判成不同(
        ThemeMode theme, string accent, string path, int blur, int opacity, bool expected)
    {
        // 漏掉任何一项的后果都是"改了不落盘"：本次看着生效，重启就还原。
        AppSettings stored = new()
        {
            Theme = ThemeMode.Dark,
            AccentColor = "#C4314B",
            BackgroundImagePath = "D:\\pic\\wall.png",
            BackgroundBlur = 42,
            BackgroundOpacity = 66,
        };

        AppearanceSpec spec = new(theme, accent, path, blur, opacity);

        Assert.Equal(expected, spec.Matches(stored));
    }

    [Fact]
    public void Matches_比的是规范化后的值()
    {
        AppSettings stored = new() { AccentColor = "#AABBCC", BackgroundImagePath = "x.png" };

        // 用户敲的 "abc" 和存着的 "#AABBCC" 是同一个颜色，不该因为写法不同白写一次配置。
        Assert.True(new AppearanceSpec(ThemeMode.System, "abc", "  x.png  ", 18, 30).Matches(stored));
    }

    [Fact]
    public void Matches_路径大小写不同算不同()
    {
        // Windows 上这两个指向同一个文件，但用户改了大小写就是想让界面记住新写法。
        // 比错的代价只是多写一次 settings.json，下一次就相等了。
        AppSettings stored = new() { BackgroundImagePath = "D:\\pic\\wall.png" };

        AppearanceSpec renamed = AppearanceSpec.From(stored) with
        {
            BackgroundImagePath = "D:\\PIC\\WALL.PNG",
        };

        Assert.False(renamed.Matches(stored));
    }
}
