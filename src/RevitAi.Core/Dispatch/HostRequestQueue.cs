using System.Collections.Concurrent;

namespace RevitAi.Core.Dispatch;

/// <summary>
/// Work that must run on a single host thread (Revit's API context), requested from any thread.
/// Callers enqueue and await; the host drains the queue when it gets control.
/// A request either never runs (timed out or cancelled while waiting) or runs to completion
/// and reports its result. It is never abandoned halfway.
/// </summary>
public sealed class HostRequestQueue<TContext>
{
    private readonly ConcurrentQueue<IHostRequest> _pending = new();
    private readonly Action _signalHost;

    /// <param name="signalHost">Asks the host to call <see cref="Drain"/> soon (e.g. ExternalEvent.Raise).</param>
    public HostRequestQueue(Action signalHost)
    {
        _signalHost = signalHost;
    }

    public Task<T> Enqueue<T>(Func<TContext, T> work, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var request = new HostRequest<T>(work, timeout, cancellationToken);
        _pending.Enqueue(request);
        _signalHost();
        return request.Task;
    }

    /// <summary>Runs every pending request. Must only be called on the host thread.</summary>
    public void Drain(TContext context)
    {
        while (_pending.TryDequeue(out IHostRequest? request))
        {
            request.Run(context);
        }
    }

    private interface IHostRequest
    {
        void Run(TContext context);
    }

    private sealed class HostRequest<T> : IHostRequest
    {
        private const int Waiting = 0;
        private const int Running = 1;
        private const int Abandoned = 2;

        private readonly Func<TContext, T> _work;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _abandon;
        private readonly CancellationTokenRegistration _registration;
        private int _state = Waiting;

        public HostRequest(Func<TContext, T> work, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _work = work;
            _abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _abandon.CancelAfter(timeout);
            _registration = _abandon.Token.Register(() => Abandon(cancellationToken, timeout));
        }

        public Task<T> Task => _completion.Task;

        public void Run(TContext context)
        {
            if (Interlocked.CompareExchange(ref _state, Running, Waiting) != Waiting)
            {
                return;
            }

            _registration.Dispose();
            _abandon.Dispose();

            try
            {
                _completion.SetResult(_work(context));
            }
            catch (Exception ex)
            {
                _completion.SetException(ex);
            }
        }

        private void Abandon(CancellationToken callerToken, TimeSpan timeout)
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Waiting) != Waiting)
            {
                return;
            }

            if (callerToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(callerToken);
            }
            else
            {
                _completion.TrySetException(new TimeoutException(
                    $"The request was not processed within {timeout.TotalSeconds:0} seconds."));
            }
        }
    }
}
