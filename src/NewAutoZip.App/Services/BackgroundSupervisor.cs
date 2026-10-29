using System.Windows.Threading;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Configuration;

namespace NewAutoZip.App.Services;

/// <summary>
/// 后台模式（<c>--background</c>，开机自启任务拉起来的那个无界面进程）的看护人。
///
/// 只做一件事：<b>让引擎跑起来，并且一直跑着</b>。没有界面可以点「开始」，
/// 所以配置校验不过时不能像界面版那样弹个提示条就算了 —— 开机那一刻外接盘还没挂上、
/// 网络盘还没连上，是后台模式下最常见的"校验不过"，过一会儿自己就好了。
/// 于是这里每隔 <see cref="RetryInterval"/> 重读一遍配置再试一次，直到起来为止。
///
/// 日志只在原因<b>变了</b>的时候写：同一个原因每两分钟写一遍，几天下来就是几千行废话。
/// </summary>
internal sealed class BackgroundSupervisor : IDisposable
{
    /// <summary>引擎没跑起来时多久再试一次。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    private readonly EngineHost _host;
    private readonly IAppLogger _log;
    private readonly DispatcherTimer _timer;

    private string? _lastProblem;
    private bool _everStarted;

    internal BackgroundSupervisor(EngineHost host, IAppLogger log)
    {
        _host = host;
        _log = log;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RetryInterval };
        _timer.Tick += (_, _) => EnsureRunning();
    }

    internal void Start()
    {
        EnsureRunning();
        _timer.Start();
    }

    private void EnsureRunning()
    {
        if (_host.IsRunning)
        {
            return;
        }

        try
        {
            // 第一次之后每次都重读：用户可能已经在别处改好了配置（比如接管、改完、又交还）。
            if (_everStarted || _lastProblem is not null)
            {
                _host.ReloadSettings();
            }

            // 先自己校验一遍：校验不过就不去调 TryStart —— 它每次失败都会写一条警告，
            // 而这里每两分钟就试一次，配置坏着放一天就是几百行一模一样的警告。
            ValidationReport report = _host.Validate();

            if (report.CanStart && _host.TryStart(out report))
            {
                _everStarted = true;

                _log.Info(_lastProblem is null
                    ? "后台模式：引擎已启动。"
                    : "后台模式：之前的问题已经消失，引擎已启动。");

                _lastProblem = null;
                return;
            }

            string problem = report.ToText();

            if (!string.Equals(problem, _lastProblem, StringComparison.Ordinal))
            {
                _lastProblem = problem;

                _log.Error(
                    $"后台模式：配置校验未通过，引擎没有启动，每 {RetryInterval.TotalMinutes:0} 分钟重试一次。" +
                    "如果原因是压缩密码为空，多半是开机自启任务不是以「保存密码」方式注册的（解不开 DPAPI 加密的配置），" +
                    $"请打开界面、在设置里重新注册。问题清单：{problem}");
            }
        }
        catch (Exception ex)
        {
            string problem = ex.Message;

            if (!string.Equals(problem, _lastProblem, StringComparison.Ordinal))
            {
                _lastProblem = problem;
                _log.Error("后台模式：启动引擎时出错，稍后重试。", ex);
            }
        }
    }

    public void Dispose() => _timer.Stop();
}
