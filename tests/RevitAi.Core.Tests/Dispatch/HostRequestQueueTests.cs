using RevitAi.Core.Dispatch;

namespace RevitAi.Core.Tests.Dispatch;

public class HostRequestQueueTests
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Request_runs_only_when_host_drains_and_returns_result()
    {
        int signals = 0;
        var queue = new HostRequestQueue<string>(() => signals++);

        Task<int> task = queue.Enqueue(context => context.Length, LongTimeout);

        Assert.Equal(1, signals);
        Assert.False(task.IsCompleted);

        queue.Drain("host");

        Assert.Equal(4, await task);
    }

    [Fact]
    public async Task Drain_runs_every_pending_request_in_order()
    {
        var queue = new HostRequestQueue<List<int>>(() => { });
        var log = new List<int>();

        Task<int> first = queue.Enqueue(l => { l.Add(1); return 1; }, LongTimeout);
        Task<int> second = queue.Enqueue(l => { l.Add(2); return 2; }, LongTimeout);
        queue.Drain(log);

        Assert.Equal([1, 2], log);
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
    }

    [Fact]
    public async Task Exception_in_work_faults_only_that_request()
    {
        var queue = new HostRequestQueue<string>(() => { });

        Task<int> failing = queue.Enqueue<int>(_ => throw new InvalidOperationException("boom"), LongTimeout);
        Task<int> healthy = queue.Enqueue(_ => 7, LongTimeout);
        queue.Drain("host");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Equal("boom", ex.Message);
        Assert.Equal(7, await healthy);
    }

    [Fact]
    public async Task Request_not_drained_in_time_times_out_and_never_runs()
    {
        var queue = new HostRequestQueue<string>(() => { });
        bool ran = false;

        Task<int> task = queue.Enqueue(_ => { ran = true; return 1; }, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => task);
        queue.Drain("host");
        Assert.False(ran);
    }

    [Fact]
    public async Task Cancelled_request_is_cancelled_and_never_runs()
    {
        var queue = new HostRequestQueue<string>(() => { });
        using var cts = new CancellationTokenSource();
        bool ran = false;

        Task<int> task = queue.Enqueue(_ => { ran = true; return 1; }, LongTimeout, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        queue.Drain("host");
        Assert.False(ran);
    }

    [Fact]
    public async Task Already_cancelled_token_cancels_immediately()
    {
        var queue = new HostRequestQueue<string>(() => { });

        Task<int> task = queue.Enqueue(_ => 1, LongTimeout, new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Running_request_completes_even_if_timeout_elapses_during_work()
    {
        var queue = new HostRequestQueue<string>(() => { });

        Task<int> task = queue.Enqueue(_ => { Thread.Sleep(200); return 5; }, TimeSpan.FromMilliseconds(50));
        queue.Drain("host");

        Assert.Equal(5, await task);
    }
}
