using Autodesk.Revit.UI;
using RevitAi.Core.Dispatch;
using RevitAi.Core.Infrastructure;

namespace RevitAi.Addin.Dispatch;

/// <summary>
/// The only way code outside Revit's API context reaches the Revit API (ADR-023).
/// Never block on the returned task from the UI thread (.Result / .Wait()):
/// Revit runs the external event on that same thread, so it would deadlock.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private readonly ExternalEvent _externalEvent;
    private readonly HostRequestQueue<UIApplication> _queue;
    private readonly TimeSpan _timeout;
    private readonly FileLog _log;

    /// <summary>Must be constructed inside Revit's API context (e.g. OnStartup).</summary>
    public RevitDispatcher(TimeSpan timeout, FileLog log)
    {
        _timeout = timeout;
        _log = log;
        _externalEvent = ExternalEvent.Create(this);
        _queue = new HostRequestQueue<UIApplication>(Signal);
    }

    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work, CancellationToken cancellationToken = default) =>
        _queue.Enqueue(work, _timeout, cancellationToken);

    public void Execute(UIApplication app) => _queue.Drain(app);

    public string GetName() => "Revit AI dispatcher";

    private void Signal()
    {
        // Pending means an earlier raise has not run yet; that run drains this request too.
        ExternalEventRequest outcome = _externalEvent.Raise();
        if (outcome is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
        {
            _log.Warning($"Revit did not accept the dispatcher event ({outcome}). The request will time out.");
        }
    }
}
