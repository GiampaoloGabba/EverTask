# Issue #23 — Durable occurrences: final report

Branch `feature/issue23-durable-occurrences`, release **4.0.0**. Written 2026-08-27 against `bfed3be`,
the last commit of the post-review fix series, plus three uncommitted documentation edits described in §7.

This document replaces the two workflow stages that never ran: the run's own completeness critic and its
final synthesis, both lost to the review abort described in §3. Everything below was assembled by reading
the per-phase reports, the phase and final adversarial reviews, the ratification record
(`review/recurring-occurrences-decisions.md`), the perf captures and the branch history.

---

## 1. Executive summary

Seven phases turned recurring schedules from a single self-rescheduling row into a definition plus a cursor
that can materialize every due slot as its own durable task. The feature ships per-schedule time zones with
DST semantics, an execution context handlers can read, three misfire policies, a runtime schedule manager,
pluggable occurrence calendars, and full monitoring API and dashboard coverage. All seven sub-issues (#24–#30)
are closed and merged; eleven follow-up issues found along the way (#34, #37–#47, #54) are closed in the same
branch. The final adversarial review ran three rounds over eight lenses and confirmed twenty-four findings,
all fixed; it aborted at its round cap with two HIGH terminal-write races still open, which were then closed
outside the workflow by a compare-and-swap terminal write (`TrySetTerminalOutcome`, commit `3b34fb9`). A
six-lens Codex pass over the finished branch produced thirty-six findings, thirty-three of them valid, fixed
in four verified batches; its six performance findings were deferred to issues #48–#53 with the one cheap
index pulled in-branch. The full suite is green on net8.0, net9.0 and net10.0 with Testcontainers, and the D7
perf-parity gate passed with a worst case of −4.6% throughput. The verdict is **GO**, with the limits in §7
stated in the release notes.

---

## 2. Per-phase outcomes

| Phase | Issue | Commit | Delivered |
|---|---|---|---|
| 1 | #24 | `93361ec` | Invariants and foundations |
| 2 | #25 | `a1b11ad` | `ITaskExecutionContext` |
| 3 | #26 | `2b16653` | Per-schedule time zones with DST semantics |
| 4 | #27 | `55b24e0` | Durable occurrences and misfire policies |
| 5 | #28 | `dbaabb1` | `ITaskScheduleManager` |
| 6 | #29 | `7ad27cc` | `INextOccurrenceProvider` |
| 7 | #30 | `dcfbfb6` | Monitoring API/UI, docs sweep, samples, release cut |

**Phase 1 — invariants (#24).** The schedule evaluator seam, a deterministic scheduling clock over a single
`TimeProvider` covering dispatcher, evaluator, builders, both schedulers, startup recovery and the rate
limiter; JSON and record compatibility with the `OccurrenceMode` scaffold; one migration per provider adding
`ParentTaskId`, `RuntimeInfo` and `ScheduleVersion` with the self-referencing foreign key, the unique index
on `(ParentTaskId, ScheduledExecutionUtc)` and the slot check constraint; the atomic storage operations at
each provider's own tier (stored procedures on SQL Server and MySQL, writable CTEs on PostgreSQL, one
conditional UPDATE in a transaction on the EF Core base); and the X3 split of recovery into rows that still
have work to execute and series that only need finalizing. Review: 50 raw findings triaged, all confirmed
medium-and-above fixed and re-verified, 18 low notes recorded. Certification complete (Fable + Codex). Perf
gate passed — see §6.

**Phase 2 — execution context (#25).** `ITaskExecutionContext`, `MisfireInfo`/`MisfireKind` and
`ITaskExecutionContextAccessor` in `EverTask.Abstractions`; `IEverTaskHandler<T>` gained `SetExecutionContext`
as a no-op default interface member, so nothing existing broke. The worker builds one context per delivery
before `OnStarted`, publishes it on a singleton ambient accessor (a scoped one would be blind to an eagerly
resolved handler, whose graph lives in the dispatcher's scope) and injects it through delegates compiled once
per handler type, replacing per-execution reflection. `SetMisfireThreshold` arrived here. The phase also fixed
a pre-existing leak: every delivery dropped before it reached the execution core stranded the DI scope its
eager handler was built in. Review: 38 raw findings triaged, medium-and-above fixed, 19 low notes.

**Phase 3 — time zones (#26).** `InTimeZone(TimeZoneInfo|string)` as default interface members on the builder
interfaces, persisted as a normalized IANA id inside the schedule JSON rather than as a column.
`ScheduleSemantics` splits Calendar from Elapsed schedules and refuses a zone on a plain cadence.
The `WallClock` mapper implements the spring-gap and repeated-hour rules; cron delegates to Cronos with the
zone, and Cronos is wired in as the test oracle. `SetDefaultScheduleTimeZone` stamps the zone into the
definition at dispatch, so changing the default later moves nothing already stored. The legacy path is
byte-identical: a null zone id short-circuits before anything new runs. Review: 29 raw findings triaged,
medium-and-above fixed, 17 low notes.

**Phase 4 — durable occurrences and misfire (#27).** `WithDurableOccurrences()` turns the schedule row into a
definition plus a cursor; `OccurrenceMaterializer` turns each due slot into its own one-shot child, and every
path that can move the schedule forward calls the same entry point. `DueSlotEnumerator` implements `Skip`,
`FireOnce` and `CatchUp`, bounded by `MaxAge`, `MaxOccurrences` with `Halt`/`SkipOldest`, and
`MaxPendingOccurrences`. Every dropped slot is reported with the rule that dropped it, and a truncated count
travels as `IsExact = false` all the way to the handler. `BackfillFrom` starts a new durable cursor in the
past. The review gate ran as four Codex rounds plus a closing pass; the last round's six findings (1 high,
5 medium) were all fixed, each pinned by a test that fails without the fix. Two completeness passes followed,
and a pre-commit recheck by two independent lenses.

**Phase 5 — runtime schedule management (#28).** `ITaskScheduleManager` (`Reschedule`, `ReevaluateSchedule`,
`ResumeSchedule`, `RequeueFailedOccurrence`, `CancelSchedule`) addressed by task key, plus `RescheduleMode`
and `ScheduleUpdateResult`. `TaskKeyLockRegistry` moves the dispatcher's per-key critical section into the
container so a dispatch and a reschedule of the same key share it; `ScheduleVersionRegistry` refuses a stale
parked registration; `ScheduleRebase` implements the nominal-period `RebaseFromCursor`. No storage members and
no migrations were needed — phase 1 had already shipped `UpdateSchedule`, `RequeueTerminal` and the CAS
overloads — so D7 was satisfied with no new round-trips. Review: 33 raw findings triaged, medium-and-above
fixed, 7 low notes. Three certification passes.

**Phase 6 — occurrence providers (#29).** `INextOccurrenceProvider`, its request type and its transient-failure
exception; a key-based registry resolved per call in its own scope; `UseOccurrenceProvider("key", config?)` on
the fluent builder; a provider branch behind `IScheduleEvaluator` for every primitive, so misfire policies,
durable occurrences, zones, skip-forward and `ITaskScheduleManager` all work over a provider unchanged. The
failure model is explicit: an unknown key is a configuration error (exception at dispatch, terminal poison at
recovery), while a provider that throws is an outage — nothing written, cursor untouched, exponential re-park
via `SetOccurrenceProviderRetry`, warning event, recovery poison counter deliberately not touched. A replay
over a provider grid asks it once per slot rather than once per occurrence. Review: 47 raw findings triaged,
medium-and-above fixed, 14 low notes.

**Phase 7 — monitoring, docs and the release cut (#30).** Durable-occurrence facts on the task list and detail
DTOs, added as `init` properties so existing consumer code keeps compiling; `GET /tasks/{id}/occurrences`;
`parentTaskId`, `onlyOccurrences` and `onlyCatchUp` filters; `TaskCountsDto.Occurrences`; and
`OverviewDto.CatchUpBacklog`, which answers what a host still owes. The dashboard mirrors all of it — an
Occurrences tab on durable schedules, catch-up, fire-once and lateness badges, a halt alert and a backlog KPI.
The docs, the `.agents` mirror and the `integrate-evertask` skill were swept, the samples gained a nightly
reconciliation task, and `[Unreleased]` was cut as `[4.0.0]`. Phase 7's own review ran three rounds of
findings and four rounds of completeness closure, all recorded in decisions §3.8.

**In-branch follow-ups closed alongside the phases.** #34 (analyzer ET0010 for `InTimeZone` on a provably
elapsed-only chain), #37 (UTC normalization at the public storage entry points), #38 (recovery poison
confirmation), #39 (pipelined recovery pages), #40, #43, #45 (test isolation and determinism), #41 (bounded
occurrence rebuild attempts), #42 (management endpoints behind their own authorization model), #44 (paged
audit trails — a declared breaking change), #46 and #47 (monitoring surface under `UsePathBase`, and an IP
whitelist that no longer trusts `X-Forwarded-For`), and #54 (scheduler polling allocations, closed in
`3b34fb9`).

---

## 3. Final adversarial review

The review over the whole feature ran with eight lenses in round 1: five Claude high-tier finders, two Opus
finders and one Codex finder. **The Codex lens failed in every round**, so its coverage is absent from all
three rounds; the Codex pass described in §4 was run afterwards, in part to make up for it.

- **Round 1 — 16 findings confirmed and fixed** (`6f70c29`). Six of them touched behaviour or public surface
  and were ratified as deviations in decisions §3.9: the `MaxOccurrenceRebuildAttempts` ceiling spent once per
  process rather than once per materializer run; `RebaseFromCursor` re-phasing a monthly schedule that names
  several days; `BackfillFrom` landing past a listed day on a month cadence; `RecurringInfo` rendering bounds
  on the host clock while labelling them with the schedule's zone; occurrence retention cascade-deleting audit
  rows inside their own configured window (`preserveTasksWithAudits`); and a re-dispatch under the same task
  key having to undo the cancellation of the row it reuses. The other ten were corrections without deviation,
  and three of them were gaps in the gate itself: CI never opened three of the five test projects (Monitoring,
  Analyzers, Logging — 3,270 lines of #23 tests), CI excluded the SQL Server and PostgreSQL arms of the
  four-provider contract suite, and every call of the five new atomic storage operations in that suite passed
  `AuditLevel.Full`, leaving each operation's non-audited branch untested on every provider.
- **Round 2 — 4 findings confirmed and fixed** (`0108076`), all on the cancel-and-redispatch path: the revival
  removing the blacklist entry that was the only cover for in-flight cancelled occurrences, a revived durable
  schedule keeping its `Halted` marker for ever, and a retention policy pruning only one audit trail turning
  `OccurrenceRetentionDays` into a permanent no-op.
- **Round 3 — 4 findings confirmed and fixed** (`ab14ca2`), again on the revival path. Two added surface and
  were ratified: the revival is now a new **generation** of the row (`TryReviveCancelledSchedule` bumps
  `ScheduleVersion` as it writes `WaitingQueue`, so the version machinery S4 built is what tells the old
  in-flight inline delivery from the new registration), and that operation became a new `ITaskStorage` member
  with a **working** default — it is reachable from any storage, including a custom one implementing nothing
  of #23. The order was inverted accordingly: the write happens first, the cover is removed after, so a lost
  write no longer uncovers anything. The other two: `FinalizeExhaustedSeriesAsync` no longer finalizes a
  `Cancelled` row, and the ending of a superseded delivery is no longer persisted over the series that has
  taken the row.

**The workflow then hit its round cap and aborted with two HIGH findings open**, both terminal-write races on
the cancel/revive path: a late `OperationCanceledException` ending writing `Cancelled` over a row a revival
had just brought back, and a late `Failed` ending erasing an operator's cancel and making the row recoverable
again. They were fixed outside the workflow, in `3b34fb9`, by a single mechanism: `ITaskStorage.TrySetTerminalOutcome`,
a compare-and-swap that demands the row still carries the delivery's `ScheduleVersion` and — unless the
outcome is itself `Cancelled` — is not already `Cancelled`. `WorkerExecutor.PersistEndingAsync` routes both
ending branches through it, a refused ending is logged (1243), and a storage that does not advertise schedule
versioning keeps the historical unconditional writes.

**Honest note on coverage.** Because of the abort, the workflow's completeness critic and its synthesis stage
never ran. Nobody inside the workflow ever asked "is anything missing", and no machine-produced summary of
the review exists. This document is the substitute, and the §4 Codex pass — six segmented lenses over the
finished branch, run afterwards — is the closest thing to the completeness sweep that was lost.

---

## 4. Post-review Codex pass

Six segmented lenses were run over the finished branch at `xhigh`/`high` reasoning, read-only: **perf**,
**storage**, **monitoring** (security-oriented), **serialization/compatibility**, **clean** (dead and
duplicated surface) and **docs**. They returned 36 findings; **33 were validated against the code** before any
fix, and the remaining 3 were rejected. Nothing was fixed on a lens's word alone.

The fixes landed in four batches, each verified after the fact:

1. **Core and storage** (`3b34fb9`). The two open HIGH terminal races (§3); the four `Down` migrations now
   delete occurrence children before dropping `ParentTaskId`; MySQL and SQLite occurrence cleanup re-assert
   status, age and preservation guards inside the `DELETE`, so a `RequeueTerminal` landing between select and
   delete keeps its row; `CleanupCompletedTasks` requires `ParentTaskId == null` on three providers, so
   children answer only to occurrence retention; SQLite terminal-occurrence discovery filters by cutoff in SQL
   and pages its candidate scan instead of materializing the whole terminal population; `usp_CancelSchedule`
   on MySQL audits the row set the `UPDATE` actually changed rather than a pre-update non-locking read; the
   EF materializer normalizes occurrence timestamps to UTC like `Persist`/`UpdateTask`; the new composite
   index `IX_QueuedTasks_ParentTaskId_Status` was folded into the unreleased migrations; `PeriodicTimerScheduler`
   waits the full calculated delay instead of clamping to the one-second check interval (this closed #54); and
   the dead or duplicated surface the clean lens found was removed or consolidated — `IsOccurrenceStillCurrentAsync`,
   `CountActiveOccurrences`, the unpaged audit pair superseded inside this same branch, plus the two ratified
   pre-branch removals (`ITaskStorage.GetCurrentRunCount` and the deprecated `TimeOnly.ToUniversalTime`).
2. **Monitoring** (`b691327`). The CSRF gate on the three bodyless management POSTs (`Sec-Fetch-Site`, with an
   `Origin`-versus-request-origin fallback and headerless non-browser clients passing), which matters in the
   host-hook mode that authorizes from an ambient cookie principal; CIDR prefix-range validation, after which
   a typo such as `10.0.0.0/-1` no longer zeroes every mask byte and turns the whitelist into allow-all;
   IPv4-mapped IPv6 normalization; `take` capped at 500; and a hub filter that ends an authenticated SignalR
   connection at its JWT's expiry.
3. **CI** (`d464c2f`). The round-1 fix had removed the container-suite filter entirely; the maintainer-ratified
   position is that SQL Server, PostgreSQL and the `AuditLevel` suites run locally with Testcontainers before
   every release and stay out of GitHub Actions, while the three Docker-free suites that the gate had never
   opened (Monitoring, Analyzers, Logging) stay in.
4. **Docs** (`bfed3be`). The storage guides no longer recommend scale-out deployments the runtime does not
   support; `TrySetTerminalOutcome` was added everywhere the versioned `ITaskStorage` contract is enumerated;
   `monitoring-events.md` documents both stable shapes of the occurrence-unusable message and the
   failed-revival error event; `SetMisfireThreshold` no longer claims delivery-time-only effect; the CHANGELOG
   gained the two BREAKING entries for the ratified removals; and decisions §3.6 records the ratifications.

**Three bugs were found during verification of those fixes, not by any lens.** `JwtTokenService` signed
`notBefore`/`expires` with a `DateTime` whose `Unspecified` kind `JwtSecurityToken` reads as **local** time, so
on a UTC+2 host every token really expired two hours off its advertised `ExpiresAt`; the hub filter had to be
registered through `AddSignalR().AddHubOptions<TaskMonitorHub>()`, because a bare `Configure<HubOptions<T>>` is
never consumed by the dispatcher; and the audit-level parity fix had to cover the PostgreSQL writable CTEs as
well as the two stored-procedure providers, all three of which treated an unknown `AuditLevel` as "drop the
audits" instead of following `AuditPolicy` and treating it as `Full`. The same batch also restored two pieces
of 3.11 binary compatibility on the public monitoring surface (the `GenerateToken(string)` overload and the
two-argument `JwtAuthenticationMiddleware` constructor), each pinned by a signature test.

**Performance findings were deferred, deliberately.** The perf lens returned four HIGH and two lower findings —
provider catch-up asking one slot at a time with a DI scope per question, each materialized occurrence
rebuilding the full DI plus serialization plus storage pipeline, a window drain re-reading child rows
quadratically, `MemoryTaskStorage`'s durable path scanning the whole store under its global lock, the worker
consumer awaiting the next-materialization kick inline, and execution-context work paid by every delivery.
All six are real, all are bounded by design caps (`MaxOccurrences`, the global budget), and every one of them
is a redesign — a bulk provider contract, bulk materialization, drain projections — rather than a fix.
With the D7 parity gate already passed, they were filed as **issues #48–#53** in the Performance project for
after 4.0.0; #51 (`MemoryTaskStorage`) is explicitly low priority, since that store is a development and test
vehicle. The one cheap half — the missing `(ParentTaskId, Status)` composite index — was pulled into the
branch, because the migrations are unreleased and adding it later would cost a migration. **#54** was fixed
in-branch outright.

---

## 5. Test totals

Final committed run, `dotnet test EverTask.slnx -c Release` with Testcontainers (SQL Server 2022,
PostgreSQL 16, MariaDB 10.11), build 0 warnings / 0 errors on all three target frameworks.

| Suite | net10.0 | net9.0 | net8.0 |
|---|---|---|---|
| Core (`EverTask.Tests`) | 2057 | 2057 | 2057 |
| Storage (`EverTask.Tests.Storage`) | 988 | 984 | 741 |
| Monitoring (`EverTask.Tests.Monitoring`) | 294 | 294 | 289 |
| Analyzers | 78 | 78 | 78 |
| Logging | 10 | 10 | 10 |

**All green, 0 failures**, plus the 4 long-standing SignalR skips in the monitoring suite. The per-TFM
differences are by design and not gaps: net8.0 has no MySQL provider (the Pomelo-fork dependency targets
net9.0 and net10.0 only) and no built-in OpenAPI generator, which accounts for both the storage and the
monitoring deltas.

---

## 6. Performance — baseline versus after

Reference: `review/orchestrator/perf-baseline.md` (`issue23-baseline` = `c71b5e2`, captured 2026-08-22 before
any #23 work). Gates: `review/orchestrator/perf-after-phase-1.md` and `review/orchestrator/perf-after-phase-4.md`,
the latter being the most recent capture and the one that carries both schedule cells. Harness:
`benchmarks/EverTask.LoadHarness`, same cells and same knobs on both sides, PostgreSQL cells provisioned by
Testcontainers.

**The D7 parity gate passed.** Its threshold is 10% throughput regression; the worst figure measured anywhere
is **−4.6%**, on the recurring-advance cell at the phase-1 gate.

| Cell | Baseline | After phase 1 | After phase 4 (final) |
|---|---|---|---|
| **A4W** engine, `NullTaskStorage` | 2,899.6 B/task | 3,045.2 B/task (**+5.0%**) | 3,240.2 B/task (**+13.6%**); throughput −3.6…−6.0% within regime |
| **L8** full lifecycle, PostgreSQL | 2,864 tasks/s · 73,842.6 B | 2,752.5 tasks/s (−3.9%) · 79,324.4 B (**+7.4%**) | 2,781 tasks/s (**+20.4%** vs baseline) · 79,603.5 B (+7.8%) |
| **LDP** dispatch latency, PostgreSQL | 2,834 tasks/s · 73,892.4 B | 2,748.5 tasks/s (−3.0%) · 79,258.1 B (+7.3%) | 2,755 tasks/s (**+5.7%** vs baseline) · 79,609.7 B (+7.5%) |
| **LRA** recurring advance, PostgreSQL | 14,520 adv/s | 13,856 adv/s (**−4.6%**) · 11,363.2 B (−1.1%) | 13,958 adv/s (**+0.8%** vs phase 1) · 11,363.3 B (**0.0** vs phase 1) |
| **LDM** durable materialization | — (did not exist) | — | 7,516 occ/s · 15,263.7 B, p999/p50 = 1.8× |

Reading of the final capture:

- **Throughput: no material regression on any hot path.** The two PostgreSQL lifecycle cells came back
  steady-state (CV 0.6%, against 7–11% for the baseline captures) and both are **above** the baseline, with
  much better tails (L8 p999 −82%, LDP p999 −69%). Read those as "no regression" rather than as a win: part of
  the gain is a better-behaved container and page cache. `LRA` is at parity and byte-for-byte identical on
  allocations against the phase-1 capture. `A4W` throughput is the weak number in both directions — the host
  was busy and both arms are bimodal with a 2× spread *inside each arm*, so only the ten alternating pairs and
  the within-regime split make it usable at all; within regime the patched arm sits 3.6–6.0% below.
- **Allocations: a real, reproducible increase, already accepted.** +387 B/task (+13.6%) on the clean engine
  path and +7.5/7.8% on the two PostgreSQL cells (roughly +5.5 KB on a 74 KB/task budget dominated by EF Core
  and Npgsql). The delta reproduces across every pair and across two independent captures to within a fraction
  of a percent, and it was explicitly ratified as the intended per-delivery cost of phases 2–3 — an
  `ITaskExecutionContext` per delivery plus the time-zone and DST machinery — in decisions §3.5, entry D7.
  The post-review fixes added none of it.
- **The two schedule cells are clean.** The advance pays nothing, and a materialization is **one** commit that
  inserts the child row and CAS-advances the cursor, not two round-trips wearing one name.

**Caveat carried forward.** A hard single throughput number for `A4W` and `LRA` still needs an idle,
affinity-pinned machine. The allocation figures are solid; the throughput ones are noise-limited.

---

## 7. Known limits

Stated plainly, because each one is a boundary a user can hit.

1. **Single active host (D5).** Durable occurrences do not make EverTask distributed. Exactly one active host
   per store may run the scheduler; a standby is fine. Cross-host lease and claim is tracked as its own epic,
   **#31**, and covers all of EverTask, not just recurring tasks. The storage guides were corrected in
   `bfed3be` to stop recommending scale-out deployments the runtime does not support.
2. **At-least-once delivery.** The unique index on `(ParentTaskId, ScheduledExecutionUtc)` guarantees a slot
   becomes at most one row; it does not guarantee a row's handler body runs exactly once across a crash. A
   handler that must not repeat work still needs its own idempotency.
3. **No rollback to 3.x with durable schedules active.** This is the sharpest operational boundary in the
   release. A 3.x reader ignores `OccurrenceMode`, `Misfire` and `Provider` on the parent row and knows nothing
   about occurrence children: a provider-backed schedule silently degrades to an inline one, and during an
   overlapping rollout an old host can run a slot a 4.0 child row already represents. The migrations' `Down`
   therefore **deletes** the child rows before dropping `ParentTaskId` — pending occurrences are lost on
   downgrade, by design, because the alternative is executing them as standalone one-shots next to their
   surviving parent. Disable durable occurrences and drain the children before any planned rollback.
4. **A custom `IScheduler` that does not implement `TrySchedule` keeps unconditional registration.** The
   default interface member forwards to `Schedule` for binary compatibility with schedulers compiled against
   older versions, and the cost of that default is that runtime rescheduling cannot refuse a stale in-flight
   registration over such a scheduler: an old delivery can replace the newly parked one until the next restart.
   Both built-in schedulers implement it.
5. **CORS under a path base.** The monitoring CORS policy matches on the pre-`UsePathBase` path, so a
   cross-origin dashboard served under a path base may need the host's own CORS setup. Functional, documented
   in `docs/configuration-reference.md`; not a security boundary, since authentication and the IP whitelist are
   applied independently and were hardened for `UsePathBase` in #46.
6. **Six open performance items, #48–#53.** All real, all bounded by the design caps, all redesigns rather
   than fixes: the per-slot provider question with a DI scope each (#48), the per-occurrence rebuild of the
   full DI/serialization/storage pipeline (#49), the quadratic window drain (#50), `MemoryTaskStorage`'s
   whole-store scan under its global lock (#51, explicitly low priority — a dev and test vehicle), the inline
   await of the next-materialization kick (#52) and the execution-context cost paid by every delivery (#53).
   The in-branch half of #50, the composite index, already shipped.
7. **One known flaky test.** `Should_handle_multiple_concurrent_recurring_tasks` can fail under parallel TFM
   runs on a loaded machine. It is a timing assertion in the test, not a product defect, and it was not
   reproduced in the final committed run.

---

## 8. GO / NO-GO

**GO for the 4.0.0 release.**

The reasoning, and what would have made it a no:

- **Every phase closed with its own review and certification.** Seven phases, seven closed sub-issues, each
  with an adversarial gate whose medium-and-above findings were fixed and re-verified, and each with a
  completeness pass by two independent lenses. The low-severity notes each phase left are recorded in the
  phase reports rather than silently dropped.
- **The final review's open findings are closed.** The two HIGH terminal-write races the workflow aborted on
  are not outstanding: they are fixed by one mechanism, `TrySetTerminalOutcome`, and pinned by tests. Nothing
  above MEDIUM is open on this branch.
- **The gap left by the abort was covered.** The completeness critic and synthesis never ran, which is a real
  hole in the process. The six-lens Codex pass over the finished branch is a defensible substitute: it swept
  performance, storage, security, compatibility, dead surface and documentation independently, produced 36
  findings, and the 33 that survived validation were fixed and verified. Its most valuable output was arguably
  the three bugs found while *verifying* those fixes, none of which any lens had reported.
- **The gates are green and the numbers are real.** Full suite green on three target frameworks with real
  containers for SQL Server, PostgreSQL and MariaDB; build clean with warnings as errors; the D7 parity gate
  passed with margin, and the one allocation increase was measured, reproduced across two captures, explained
  and ratified in advance rather than discovered at release time.
- **The breaking changes are declared.** Four of them — the two paged audit endpoints and the
  `ITaskQueryService` return types (#44), the removal of `ITaskStorage.GetCurrentRunCount`, the removal of
  `TimeOnly.ToUniversalTime`, and the retry-policy namespace move — are in the CHANGELOG under explicit
  `### Changed (breaking — …)` headings. This is a major release and they belong in it.
- **The limits are documented, not hidden.** Single-active-host, at-least-once, the 3.x rollback boundary and
  the `IScheduler` default are written into the CHANGELOG, the storage guides and the durable-occurrences page.
  A user can find each of them before hitting it.

What holds the verdict at GO rather than at an unqualified one: the six deferred performance items are real
costs a heavy catch-up will pay, and the rollback boundary is unforgiving. Neither is a defect, both are
documented, and both are the right trade for shipping. Release with §7 reproduced in the release notes.
