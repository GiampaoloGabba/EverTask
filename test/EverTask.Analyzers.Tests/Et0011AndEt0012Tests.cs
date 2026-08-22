using Xunit;

namespace EverTask.Analyzers.Tests;

using Verify = CSharpAnalyzerVerifier<HandlerRegistrationAnalyzer>;

/// <summary>
/// ET0011 (open-generic handler is skipped by the assembly scan) and ET0012 (multiple handlers for
/// the same closed task contract in one compilation) — the compile-time mirror of the
/// HandlerRegistrar G1/G2 startup warnings.
/// </summary>
public class Et0011AndEt0012Tests
{
    // The analyzer resolves IEverTaskHandler`1 by metadata name; a source stub with the same shape
    // (contravariant, IEverTask-constrained) plus the abstract base is enough.
    private const string HandlerStubs = """
        namespace EverTask.Abstractions
        {
            public interface IEverTaskHandler<in TTask> where TTask : IEverTask { }

            public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask { }
        }
        """;

    [Fact]
    public Task Closed_handler_is_clean()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record PingTask : IEverTask;
            public class PingHandler : EverTaskHandler<PingTask> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Open_generic_handler_reports_et0011()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record WrapTask<T>(T Value) : IEverTask;
            public class {|ET0011:OpenHandler|}<T> : EverTaskHandler<WrapTask<T>> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Open_generic_handler_with_fixed_closed_contract_reports_et0011_once()
    {
        // Even the fully closed contract is unreachable: the handler type itself stays open.
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record FixedTask : IEverTask;
            public record WrapTask<T>(T Value) : IEverTask;

            public class {|ET0011:MixedOpenHandler|}<T> : EverTaskHandler<WrapTask<T>>, IEverTaskHandler<FixedTask> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Abstract_open_generic_base_is_silent()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public abstract class HandlerBase<TTask> : EverTaskHandler<TTask> where TTask : IEverTask { }

            public record RealTask : IEverTask;
            public class RealHandler : HandlerBase<RealTask> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Open_generic_non_handler_is_silent()
    {
        return Verify.VerifyAsync("""
            public class OpenHelper<T>
            {
                public T Get() => default!;
            }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Duplicate_handlers_report_et0012_on_both()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record OrderTask : IEverTask;

            public class {|ET0012:FirstOrderHandler|} : EverTaskHandler<OrderTask> { }
            public class {|ET0012:SecondOrderHandler|} : EverTaskHandler<OrderTask> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Distinct_tasks_are_clean()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record TaskA : IEverTask;
            public record TaskB : IEverTask;

            public class HandlerA : EverTaskHandler<TaskA> { }
            public class HandlerB : EverTaskHandler<TaskB> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Contravariant_base_task_handler_is_not_a_duplicate()
    {
        // IEverTaskHandler<in TTask>: the BASE task's handler is assignable to the DERIVED task's
        // interface, but grouping is by exact identity — no false ET0012.
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record BaseTask : IEverTask;
            public record DerivedTask : BaseTask;

            public class BaseHandler : EverTaskHandler<BaseTask> { }
            public class DerivedHandler : EverTaskHandler<DerivedTask> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Multi_interface_handler_reports_only_the_contested_contract()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record TaskA : IEverTask;
            public record TaskB : IEverTask;

            public class {|ET0012:MultiHandler|} : EverTaskHandler<TaskA>, IEverTaskHandler<TaskB> { }
            public class {|ET0012:OtherBHandler|} : EverTaskHandler<TaskB> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Partial_handler_is_collected_once_and_flagged_at_its_first_declaration()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record OrderTask : IEverTask;

            public partial class {|ET0012:PartHandler|} : EverTaskHandler<OrderTask> { }
            public partial class PartHandler { }
            public class {|ET0012:OtherHandler|} : EverTaskHandler<OrderTask> { }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Same_named_handlers_in_different_namespaces_are_both_flagged()
    {
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record SharedTask : IEverTask;

            namespace ModuleA
            {
                public class {|ET0012:SharedHandler|} : EverTask.Abstractions.EverTaskHandler<SharedTask> { }
            }

            namespace ModuleB
            {
                public class {|ET0012:SharedHandler|} : EverTask.Abstractions.EverTaskHandler<SharedTask> { }
            }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Handler_nested_in_generic_container_reports_et0011()
    {
        // Nested in a generic container: the runtime scan sees ContainsGenericParameters and
        // skips it, so the analyzer must flag it even though the handler itself declares no
        // type parameters.
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record PingTask : IEverTask;

            public class Container<T>
            {
                public class {|ET0011:InnerHandler|} : EverTaskHandler<PingTask> { }
            }
            """,
            extraSource: HandlerStubs);
    }

    [Fact]
    public Task Duplicate_closed_constructions_of_generic_task_are_independent()
    {
        // WrapTask<int> and WrapTask<string> are distinct exact contracts: no ET0012.
        return Verify.VerifyAsync("""
            using EverTask.Abstractions;

            public record WrapTask<T>(T Value) : IEverTask;

            public class IntHandler : EverTaskHandler<WrapTask<int>> { }
            public class StringHandler : EverTaskHandler<WrapTask<string>> { }
            """,
            extraSource: HandlerStubs);
    }
}
