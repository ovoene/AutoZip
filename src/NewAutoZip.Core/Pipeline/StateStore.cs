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
