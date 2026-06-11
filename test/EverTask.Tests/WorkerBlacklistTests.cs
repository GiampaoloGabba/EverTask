using EverTask.Worker;

namespace EverTask.Tests;

/// <summary>
/// Unit tests for <see cref="WorkerBlacklist"/>, including the TTL sweep that prevents
/// entries from leaking when a cancelled task's occurrence is never delivered to a worker
/// (e.g. it was parked in the scheduler and unscheduled by Cancel).
/// </summary>
public class WorkerBlacklistTests
{
    [Fact]
    public void Should_blacklist_and_remove_entries()
    {
        var blacklist = new WorkerBlacklist();
        var id = Guid.NewGuid();

        blacklist.IsBlacklisted(id).ShouldBeFalse();

        blacklist.Add(id);
        blacklist.IsBlacklisted(id).ShouldBeTrue();

        blacklist.Remove(id);
        blacklist.IsBlacklisted(id).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_sweep_expired_entries_when_adding()
    {
        var blacklist = new WorkerBlacklist { EntryTtl = TimeSpan.FromMilliseconds(50) };

        var expired = Guid.NewGuid();
        blacklist.Add(expired);
        blacklist.IsBlacklisted(expired).ShouldBeTrue();

        await Task.Delay(150);

        // The sweep runs on Add: a new insertion evicts the lapsed entry
        var fresh = Guid.NewGuid();
        blacklist.Add(fresh);

        blacklist.IsBlacklisted(expired).ShouldBeFalse();
        blacklist.IsBlacklisted(fresh).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_not_sweep_entries_still_within_ttl()
    {
        var blacklist = new WorkerBlacklist { EntryTtl = TimeSpan.FromHours(1) };

        var first = Guid.NewGuid();
        blacklist.Add(first);

        await Task.Delay(50);

        blacklist.Add(Guid.NewGuid());

        blacklist.IsBlacklisted(first).ShouldBeTrue();
    }

    [Fact]
    public void Should_refresh_timestamp_when_re_adding_same_entry()
    {
        var blacklist = new WorkerBlacklist { EntryTtl = TimeSpan.FromHours(1) };
        var id = Guid.NewGuid();

        blacklist.Add(id);
        blacklist.Add(id);

        blacklist.IsBlacklisted(id).ShouldBeTrue();
    }
}
