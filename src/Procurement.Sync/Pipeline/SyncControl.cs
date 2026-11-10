namespace Procurement.Sync.Pipeline;

// A paused run keeps its lease, so no other run can start while it waits.
public sealed class SyncControl
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _paused;

    public bool IsPaused
    {
        get { lock (_gate) return _paused is not null; }
    }

    public void Pause()
    {
        lock (_gate) _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? paused;
        lock (_gate)
        {
            paused = _paused;
            _paused = null;
        }
        paused?.TrySetResult();
    }

    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task? wait;
        lock (_gate) wait = _paused?.Task;
        return wait is null ? Task.CompletedTask : wait.WaitAsync(ct);
    }
}
