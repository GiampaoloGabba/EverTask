using System.Reflection;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// The guardian of the one rule <see cref="FaultInjectingTaskStorage"/> lives by: it forwards EVERY member of
/// <see cref="ITaskStorage"/> to the inner store.
/// </summary>
/// <remarks>
/// A member left out does not fail to compile — the interface's own default body runs instead, which for the
/// atomic operations is a <c>NotSupportedException</c> and for the reads an unindexed composition over
/// another member. A test that wraps a real store then exercises the interface's default rather than the
/// store it was pointed at, and the operation cannot be armed with a fault at all: nothing calls
/// <c>Gate</c> in front of it. That is how <c>GetOccurrencesPage</c> reached the interface, the four storages
/// and three of the four sibling readers of this wrapper without reaching this one.
/// </remarks>
public class FaultInjectingTaskStorageContractTests
{
    [Fact]
    public void Should_forward_every_member_of_the_storage_interface_to_the_inner_store()
    {
        var missing = MembersNotDeclaredOn(typeof(FaultInjectingTaskStorage));

        missing.ShouldBeEmpty(
            "every member has to be forwarded explicitly, or the interface's own default runs against the " +
            "wrapper instead of the store it decorates, and no test can inject a fault into it: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void Should_notice_a_storage_that_leaves_the_default_members_alone()
    {
        // The premise of the test above. TestTaskStorage implements only what the interface makes mandatory
        // and lets every default member be, so "nothing missing" is proven to mean "everything is forwarded"
        // rather than "the reflection matched nothing".
        MembersNotDeclaredOn(typeof(TestTaskStorage)).ShouldNotBeEmpty();
    }

    private static string[] MembersNotDeclaredOn(Type type) =>
        typeof(ITaskStorage)
            .GetMethods()
            .Where(m => type.GetMethod(m.Name,
                       BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                       binder: null,
                       m.GetParameters().Select(p => p.ParameterType).ToArray(),
                       modifiers: null) is null)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
