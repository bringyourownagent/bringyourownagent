namespace Byoa.Wallet.Everywhere;

/// <summary>
/// Runs jobs one after another, in the order they were posted. A hotkey arrives on the keyboard hook's thread, which must never wait; the state
/// machines take their commands through a queue, so that they need no locks and a "released" never overtakes its "pressed".
/// </summary>
sealed class SerialQueue(Action<Exception>? onError = null)
{
    readonly object _gate = new();
    Task _tail = Task.CompletedTask;

    /// <summary>Queues the job; the task ends when it has run. A job that throws is reported and does not stop the ones behind it.</summary>
    public Task Post(Func<Task> job)
    {
        lock (_gate)
        {
            var next = _tail.ContinueWith(_ => RunAsync(job), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _tail = next;
            return next;
        }
    }

    public Task Post(Action job) => Post(() =>
    {
        job();
        return Task.CompletedTask;
    });

    async Task RunAsync(Func<Task> job)
    {
        try
        {
            await job().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            onError?.Invoke(e);
        }
    }
}
