using System.Diagnostics.CodeAnalysis;

namespace EverTask.Scheduler;

internal class ConcurrentPriorityQueue<TElement, TPriority>
{
    private readonly PriorityQueue<TElement, TPriority> _queue;
    private readonly object _lock = new();


    /// <summary>
    ///  Initializes a new instance of the <see cref="PriorityQueue{TElement, TPriority}"/> class with thread safety and default comparer.
    /// </summary>
    public ConcurrentPriorityQueue() => _queue = new PriorityQueue<TElement, TPriority>();

    /// <summary>
    ///  Initializes a new instance of the <see cref="PriorityQueue{TElement, TPriority}"/> class with thread safety
    ///  and the specified custom priority comparer.
    /// </summary>
    /// <param name="comparer">
    ///  Custom comparer dictating the ordering of elements.
    ///  Uses <see cref="Comparer{T}.Default" /> if the argument is <see langword="null"/>.
    /// </param>
    public ConcurrentPriorityQueue(IComparer<TPriority>? comparer) =>
        _queue = new PriorityQueue<TElement, TPriority>(comparer);

    /// <summary>
    ///  Initializes a new instance of the <see cref="PriorityQueue{TElement, TPriority}"/> class with thread safety
    ///  with the specified initial capacity and custom priority comparer.
    /// </summary>
    /// <param name="initialCapacity">Initial capacity to allocate in the underlying heap array.</param>
    /// <param name="comparer">
    ///  Custom comparer dictating the ordering of elements.
    ///  Uses <see cref="Comparer{T}.Default" /> if the argument is <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///  The specified <paramref name="initialCapacity"/> was negative.
    /// </exception>
    public ConcurrentPriorityQueue(int initialCapacity, IComparer<TPriority>? comparer = null) =>
        _queue = new PriorityQueue<TElement, TPriority>(initialCapacity, comparer);

    /// <summary>
    ///  Initializes a new instance of the <see cref="PriorityQueue{TElement, TPriority}"/> class
    ///  that is populated with the specified elements and priorities,
    ///  and with the specified custom priority comparer.
    /// </summary>
    /// <param name="items">The pairs of elements and priorities with which to populate the queue.</param>
    /// <param name="comparer">
    ///  Custom comparer dictating the ordering of elements.
    ///  Uses <see cref="Comparer{T}.Default" /> if the argument is <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    ///  The specified <paramref name="items"/> argument was <see langword="null"/>.
    /// </exception>
    /// <remarks>
    ///  Constructs the heap using a heapify operation,
    ///  which is generally faster than enqueuing individual elements sequentially.
    /// </remarks>
    public ConcurrentPriorityQueue(IEnumerable<(TElement Element, TPriority Priority)> items,
                                   IComparer<TPriority>? comparer = null) =>
        _queue = new PriorityQueue<TElement, TPriority>(items, comparer);


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
}
