using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.App.Views;

/// <summary>选中的导航项索引 == 参数时才显示对应页面。</summary>
public sealed class IndexVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int selected
            && parameter is string text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted))
        {
            return selected == wanted ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>true → 显示。参数为 "invert" 时反转。</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;

        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>非空字符串 / 非空集合 / 非 null 才显示。
/// <b>集合一定要绑 <c>.Count</c>，不能绑集合本身。</b>
/// <c>{Binding Files, Converter=...}</c> 只在集合<i>换成另一个对象</i>时才重新求值 ——
/// 往 <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> 里加删元素
/// 不会让这个绑定动一下，于是空列表提示会一直挂在屏幕上、表格永远不出现，
/// 而且<b>不报任何错</b>。<c>{Binding Files.Count, ...}</c> 才会跟着变
/// （ObservableCollection 增删时会为 Count 发属性变更通知）。
/// </summary>
public sealed class HasContentVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int n => n > 0,
            long n => n > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            has = !has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>时间戳 → <c>HH:mm:ss</c>。日志一行一条，日期挤在里面只是噪音。</summary>
public sealed class TimeOfDayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DateTimeOffset dto => dto.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 时间戳 → <c>yyyy-MM-dd HH:mm:ss</c>。<b>带年份。</b>
///
/// 用转换器而不是 <c>StringFormat=yyyy-MM-dd HH:mm:ss</c>：那样每个列都要抄一遍格式串，
/// 而放进资源里的 <c>StringFormat</c> 又必须写 XAML 的 <c>{}</c> 转义、
/// 这个转义只对内联特性值有效，从资源取出来时会原样留在字符串里
/// （表现是运行时一句 <c>StringFormat converter failed</c>，界面上那一格变空）。
/// 转换器没有这些坑，还能顺手把 null 变成空串而不是 "1/1/0001"。
///
/// 为什么必须带年份：这个程序常驻后台跑几个月，"08-13 18:09"跨年之后完全分不清
/// 是今年还是去年 —— 而"上次成功备份是多久以前"恰好是用户最需要判断的事。
/// </summary>
public sealed class StampConverter : IValueConverter
{
    /// <summary>界面上所有"某件事发生在什么时候"的统一格式。</summary>
    public const string Format = "yyyy-MM-dd HH:mm:ss";

    /// <summary>同上，不带秒 —— "下次几点开工"精确到秒没有意义。</summary>
    public const string MinuteFormat = "yyyy-MM-dd HH:mm";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DateTimeOffset dto => dto.ToString(Format, CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString(Format, CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

// 这里原先还有 ThemeBrush / ResourceKeyBrushConverter / LogLevelBrushConverter 三个东西：
// ViewModel 给一个资源键（"DotOk"），转换器按键名去 App.xaml 取画刷。已经删掉了。
//
// 那条路有一个改不掉的毛病：绑定只在<源属性变化>时求值，而键名恰恰是不变的那部分。
// 切主题时资源里的画刷换了，绑定不会重新求值，颜色就停在上一个主题上 ——
// 状态灯变成浅色底上一颗看不见的浅绿点，几百行已经画出来的日志整片保持深色主题的浅灰。
// 现在状态灯（MainWindow.xaml）和日志行（LogPage.xaml）都改成
// Style + DataTrigger + DynamicResource：颜色由资源系统推送，换主题即刻生效。
