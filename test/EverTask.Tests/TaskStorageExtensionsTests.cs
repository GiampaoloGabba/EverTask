using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// Tests for <see cref="TaskStorageExtensions.GetLogsAsync(ITaskStorage, Guid, CancellationToken)"/> and its
/// paginated overload, over the real <see cref="MemoryTaskStorage"/>: argument validation, page arithmetic
/// and sequence ordering.
/// </summary>
public class TaskStorageExtensionsTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private async Task<Guid> PersistTaskWithLogs(int logCount)
    {
        var taskId = Guid.NewGuid();
        await _storage.Persist(new QueuedTask
        {
            Id           = taskId,
            Type         = "TestType",
            Request      = "{}",
            Handler      = "TestHandler",
            Status       = QueuedTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // Ids ascend with the sequence, as the UUIDv7 generator produces them in production; the list is
        // saved in reverse so the assertions prove ordering comes from the query, not from insertion.
        var logs = Enumerable.Range(1, logCount).Reverse().Select(seq => new TaskExecutionLog
        {
            Id             = new Guid(seq, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
            TaskId         = taskId,
            TimestampUtc   = DateTimeOffset.UtcNow,
            Level          = "Information",
            Message        = $"log {seq}",
            SequenceNumber = seq
        }).ToList();

        await _storage.SaveExecutionLogsAsync(taskId, logs, CancellationToken.None);
        return taskId;
    }

    [Fact]
    public async Task Should_return_all_logs_ordered_by_sequence_number()
    {
        var taskId = await PersistTaskWithLogs(5);

        var logs = await _storage.GetLogsAsync(taskId);

        logs.Count.ShouldBe(5);
        logs.Select(l => l.SequenceNumber).ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Should_return_empty_list_when_task_has_no_logs()
    {
        var logs = await _storage.GetLogsAsync(Guid.NewGuid());

        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_page_logs_with_one_based_page_numbers()
    {
        var taskId = await PersistTaskWithLogs(5);

        var firstPage  = await _storage.GetLogsAsync(taskId, pageNumber: 1, pageSize: 2);
        var secondPage = await _storage.GetLogsAsync(taskId, pageNumber: 2, pageSize: 2);
        var lastPage   = await _storage.GetLogsAsync(taskId, pageNumber: 3, pageSize: 2);

        firstPage.Select(l => l.SequenceNumber).ShouldBe([1, 2]);
        secondPage.Select(l => l.SequenceNumber).ShouldBe([3, 4]);
        lastPage.Select(l => l.SequenceNumber).ShouldBe([5]);
    }

    [Fact]
    public async Task Should_return_empty_page_when_page_number_is_past_the_end()
    {
        var taskId = await PersistTaskWithLogs(3);

        var page = await _storage.GetLogsAsync(taskId, pageNumber: 5, pageSize: 10);

        page.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_throw_when_storage_is_null()
    {
        ITaskStorage storage = null!;

        await Should.ThrowAsync<ArgumentNullException>(() => storage.GetLogsAsync(Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentNullException>(() => storage.GetLogsAsync(Guid.NewGuid(), 1, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Should_throw_when_page_number_is_below_one(int pageNumber)
    {
        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _storage.GetLogsAsync(Guid.NewGuid(), pageNumber, 10));

        ex.ParamName.ShouldBe("pageNumber");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Should_throw_when_page_size_is_out_of_range(int pageSize)
    {
        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _storage.GetLogsAsync(Guid.NewGuid(), 1, pageSize));

        ex.ParamName.ShouldBe("pageSize");
    }

    [Fact]
    public async Task Should_accept_the_maximum_page_size()
    {
        var taskId = await PersistTaskWithLogs(3);

        var page = await _storage.GetLogsAsync(taskId, pageNumber: 1, pageSize: 1000);

        page.Count.ShouldBe(3);
    }
}
