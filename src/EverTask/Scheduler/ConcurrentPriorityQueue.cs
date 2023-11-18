using System.Diagnostics.CodeAnalysis;

namespace EverTask.Scheduler;

internal sealed class ConcurrentPriorityQueue<TElement, TPriority>
{
    private readonly PriorityQueue<TElement, TPriority> _queue;
    private readonly object _lock = new();


    /// <summary>
    ///  Initializes a new instance of the <see cref="PriorityQueue{TElement, TPriority}"/> class with thread safety and default comparer.
    /// </summary>
    public ConcurrentPriorityQueue() => _queue = new PriorityQueue<TElement, TPriority>();

    /// <summary>
    ///  Adds with thread safety the specified element with associated priority to the <see cref="PriorityQueue{TElement, TPriority}"/>.
    /// </summary>
    /// <param name="element">The element to add to the <see cref="PriorityQueue{TElement, TPriority}"/>.</param>
    /// <param name="priority">The priority with which to associate the new element.</param>
    public void Enqueue(TElement element, TPriority priority)
    {
        lock (_lock)
        {
            _queue.Enqueue(element, priority);
        }
    }

    /// <summary>
    ///  Enqueues with thread safety a sequence of element/priority pairs to the <see cref="PriorityQueue{TElement, TPriority}"/>.
    /// </summary>
    /// <param name="items">The pairs of elements and priorities to add to the queue.</param>
    /// <exception cref="ArgumentNullException">
    ///  The specified <paramref name="items"/> argument was <see langword="null"/>.
    /// </exception>
    public void EnqueueRange(IEnumerable<(TElement Element, TPriority Priority)> items)
    {
        lock (_lock)
        {
            _queue.EnqueueRange(items);
        }
    }

    /// <summary>
    ///  Removes with thread safety and returns the minimal element from the <see cref="PriorityQueue{TElement, TPriority}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    /// <returns>The minimal element of the <see cref="PriorityQueue{TElement, TPriority}"/>.</returns>
    public TElement Dequeue()
    {
        lock (_lock)
        {
            return _queue.Dequeue();
        }
    }

    /// <summary>
    ///  Removes with thread safety the minimal element from the <see cref="PriorityQueue{TElement, TPriority}"/>,
    ///  and copies it to the <paramref name="element"/> parameter,
    ///  and its associated priority to the <paramref name="priority"/> parameter.
    /// </summary>
    /// <param name="element">The removed element.</param>
    /// <param name="priority">The priority associated with the removed element.</param>
    /// <returns>
    ///  <see langword="true"/> if the element is successfully removed;
    ///  <see langword="false"/> if the <see cref="PriorityQueue{TElement, TPriority}"/> is empty.
    /// </returns>
    public bool TryDequeue([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        lock (_lock)
        {
            return _queue.TryDequeue(out element, out priority);
        }
    }

    /// <summary>
    ///  Returns with thread safety the minimal element from the <see cref="PriorityQueue{TElement, TPriority}"/> without removing it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The <see cref="PriorityQueue{TElement, TPriority}"/> is empty.</exception>
    /// <returns>The minimal element of the <see cref="PriorityQueue{TElement, TPriority}"/>.</returns>
    public TElement Peek()
    {
        lock (_lock)
        {
            return _queue.Peek();
        }
    }

    /// <summary>
    ///  Returns with thread safety a value that indicates whether there is a minimal element in the <see cref="PriorityQueue{TElement, TPriority}"/>,
    ///  and if one is present, copies it to the <paramref name="element"/> parameter,
    ///  and its associated priority to the <paramref name="priority"/> parameter.
    ///  The element is not removed from the <see cref="PriorityQueue{TElement, TPriority}"/>.
    /// </summary>
    /// <param name="element">The minimal element in the queue.</param>
    /// <param name="priority">The priority associated with the minimal element.</param>
    /// <returns>
    ///  <see langword="true"/> if there is a minimal element;
    ///  <see langword="false"/> if the <see cref="PriorityQueue{TElement, TPriority}"/> is empty.
    /// </returns>
    public bool TryPeek([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        lock (_lock)
        {
            return _queue.TryPeek(out element, out priority);
        }
    }

    /// <summary>
    ///  Gets, with thread safety, the number of elements contained in the <see cref="PriorityQueue{TElement, TPriority}"/>.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }
}
