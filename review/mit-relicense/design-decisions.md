# MIT Relicense — decisions & outcomes log

Companion to `design-brief.md`. Chronological record of the Claude ⇄ Codex (xhigh) design dialogue
and of the verification evidence. Status: implementation complete, adversarial review in progress.

## Design round 1 (Codex xhigh, thread 01a02aa1-23d8-7313-aa65-343710f20a09)

- **Q1 — ReflectionTypeLoadException**: fail-fast, let it propagate (an explicitly requested,
  uninspectable assembly is a deployment failure; skipping would silently drop handlers).
  Registration happens only after discovery completes, so a throw leaves the ServiceCollection
  untouched. ACCEPTED.
- **Q2 — the key finding**: `IsMatchingWithInterface` was NOT dead logic. `IEverTaskHandler<in TTask>`
  is **contravariant**, so `IsAssignableFrom` admits a base-task handler as a candidate for every
  derived task's interface; the filter compensated. The rewrite groups by **exact closed interface
  Type identity** (never assignability), making both helpers unnecessary. Verified against the
  source: the interface is declared `in TTask`. ACCEPTED.
- **Q3 — losing duplicates stay self-registered** (concrete type, TryAddTransient): a persisted row
  can carry the AssemblyQualifiedName of a handler that lost the scan only after a redeploy
  reordered types; lazy resolution must still find it. ACCEPTED.
- **Q4 — algorithm**: single DefinedTypes pass → `Dictionary<Type, HandlerBucket>` keyed by exact
  interface + ordered bucket list (first-wins = assembly order, then DefinedTypes order);
  registration pass afterwards. G1 warned once per open handler during the scan, G2 once per
  contract during registration. Structurally distinct from MediatR (no template discovery, no
  recursive helper chain, no assignability recomputation). ACCEPTED.
- **Q6 — benchmark**: scratchpad-only, never in the repo. ACCEPTED.

## Design round 2 (convergence, same thread)

- DELTA-1 ACK: cross-assembly ordering + dynamic-assembly tests via **Reflection.Emit** (two Run
  assemblies deriving `EverTaskHandler<SharedOrderingTask>`, public default ctor, Handle override
  emitted as Public|Virtual|HideBySig, unique assembly names) instead of fixture csproj projects.
- DELTA-2 ACK: no struct/record-struct handler tests (identical registrar path, heavy fixtures).
- DELTA-3 ACK: G1/G2 warning formats pinned by exact-string assertions in two dedicated tests.
- Implementation review: **no behavioral findings**; two comment-wording fixes applied.

## Implementation & verification evidence

- New `HandlerRegistrar.cs`: ~130 lines incl. docs, zero MediatR expression.
- Tests: 42 green on the registration surface (15 AssemblyResolutionTests incl. renames +
  27 new HandlerRegistrarTests: exact-identity/contravariance, duplicates ×3 with computed
  DefinedTypes order, multi-interface, inheritance via open-generic abstract base, closed
  constructed-generic contracts, nested private, G1 variants incl. open handler with fixed closed
  contract, exact G1/G2 strings, TryAdd preservation, null warnings, double scan, double
  AddEverTask, Moq'd ReflectionTypeLoadException propagation, Reflection.Emit cross-assembly
  ordering both directions + dynamic assembly resolution).
- **Full Docker-free suite: green on net8/net9/net10** (core 1235×3; storage SQLite 141-143×3;
  monitoring green after copying the git-ignored `wwwroot` UI build into the fresh worktree — the
  only 2 fails were environmental, SPA 404 without the vite build, unrelated to the change).
- **Benchmark** (scratchpad, 1000 iter × 5 interleaved rounds, median, scanning the pre-change
  EverTask.Tests assembly; identical output 193 descriptors both):
  - OLD 1064.25 µs/op, 699,630 B/op → NEW **74.09 µs/op (-93.0%), 80,494 B/op (-88.5%)**.
- **Latent P0/P1 bug in the OLD code, proven live**: scanning an assembly containing a
  multi-interface handler plus a duplicate on one of its contracts (the new fixture set) makes the
  OLD registrar **crash at startup** with `AmbiguousMatchException` in `IsMatchingWithInterface` →
  `Type.GetInterface("IEverTaskHandler`1")` (ambiguous when a type implements two closed forms).
  Codex predicted exactly this fragility in round 1; the rewrite is immune (exact Type identity,
  no name-based relookup).

## License chores applied (worktree EverTask-mit, branch feature/mit-license)

LICENSE → MIT; `Directory.Build.props` PackageLicenseExpression → MIT; README license section +
MediatR acknowledgement line (ATTRIBUTION link removed); `docs/_config.yml` footer; plugin.json;
CLAUDE.md attribution rule removed; attribution headers removed from Dispatcher.cs,
TaskHandlerExecutor.cs, TaskHandlerWrapper.cs (git diff verified: comment-only);
ATTRIBUTION.md deleted. `Dispatcher.CreateCachedWrapper` inspected: already structurally distinct
(compiled Expression factory), no rewrite needed.
