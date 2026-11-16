using System.Windows.Threading;
using WpfDispatcher = System.Windows.Threading.Dispatcher;

namespace QuickPreview;

/// <summary>Serializes Explorer COM calls on one background STA with a message pump.</summary>
internal static class ExplorerStaWorker
{
    private static readonly Lazy<Task<WpfDispatcher>> WorkerDispatcher = new(CreateDispatcher);
    private static int _shutdown;

    internal static Task<WpfDispatcher> Dispatcher => WorkerDispatcher.Value;

    internal static async Task<T> RunAsync<T>(Func<T> action, DispatcherPriority priority = DispatcherPriority.Normal,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdown) != 0) throw new OperationCanceledException("Explorer worker is shutting down.");
        var dispatcher = await WorkerDispatcher.Value.ConfigureAwait(false);
        if (Volatile.Read(ref _shutdown) != 0 || dispatcher.HasShutdownStarted)
            throw new OperationCanceledException("Explorer worker is shutting down.");
        // Navigation uses Normal priority. Selection polling uses Background so
        // a queued poll never overtakes pending keyboard navigation.
        return await dispatcher.InvokeAsync(action, priority, token).Task.ConfigureAwait(false);
    }

    internal static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0 || !WorkerDispatcher.IsValueCreated) return;
        _ = WorkerDispatcher.Value.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && !task.Result.HasShutdownStarted)
                task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static Task<WpfDispatcher> CreateDispatcher()
    {
        var ready = new TaskCompletionSource<WpfDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ready.TrySetResult(WpfDispatcher.CurrentDispatcher);
                WpfDispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "QuickPreview Explorer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
