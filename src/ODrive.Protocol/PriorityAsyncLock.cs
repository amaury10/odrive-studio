namespace ODrive.Protocol;

/// <summary>Priorité d'une requête dans la file d'une carte.</summary>
public enum RequestPriority
{
    Normal,

    /// <summary>Arrêt d'urgence : passe devant toutes les requêtes en attente (docs/SAFETY.md).</summary>
    Emergency,
}

/// <summary>
/// Verrou asynchrone à deux niveaux de priorité : une seule requête en vol par carte, et une requête
/// d'urgence passe devant toutes celles qui attendent.
/// </summary>
public sealed class PriorityAsyncLock
{
    private readonly Lock _sync = new();
    private readonly LinkedList<TaskCompletionSource> _emergency = new();
    private readonly LinkedList<TaskCompletionSource> _normal = new();
    private bool _held;

    public Task EnterAsync(RequestPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TaskCompletionSource waiter;
        LinkedList<TaskCompletionSource> queue = priority == RequestPriority.Emergency ? _emergency : _normal;
        lock (_sync)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.AddLast(waiter);
        }

        if (!ct.CanBeCanceled)
        {
            return waiter.Task;
        }
        return WaitAsync(waiter, queue, ct);
    }

    public void Exit()
    {
        lock (_sync)
        {
            while (true)
            {
                LinkedList<TaskCompletionSource> queue = _emergency.Count > 0 ? _emergency : _normal;
                if (queue.Count == 0)
                {
                    _held = false;
                    return;
                }

                TaskCompletionSource next = queue.First!.Value;
                queue.RemoveFirst();
                // Échoue si l'attente vient d'être annulée : on passe au suivant.
                if (next.TrySetResult())
                {
                    return;
                }
            }
        }
    }

    private async Task WaitAsync(TaskCompletionSource waiter, LinkedList<TaskCompletionSource> queue, CancellationToken ct)
    {
        await using CancellationTokenRegistration registration = ct.Register(() =>
        {
            if (waiter.TrySetCanceled(ct))
            {
                lock (_sync)
                {
                    queue.Remove(waiter);
                }
            }
        });
        await waiter.Task.ConfigureAwait(false);
    }
}
