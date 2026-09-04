namespace NewAutoZip.Core.Configuration;

/// <summary>
/// 云盘类型。决定压缩包"送达"之后程序还做不做事。
/// </summary>
public enum CloudTarget
{
    /// <summary>
    /// OneDrive。移进目录之后还要确认云端确实收到，再按设置释放本地占用。
    /// 这条路能给出"云端已收到"的证据，所以值得多等那一段。
    /// </summary>
    OneDrive,

    /// <summary>
    /// 其他云盘的本地同步目录（坚果云、百度网盘、Dropbox、群晖 Drive、或者干脆一个普通目录）。
    ///
    /// 这些客户端没有统一、可靠、且与系统语言无关的"这个文件同步完了吗"的查询方式。
    /// 与其猜一个再谎报成功，不如把边界划清楚：<b>压缩包剪切进目标目录，本轮就算完成。</b>
    /// 之后是客户端自己的事，程序不再等待、不再检测、消息里也不出现 OneDrive 字样。
    /// </summary>
    Folder,
}

/// <summary>云盘类型对应的措辞。同一件事在两种模式下该有不同的说法。</summary>
public static class CloudTargetText
{
    /// <summary>设置页下拉框里的说明。</summary>
    public static string Describe(CloudTarget target) => target switch
    {
        CloudTarget.OneDrive => "OneDrive（确认云端收到后可释放本地空间）",
        CloudTarget.Folder => "其他云盘 / 普通目录（剪切进去即完成）",
        _ => target.ToString(),
    };

    /// <summary>
    /// 一行提示里用的短名字。<see cref="Describe"/> 那串括号说明放进句子里太长，
    /// 一句话要提两种类型时（"已保存为 A、但引擎还在按 B 跑"）会长到看不清重点。
    /// </summary>
    public static string ShortName(CloudTarget target) =>
        target == CloudTarget.OneDrive ? "OneDrive" : "其他云盘";

    /// <summary>"送达"这一步在这种模式下叫什么。OneDrive 叫上传，普通目录只是移入。</summary>
    public static string DeliverNoun(CloudTarget target) =>
        target == CloudTarget.OneDrive ? "上传" : "移入";

    /// <summary>
    /// 目标目录在界面与消息里的称呼。
    ///
    /// 非 OneDrive 一律叫"指定目录"，<b>不叫"云盘目录"</b>：这条分支下程序做的事就是
    /// 把压缩包移进用户指定的那个目录，之后什么都不管 —— 说"云盘"是在替一个
    /// 程序根本没检测过的客户端背书，用户也确实抱怨过流程和消息里到处冒出"云盘"。
    /// 目录后面可能压根没有云盘（就是个普通目录、或者一台 NAS 的映射盘）。
    /// </summary>
    public static string PathLabel(CloudTarget target) =>
        target == CloudTarget.OneDrive ? "OneDrive 目录" : "指定目录";
}
