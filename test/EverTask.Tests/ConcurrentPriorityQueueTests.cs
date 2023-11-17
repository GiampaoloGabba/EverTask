using EverTask.Scheduler;

namespace EverTask.Tests;

public class ConcurrentPriorityQueueTests
{
    [Fact]
    public async Task Should_enqueue_Items_when_called_from_multiple_Threads()
    {
        var queue         = new ConcurrentPriorityQueue<int, int>();
        var tasks         = new List<Task>();
        int numberOfItems = 1000;

        for (int i = 0; i < numberOfItems; i++)
        {
            int localI = i;
            tasks.Add(Task.Run(() => queue.Enqueue(localI, localI)));
        }

        await Task.WhenAll(tasks);

        // Verifica che tutti gli elementi siano stati inseriti
        numberOfItems.ShouldBe(queue.Count);
    }

    [Fact]
    public async Task Should_EnqueueRange_Items_when_called_from_multiple_Threads()
    {
        var queue         = new ConcurrentPriorityQueue<int, int>();
        var tasks         = new List<Task>();
        int numberOfItems = 1000;

        // Enqueue range of items in tasks
        for (int i = 0; i < numberOfItems; i += 10)
        {
            var range = Enumerable.Range(i, 10).Select(j => (j, j));
            tasks.Add(Task.Run(() => queue.EnqueueRange(range)));
        }

        await Task.WhenAll(tasks);

        // Verify if all items are enqueued
        queue.Count.ShouldBe(numberOfItems);
    }

    [Fact]
    public async Task Should_Dequeue_Items_when_called_from_multiple_Threads()
    {
        var queue         = new ConcurrentPriorityQueue<int, int>();
        var tasks         = new List<Task>();
        int numberOfItems = 1000;

        // Enqueue items
        for (int i = 0; i < numberOfItems; i++)
        {
            queue.Enqueue(i, i);
        }

        // Dequeue items in tasks
        for (int i = 0; i < numberOfItems; i++)
        {
            tasks.Add(Task.Run(() => queue.Dequeue()));
        }

        await Task.WhenAll(tasks);

        // Verify if all items are dequeued
        queue.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Should_TryDequeue_Items_when_called_from_multiple_Threads()
    {
        var queue         = new ConcurrentPriorityQueue<int, int>();
        var tasks         = new List<Task>();
        int numberOfItems = 1000;

        // Enqueue items
        for (int i = 0; i < numberOfItems; i++)
        {
            queue.Enqueue(i, i);
        }

        // TryDequeue items in tasks
        for (int i = 0; i < numberOfItems; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                queue.TryDequeue(out var item, out _);
            }));
        }

        await Task.WhenAll(tasks);

        // Verify if all items are dequeued
        queue.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Peek_Items_when_called_from_multiple_Threads()
    {
        var queue         = new ConcurrentPriorityQueue<int, int>();
        var tasks         = new List<Task>();
        int numberOfItems = 1000;

        // Enqueue items
        for (int i = 0; i < numberOfItems; i++)
        {
            queue.Enqueue(i, i);
        }

        // Peek items in tasks
        for (int i = 0; i < numberOfItems; i++)
        {
            tasks.Add(Task.Run(() => queue.Peek()));
        }

        await Task.WhenAll(tasks);

        // Verify if all items are peeked and still in the queue
        queue.Count.ShouldBe(numberOfItems);
    }

    [Fact]
    public async Task Should_TryPeek_Items_when_called_from_multiple_Threads()
    {
        var queue         = new ConcurrentPriorityQueue<int, int>();
        var tasks         = new List<Task>();
        int numberOfItems = 1000;

        // Enqueue items
        for (int i = 0; i < numberOfItems; i++)
        {
            queue.Enqueue(i, i);
        }

        // TryPeek items in tasks
        for (int i = 0; i < numberOfItems; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                queue.TryPeek(out var item, out _);
            }));
        }

        await Task.WhenAll(tasks);

        // Verify if all items are peeked and still in the queue
        queue.Count.ShouldBe(numberOfItems);
    }

    private void EnqueueItems(ConcurrentPriorityQueue<int, int> queue, int numberOfItems)
    {
        for (int i = 0; i < numberOfItems; i++)
        {
            queue.Enqueue(i, i);
        }
    }

    private List<Task> CreateTasks(Action action, int numberOfTasks)
    {
        var tasks = new List<Task>();
        for (int i = 0; i < numberOfTasks; i++)
        {
            tasks.Add(Task.Run(action));
        }
        return tasks;
    }
}
