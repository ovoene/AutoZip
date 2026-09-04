using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Pipeline;

namespace NewAutoZip.App.ViewModels;

/// <summary>
/// 「执行到哪一步了」里的一行。
///
/// 状态条上那句「压缩中」只告诉用户<b>此刻</b>在干什么，看不出整条流水线有几步、
/// 走到哪了、还剩什么。这个列表把全程摊开：走过的打勾、正在跑的挂徽章、没到的灰着。
/// </summary>
public sealed class PipelineStepView : ObservableObject
{
    private bool _isActive;
    private bool _isDone;

    public PipelineStepView(string ordinal, string title, string hint)
    {
        Ordinal = ordinal;
        Title = title;
        Hint = hint;
    }

    /// <summary>
    /// 序号（"1"…"6"）。
    ///
    /// 刻意用数字而不是图标字体：图标要是在目标机器的字体里不存在，
    /// 渲染出来是一个空心方框，比没有更难看 —— 而数字任何字体都有。
    /// 已完成的那几步界面会把它换成对勾。
    /// </summary>
    public string Ordinal { get; }

    public string Title { get; }

    /// <summary>这一步到底在做什么。旧版界面完全没有这类解释。</summary>
    public string Hint { get; }

    /// <summary>正在进行。界面用它显示徽章。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>本轮已经走过了。</summary>
    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }
}

/// <summary>
/// 流水线有哪几步、当前跑到第几步。
///
/// 步数随云盘类型变：非 OneDrive 模式下「等待上传完成」「释放本地空间」这两步
/// <b>根本不存在</b>（程序移入目录就收工），列出来只会让人以为它们还会发生。
/// </summary>
public static class PipelineStepMap
{
    public static List<PipelineStepView> Build(CloudTarget target)
    {
        bool oneDrive = target == CloudTarget.OneDrive;

        // 第 4 步的名字取自 PathLabel，跟总览页那个「打开…」按钮同一个来源 ——
        // 两处说的是同一个目录，各写一份就会各改一半。空格规则也照按钮来：
        // 中文动词后面紧跟拉丁词会挤成「移入OneDrive 目录」。
        string pathLabel = CloudTargetText.PathLabel(target);
        string deliverStep = oneDrive ? $"移入 {pathLabel}" : $"移入{pathLabel}";

        List<PipelineStepView> steps =
        [
            new("1", "等待新文件", "监控目录里出现新文件就开一个批处理窗口"),
            new("2", "收集文件", "等文件写完不再变动，凑齐这一窗口内的全部文件"),
            new("3", "压缩打包", "生成加密压缩包，校验完整性后才认账"),
            new("4", deliverStep,
                oneDrive ? "压缩包剪切到同步目录" : "压缩包剪切到目标目录，本轮到此结束"),
        ];

        if (oneDrive)
        {
            steps.Add(new("5", "等待上传完成", "确认云端确实收到了这个包，而不是猜"));
            steps.Add(new("6", "释放本地空间", "让 OneDrive 把本地副本脱水成「仅联机」"));
        }

        return steps;
    }

    /// <summary>
    /// 当前阶段对应第几步，-1 表示「没有任何一步在跑」。
    ///
    /// <see cref="EnginePhase.Retrying"/> 与 <see cref="EnginePhase.OutsideSchedule"/>
    /// 故意映射成 -1：它们都不是流水线上的一步。前者是「上一批失败了，正在等下一次尝试」，
    /// 后者是「压根不在工作时段，什么都没在看」—— 把「正在进行」挂到某一步上就是在撒谎。
    /// 这两种情况由 <see cref="Note"/> 出面解释，所以有下标就没有说明、有说明就没有下标，
    /// 两者恰好互补（自检会把这条互补关系钉住）。
    /// </summary>
    public static int ActiveIndex(EnginePhase phase) => phase switch
    {
        EnginePhase.Starting or EnginePhase.Idle => 0,
        EnginePhase.Collecting => 1,
        EnginePhase.Packing => 2,
        EnginePhase.Delivering => 3,
        EnginePhase.WaitingUpload => 4,
        EnginePhase.Releasing => 5,
        _ => -1,
    };

    /// <summary>没有步骤在跑时说明为什么。空字符串＝流水线正常推进，不必解释。</summary>
    public static string Note(EnginePhase phase) => phase switch
    {
        EnginePhase.Stopped => "引擎未启动，点上方的「开始」。",
        EnginePhase.Stopping => "正在停止，等当前动作收尾。",
        EnginePhase.Retrying => "上一批失败了，正在按退避间隔等待重试。",
        EnginePhase.OutsideSchedule => "不在工作时段，到点自动开始。",
        _ => string.Empty,
    };
}
