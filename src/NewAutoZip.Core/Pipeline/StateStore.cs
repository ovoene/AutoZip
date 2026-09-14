using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Pipeline;

/// <summary>
/// 状态持久化。与 <see cref="SettingsStore"/> 同样的原子写 + 永不抛异常策略。
///
/// 关键差异：<see cref="Save"/> 只记日志、<b>不抛异常</b>。
/// 状态写不进去（磁盘满、目录只读）是必须容忍的情况 ——
/// 旧版让它一路抛到主循环，正好跳过了状态复位，把"磁盘快满了"直接升级成"磁盘被打满"。
/// </summary>
public sealed class StateStore
{
    private readonly string _path;
    private readonly IAppLogger _log;
    private readonly object _gate = new();

    private bool _warnedAboutSaveFailure;

    public StateStore(string path, IAppLogger log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;

    public EngineState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new EngineState();
            }

            EngineState? state = AtomicJsonFile.TryRead<EngineState>(_path, out string? error);

            if (state is not null)
            {
                state.PendingUploads ??= [];
                state.Quarantined ??= [];
                state.ForcedFiles ??= [];
                state.FutureStamped ??= [];
                return state;
            }

            _log.Warn($"状态文件损坏，已按空状态启动（原文件保留为 .bad 供排查）：{error}");
            TryPreserveBadFile();
            return new EngineState();
        }
    }

    public void Save(EngineState state)
    {
        lock (_gate)
        {
            if (AtomicJsonFile.TryWrite(_path, state, out string? error))
            {
                _warnedAboutSaveFailure = false;
                return;
            }

            // 只在第一次失败时告警，否则每轮一条会把日志刷爆。
            if (!_warnedAboutSaveFailure)
            {
                _warnedAboutSaveFailure = true;
                _log.Error($"保存运行状态失败（后续同类失败不再重复告警）：{error}。" +
                           "程序会继续工作，但重启后可能丢失待上传记录与统计数字。");
            }
        }
    }

    /// <summary>
    /// 把运行状态复位成"全新的、从未运行过"的样子。
    ///
    /// 清的是<b>账本</b>：累计计数、各类时间戳、增量水位线 <c>Checkpoint</c>、
    /// 首次运行标记、待上传队列、隔离批次、强制/未来时间戳台账 —— 全部回到初始值。
    ///
    /// 清的<b>不是备份</b>：ZipTemp 与云盘目录里的压缩包一个都不碰。
    /// 这个区分是这个方法存在的全部意义，调用方的确认文案里必须说清楚。
    ///
    /// <b>只能在引擎停止时调用。</b><see cref="BackupEngine"/> 启动时把状态读进内存副本，
    /// 之后十几处 <see cref="Save"/> 会把那份副本写回来 —— 运行中复位，
    /// 下一次保存就把旧状态原样写回去了，用户看到的是"点了没反应"。
    ///
    /// 与 <see cref="Save"/> 不同，这里<b>会</b>把失败报告给调用方：
    /// 这是用户主动点的一次性操作，失败了必须当场说，不能只记一行日志。
    /// </summary>
    /// <returns>成功为 true；失败时 <paramref name="error"/> 带上原因。</returns>
    public bool Reset(out string? error)
    {
        lock (_gate)
        {
            if (AtomicJsonFile.TryWrite(_path, new EngineState(), out error))
            {
                _warnedAboutSaveFailure = false;
                _log.Info("运行状态已清空，下次启动按首次运行处理。");
                return true;
            }

            _log.Error($"清空运行状态失败：{error}");
            return false;
        }
    }

    private void TryPreserveBadFile()
    {
        try
        {
            string bad = _path + ".bad";
            File.Move(_path, bad, overwrite: true);
        }
        catch
        {
            // 保不住就算了，不能让它影响启动。
        }
    }
}
