using EverTask;
using Shouldly;
using Xunit;

namespace EverTask.Tests.Storage;

/// <summary>
/// Focused tests for the audit-retention policy surface touched by M6 (G5/G6).
/// The cross-provider cleanup behavior (age gate + execution-log preservation) lives in
/// <c>EfCoreTaskStorageTestsBase</c> so it runs on every storage provider.
/// </summary>
public class AuditCleanupRetentionTests
{
    [Fact]
    public void Obsolete_DeleteCompletedTasksWithAudits_should_forward_to_new_property()
    {
        var policy = new AuditRetentionPolicy();

#pragma warning disable CS0618 // exercising the deprecated alias on purpose
        // Setting the deprecated alias flows into the renamed property...
        policy.DeleteCompletedTasksWithAudits = true;
        policy.DeleteCompletedTasksAfterRetention.ShouldBeTrue();

        // ...and reading the alias reflects the renamed property.
        policy.DeleteCompletedTasksAfterRetention = false;
        policy.DeleteCompletedTasksWithAudits.ShouldBeFalse();
#pragma warning restore CS0618
    }

    [Fact]
    public void DeleteCompletedTasksAfterRetention_should_default_to_false()
    {
        new AuditRetentionPolicy().DeleteCompletedTasksAfterRetention.ShouldBeFalse();
        AuditRetentionPolicy.WithUniformRetention(30).DeleteCompletedTasksAfterRetention.ShouldBeFalse();
    }
}
