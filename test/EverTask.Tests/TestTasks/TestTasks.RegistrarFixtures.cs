// Fixture files under TestTasks/ share the flat EverTask.Tests namespace, like every other
// TestTasks.*.cs file in this project.
// ReSharper disable once CheckNamespace
namespace EverTask.Tests;

// Fixtures for HandlerRegistrarTests (assembly-scanning discovery/registration).
// NOTE: these types are deliberately part of the shared test assembly, so every AddEverTask scan
// in the suite sees them (exactly like TestTaskHanlderDuplicate already does). Handlers are inert.

// ── Contravariance: IEverTaskHandler<in TTask> makes a base-task handler assignable to the
//    derived task's interface. The scanner must group by EXACT interface identity and never let
//    CtvBaseTaskHandler become a candidate for CtvDerivedTask. ─────────────────────────────────

public record CtvBaseTask : IEverTask;

public record CtvDerivedTask : CtvBaseTask;

public class CtvBaseTaskHandler : EverTaskHandler<CtvBaseTask>
{
    public override Task Handle(CtvBaseTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class CtvDerivedTaskHandler : EverTaskHandler<CtvDerivedTask>
{
    public override Task Handle(CtvDerivedTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// ── Multi-interface handler: one concrete type serving two task contracts (base class for the
//    first, direct interface implementation for the second), plus a competitor on the second ───

public record MultiTaskA : IEverTask;

public record MultiTaskB : IEverTask;

public class MultiTaskHandler : EverTaskHandler<MultiTaskA>, IEverTaskHandler<MultiTaskB>
{
    public override Task Handle(MultiTaskA backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task Handle(MultiTaskB backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    // The base implements SetLogCapture explicitly, so it only satisfies ITS closed interface.
    void IEverTaskHandler<MultiTaskB>.SetLogCapture(ITaskLogCapture logCapture) { }
}

public class MultiTaskBOtherHandler : EverTaskHandler<MultiTaskB>
{
    public override Task Handle(MultiTaskB backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// ── Inheritance through an intermediate OPEN GENERIC ABSTRACT base: the base itself must stay
//    silent (no G1 warning, no registration); the closed concrete descendant registers ─────────

public abstract class IntermediateHandlerBase<TTask> : EverTaskHandler<TTask> where TTask : IEverTask;

public record InheritedViaBaseTask : IEverTask;

public class InheritedViaBaseHandler : IntermediateHandlerBase<InheritedViaBaseTask>
{
    public override Task Handle(InheritedViaBaseTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// ── Closed handler for a CONSTRUCTED generic task type (the task definition is generic, the
//    handler's contract is fully closed) ──────────────────────────────────────────────────────

public record GenericPayloadTask<T>(T Payload) : IEverTask;

public class GenericPayloadIntHandler : EverTaskHandler<GenericPayloadTask<int>>
{
    public override Task Handle(GenericPayloadTask<int> backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// ── Three handlers for the same task: first-wins by DefinedTypes order, the G2 warning lists
//    every loser in discovery order. AbstractClosedHandler must not count as a candidate. ──────

public record TriplicateTask : IEverTask;

public class TriplicateHandlerOne : EverTaskHandler<TriplicateTask>
{
    public override Task Handle(TriplicateTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class TriplicateHandlerTwo : EverTaskHandler<TriplicateTask>
{
    public override Task Handle(TriplicateTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class TriplicateHandlerThree : EverTaskHandler<TriplicateTask>
{
    public override Task Handle(TriplicateTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public abstract class AbstractClosedHandler : EverTaskHandler<TriplicateTask>;

// ── Concrete OPEN generic handler implementing TWO handler contracts (one open, one fixed and
//    closed): exactly one G1 warning, and even the closed contract must NOT be registered
//    because the handler type itself still has open type parameters ───────────────────────────

public record OpenMultiFixedTask : IEverTask;

public class OpenMultiHandler<T> : EverTaskHandler<OpenGenericRegistrationTask<T>>,
                                   IEverTaskHandler<OpenMultiFixedTask>
{
    public override Task Handle(OpenGenericRegistrationTask<T> backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task Handle(OpenMultiFixedTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    void IEverTaskHandler<OpenMultiFixedTask>.SetLogCapture(ITaskLogCapture logCapture) { }
}

// ── Not a handler at all: must produce no descriptors and no warnings ─────────────────────────

public class NotAHandlerService;

// ── Open generic that is NOT a handler: must stay silent too (pins that the G1 warning requires
//    BOTH an open type AND a matching handler interface — the isHandler flag) ─────────────────

public class OpenGenericNotAHandler<T>
{
    public T? Value { get; set; }
}

// ── Nested private handler lives inside HandlerRegistrarTests (DefinedTypes includes nested
//    types; accessibility is not a discovery filter) ─────────────────────────────────────────

public record NestedPrivateTask : IEverTask;

// ── Shared task for the Reflection.Emit cross-assembly ordering tests: must be PUBLIC so the
//    dynamically emitted handler assemblies (no friend access) can close EverTaskHandler<T>
//    over it. No static handler exists on purpose. ───────────────────────────────────────────

public record SharedOrderingTask : IEverTask;

// ── Task nested in a GENERIC container + duplicate handlers: pins the G2 formatter's
//    declaring-chain rendering and generic-argument partitioning (Outer<Int32>.InnerTask,
//    with the argument attributed to the outer type, not the inner) ──────────────────────────

public class NestedTaskContainer<T>
{
    public record InnerTask(T Payload) : IEverTask;
}

public class NestedIntHandlerOne : EverTaskHandler<NestedTaskContainer<int>.InnerTask>
{
    public override Task Handle(NestedTaskContainer<int>.InnerTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class NestedIntHandlerTwo : EverTaskHandler<NestedTaskContainer<int>.InnerTask>
{
    public override Task Handle(NestedTaskContainer<int>.InnerTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
