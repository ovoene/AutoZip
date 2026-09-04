using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NewAutoZip.App.ViewModels;

namespace NewAutoZip.App.Views;

/// <summary>
/// 日志页。
///
/// 自动跟随必须由代码驱动：<c>ListBox</c> 开了虚拟化，目标那一项在滚进视野之前
/// 根本没有可视容器，纯 XAML 做不到「跟着最新一行走」。
///
/// 「最新的一行」在哪一头由 <see cref="MainViewModel.NewestLogFirst"/> 决定 ——
/// 倒序时在 0 号位，正序时在末尾。这里必须问它，不能写死 <c>[^1]</c>：
/// 写死的话切成倒序后，界面会一直往下滚去追那条<b>最旧</b>的记录，
/// 而新行正从头顶上冒出来，越滚越看不见。
///
/// 除了滚动，最新那一行还会被<b>选中</b>：日志一行挨一行、颜色又淡，
/// 没有选中态的话眼睛找不到"当前看到哪了"，倒序时新行从顶上冒出来更是如此。
/// 选中和滚动共用同一个开关（<see cref="MainViewModel.AutoScrollLog"/>，默认开），
/// 因为它们的意图本来就是一件事 —— 用户想停下来细看某一行时，
/// 只要取消勾选，滚动和选中就会一起停住，不会把视野拽回最新那条。
/// </summary>
public partial class LogPage : UserControl
{
    private MainViewModel? _model;
    private INotifyCollectionChanged? _watched;

    /// <summary>已经排了一次待滚动。用来把一轮里的几十条变更合成一次滚动。</summary>
    private bool _scrollPending;

    public LogPage()
    {
        InitializeComponent();

        // UserControl 的 DataContext 是从 MainWindow 继承下来的，构造时还没有值。
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (DataContext is not MainViewModel model)
        {
            return;
        }

        _model = model;
        _watched = model.LogLines;
        _watched.CollectionChanged += OnLogChanged;

        // 进页面时先补一次：日志是后台定时器一撮一撮喂进来的，
        // 如果这一刻恰好没有新日志，OnLogChanged 一次都不会触发，
        // 列表就会一直停在"没有任何一行被选中"的状态。
        //
        // 同样推到 Background 优先级 —— 此刻布局还没量完，
        // 在这里直接选会让 ListBox 去为一个还没生成的容器定位。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToNewest));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_watched is not null)
        {
            _watched.CollectionChanged -= OnLogChanged;
            _watched = null;
        }

        _model = null;
    }

    /// <summary>
    /// 排一次滚动，<b>不在这里直接滚</b>。
    ///
    /// <c>ScrollIntoView</c> 内部会强制走一次 <c>UpdateLayout</c>。在 CollectionChanged
    /// 的处理函数里调它，等于让虚拟化面板在 <c>ListBox</c> 自己还没消化完这条通知时就去量布局，
    /// 生成器看到的条数和集合的真实条数对不上 —— 直接抛
    /// “某个 ItemsControl 与它的项源不一致”。一轮塞进几十条日志时必中。
    ///
    /// 所以推到 <c>Background</c> 优先级：等这一批通知和布局都处理完再滚一次。
    /// 顺带把一轮里的多次变更合并成一次滚动，本来也只需要滚到最新那一条。
    ///
    /// <c>Reset</c> 也要接：切换排序方向时列表是整块重建的（发的是 Reset 而不是 Add），
    /// 翻完之后视野得跟着落到新的那一头，否则用户点一下开关，看到的是空白或者一片旧记录。
    /// </summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        bool interesting = e.Action is NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Reset;

        if (!interesting || _scrollPending || _model is not { AutoScrollLog: true })
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToNewest));
    }

    private void ScrollToNewest()
    {
        _scrollPending = false;

        // 排队期间可能已经切走页面、关掉自动跟随，或者日志被清空了。
        if (_model is not { AutoScrollLog: true } model || model.LogLines.Count == 0)
        {
            return;
        }

        try
        {
            // 只取一次、装箱一次：SelectedItem 和 ScrollIntoView 都按值相等去找容器，
            // 同一个盒子能保证两者落在同一行上。
            object newest = model.NewestLogFirst ? model.LogLines[0] : model.LogLines[^1];

            LogList.SelectedItem = newest;
            LogList.ScrollIntoView(newest);
        }
        catch (Exception)
        {
            // 滚动纯粹是观感，任何情况下都不值得让它把程序带下去。
        }
    }
}
