namespace NewAutoZip.Core.Pipeline;

public enum EnginePhase
{
    Stopped,
    Starting,

    /// <summary>在计划时段内，等待新文件。</summary>
    Idle,

    /// <summary>不在计划时段内（日期范围外或每日时段外）。</summary>
    OutsideSchedule,

    /// <summary>批处理窗口已开启，正在收集文件。</summary>
    Collecting,

    Packing,

    /// <summary>正在把归档移入目标目录（OneDrive 同步目录，或用户指定的普通目录）。</summary>
    Delivering,

    /// <summary>等待云端确认收到（只有 OneDrive 模式会走到这里）。</summary>
    WaitingUpload,

    /// <summary>正在请求释放本地空间（只有 OneDrive 模式会走到这里）。</summary>
    Releasing,

    /// <summary>上一批失败，正在按退避间隔等待重试。</summary>
    Retrying,

    /// <summary>
    /// 正在做恢复演练：把包真的解开一遍，确认它还能用。
    /// 两种 <c>CloudTarget</c> 模式下都可能出现（本地刚打完的包、以及云端抽验的包）。
    /// </summary>
    Drilling,

    Stopping,
}

/// <summary>
/// 一轮处理分成哪两段。<b>"成功了"和"失败了"必须说清是哪一段</b>——
/// "打包成功但没送上去"和"压根没打成包"要采取的行动完全不同，
/// 只报一个笼统的"最近失败"等于让用户自己去翻日志。
/// </summary>
public enum PipelineStage
{
    /// <summary>生成压缩包（含磁盘预检与完整性校验）。</summary>
    Pack,

    /// <summary>送达：把压缩包移进目标目录，OneDrive 模式还要确认云端收到。</summary>
    Deliver,
}

public static class EnginePhaseText
{
    /// <summary>
    /// 阶段的中文名。<b>不带云盘类型参数</b>，所以措辞必须两种模式下都成立 ——
    /// <see cref="EnginePhase.Delivering"/> 因此叫"移入目标目录"而不是"移入云盘目录"：
    /// 非 OneDrive 那一边目标可能压根不是云盘（普通目录、映射的 NAS 盘），
    /// 说"云盘"是在替一个程序从没检测过的客户端背书。
    /// 需要按模式区分称呼的地方走 <c>CloudTargetText.PathLabel</c>（如流水线第 4 步）。
    /// </summary>
    public static string Describe(EnginePhase phase) => phase switch
    {
        EnginePhase.Stopped => "已停止",
        EnginePhase.Starting => "正在启动",
        EnginePhase.Idle => "等待新文件",
        EnginePhase.OutsideSchedule => "不在计划时段",
        EnginePhase.Collecting => "收集文件中",
        EnginePhase.Packing => "压缩中",
        EnginePhase.Delivering => "移入目标目录",
        EnginePhase.WaitingUpload => "等待上传完成",
        EnginePhase.Releasing => "释放本地空间",
        EnginePhase.Retrying => "等待重试",
        EnginePhase.Drilling => "恢复演练中",
        EnginePhase.Stopping => "正在停止",
        _ => phase.ToString(),
    };
}
