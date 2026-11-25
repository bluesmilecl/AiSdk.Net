using System.Runtime.CompilerServices;

namespace AiSdk.Internal;

/// <summary>
/// Buffer that allows multiple independent consumers of the same stream
/// (each one receives all the elements from the beginning).
/// </summary>
internal sealed class ReplayBuffer<T>
{
    private readonly List<T> _items = [];
    private readonly object _gate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public void Add(T item)
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _items.Add(item);
            previous = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    public void Complete()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            _completed = true;
            previous = _signal;
        }

        previous.TrySetResult();
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        while (true)
        {
            Task wait;
            List<T>? batch = null;
            lock (_gate)
            {
                if (index < _items.Count)
                {
                    batch = _items.GetRange(index, _items.Count - index);
                    index = _items.Count;
                }
                else if (_completed)
                {
                    yield break;
                }

                wait = _signal.Task;
            }

            if (batch is not null)
            {
                foreach (var item in batch)
                {
                    yield return item;
                }

                continue;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
