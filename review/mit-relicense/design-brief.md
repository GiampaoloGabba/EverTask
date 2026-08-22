# MIT Relicense — HandlerRegistrar clean-room rewrite: design brief

**Goal.** EverTask moves from Apache-2.0 to MIT ("pure MIT", no third-party notices). The only file
with substantial expression derived from MediatR's `ServiceRegistrar` is
`src/EverTask/MicrosoftExtensionsDI/HandlerRegistrar.cs`. It must be rewritten with **genuinely new
expression and structure** (clean-room quality: no `FindInterfacesThatClose` / `CanBeCastTo` /
`Fill` helper zoo, no recursive base-type walk copied from MediatR), while preserving the exact
observable behavior pinned by the existing tests. Three other files (`Dispatcher.cs`,
`TaskHandlerExecutor.cs`, `TaskHandlerWrapper.cs`) only carry attribution headers and are already
divergent — they are out of scope except for header removal.

This document is the shared design workspace between Claude (orchestrator/implementer) and Codex
(co-designer, xhigh). Codex proposes the design; Claude implements; both adversarially review.

## Current observable contract (MUST be preserved)

Entry point (internal, called once at startup from `ServiceCollectionExtensions.AddEverTaskHandlers`):

```csharp
namespace Microsoft.Extensions.DependencyInjection;   // keep this namespace

internal static class HandlerRegistrar
{
    public static void RegisterConnectedImplementations(
        IServiceCollection services,
        IEnumerable<Assembly> assembliesToScan,          // caller already .Distinct()s
        ICollection<string>? warnings = null);           // -> EverTaskServiceConfiguration.HandlerRegistrationWarnings
}
```

Behavior:

1. **Discovery**: every type in `assembly.DefinedTypes` (includes `internal` types) that is
   concrete (non-abstract, non-interface) and implements one or more **closed**
   `IEverTaskHandler<TTask>` interfaces — including indirectly via a base class
   (`EverTaskHandler<T>` abstract base is the normal case; `Type.GetInterfaces()` already
   flattens inherited interfaces, no recursive walk needed).
2. **G1 — open generics**: a type that is a generic type definition / has open type parameters and
   implements `IEverTaskHandler<>` is NOT registered; a warning is recorded whose text contains the
   type name (test matches `Contains("OpenGenericRegistrationHandler")`; current text uses
   `type.FullName`).
3. **G2 — duplicate closed handlers**: multiple concrete types for the same closed interface →
   first-wins (deterministic: assembly/DefinedTypes discovery order), warning containing the task
   type name (`GenericTypeArguments[0].Name`) and the ignored handler names. First-wins is enforced
   via `TryAddTransient`.
4. **Registrations** (both, transient, TryAdd semantics):
   - `TryAddTransient(closedInterface, concreteType)` — used by eager resolution
     (`TaskHandlerWrapperImp`), and by lazy fallback.
   - `TryAddTransient(concreteType, concreteType)` — used by lazy resolution
     (`TaskHandlerExecutor.GetOrResolveHandler` resolves by AssemblyQualifiedName) and by the eager
     G3 concrete-type re-resolution. **Note:** current code self-registers ALL duplicates'
     concrete types (not just the winner). Keep or drop? → design decision, see Q3.
5. A handler implementing multiple closed `IEverTaskHandler<>` interfaces (multi-task handler) is
   registered under each interface.
6. No exceptions thrown for any discovery anomaly; warnings only. `warnings` may be null (skip
   recording, behavior otherwise identical).

Dead code to DROP: `CouldCloseTo` extension (unused by the flow; only referenced by two tests in
`AssemblyResolutionTests.cs`, which will be removed/replaced), `IsMatchingWithInterface` (its
filtering effect must be reasoned about — see Q2).

Pinning tests today: `test/EverTask.Tests/AssemblyResolutionTests.cs` (resolution of handlers incl.
internal, duplicate first-wins + G2 warning, G1 warning), `TestTasks.Registration.cs` (fixtures),
plus the whole integration suite exercising eager+lazy resolution.

## Performance goals

Startup-only, but user asks for a tight implementation: single pass over `DefinedTypes`, one
`GetInterfaces()` call per candidate type, no LINQ in the per-type loop, minimal allocations
(pre-sized collections where sensible), no static caches (one-shot code). Target ~60-90 lines.

## Constraints

- TFMs net8.0/net9.0/net10.0, nullable enabled, warnings-as-errors, C# 12 (primary constructors
  N/A here), file-scoped namespace, repo style (aligned assignments, comment density like the
  current file, log-free — warnings list is the only diagnostic channel).
- No public API change; no behavior change observable by the test suite except where this brief
  explicitly allows it (dead-code tests).
- New expression only: do not mirror MediatR's method decomposition, names, or control flow.

## Open design questions for Codex (answer each explicitly)

- **Q1 — ReflectionTypeLoadException resilience**: `assembly.DefinedTypes` can throw for partially
  loadable assemblies. Current code does not handle it. Add `try/catch → warning + skip`, or keep
  strict parity (let it throw)? Recommend one.
- **Q2 — `IsMatchingWithInterface` filter**: in current code, when >1 exact matches, it removes
  candidates whose generic arguments don't match the interface's. For closed (non-generic) concrete
  handlers this filter is a no-op in practice (matches reach it only if `CanBeCastTo` passed).
  Confirm or refute that dropping it changes nothing observable; define the exact first-wins rule.
- **Q3 — self-registration of losing duplicates**: keep (parity: a persisted lazy task whose
  stored AQN points at the "losing" handler can still resolve) or drop (stricter)? Recommend with
  rationale; consider recovery of old persisted rows.
- **Q4 — algorithm & data structures**: propose the concrete shape (e.g. single pass filling
  `Dictionary<Type, List<Type>>` keyed by closed interface, ordered; then a registration pass).
  Include the warning-message formats (must satisfy the Contains-based tests).
- **Q5 — additional tests**: enumerate the new unit tests the rewrite should ship (multi-interface
  handler, inherited handler via intermediate base, internal handler, duplicate across two
  assemblies, null warnings collection, abstract base excluded, struct/record edge cases, TryAdd
  idempotence on double AddEverTask call, ...).
- **Q6 — micro-benchmark**: sensible before/after benchmark shape for a startup-only API
  (iterate N× over the test assembly with a fresh ServiceCollection; measure time + allocated
  bytes). Worth shipping in repo or scratchpad-only? Recommend.

## Out of scope (handled separately by Claude)

License chores: LICENSE → MIT, `Directory.Build.props` PackageLicenseExpression, README badge +
"inspired by MediatR" acknowledgement line, ATTRIBUTION.md removal, CLAUDE.md attribution rule
removal, attribution header removal in the four files, light restructure of
`Dispatcher.CreateCachedWrapper`.
