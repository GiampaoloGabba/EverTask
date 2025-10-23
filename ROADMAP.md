# EverTask Roadmap

This document outlines planned features and improvements for EverTask.

---

## Version 3.1.0+

### 🌐 Distributed Clustering with Leader Election
**Status:** Planned | **Priority:** High | **Effort:** 40-50 hours

Transform EverTask into a fully distributed system supporting horizontal scaling with multiple server instances.

**Features:**
- Execution clustering with external queues (RabbitMQ, Azure Service Bus, AWS SQS)
- Scheduler high availability with automatic leader election and failover
- Pluggable queue providers for message delivery and distribution
- Pluggable distributed lock providers (Redis, Azure Blob Storage, Consul)
- Backward compatible (single-instance mode still supported)

**Details:** See `.claude/tasks/distributed-clustering-leader-election.md`

---

### 🚀 Advanced Throttling and Rate Limiting System
**Status:** Planned | **Priority:** High | **Effort:** 23-29 hours

Comprehensive rate limiting and throttling system to prevent server overwhelm during task spikes.

**Features:**
- Global rate limiting (e.g., max 1000 tasks/sec)
- Per-handler rate limiting (e.g., EmailHandler: 100/sec)
- Concurrency limits per handler (e.g., max 5 concurrent instances)
- Adaptive throttling based on CPU/memory pressure
- Configurable recurring task skip behavior

**Details:** See `.claude/tasks/advanced-throttling-rate-limiting.md`

---

### 🔄 Workflow Orchestration System
**Status:** Planned | **Priority:** Medium | **Effort:** 50-70 hours

Enable complex workflow and saga orchestration with fluent API on top of EverTask primitives.

**Features:**
- Sequential, parallel, conditional, and saga/compensation workflows
- Full workflow persistence with step-level tracking
- Shared context and pipeline data passing between steps
- Static workflow definitions with compile-time safety
- SQL Server stored procedures for optimized performance
- SignalR monitoring integration

**Details:** See `.claude/tasks/workflow-orchestration.md`

---

## Future Versions

### Version 4.0.0 (Ideas)

### 📊 Enhanced Observability
Metrics export (Prometheus, OpenTelemetry), alerting.

### 🌐 Distributed Rate Limiting
Coordinate rate limits across multiple servers using Redis.

### 🎯 Priority-Based Scheduling
High-priority tasks bypass rate limits and execute first.

---

## Completed Features

### ✅ Task Execution Log Capture with Proxy Pattern
**Completed:** v3.0.0 (2025-10-23) | **Effort:** 30 hours

Implemented a comprehensive log capture system with proxy pattern architecture that always forwards logs to ILogger while optionally persisting to database.

**Delivered:**
- Proxy pattern: Logger ALWAYS forwards to ILogger infrastructure (console, file, Serilog, Application Insights)
- Optional database persistence via `EnablePersistentHandlerLogging` configuration
- `TaskExecutionLog` entity with cascade delete and sequence numbers
- Thread-safe in-memory log collection with lock-based synchronization
- Configurable filtering: `SetMinimumPersistentLogLevel`, `SetMaxPersistedLogsPerTask`
- ILogger<THandler> injection for proper log categorization per handler type
- Storage methods: `SaveExecutionLogsAsync()`, `GetExecutionLogsAsync()` with pagination
- Logs persist even when tasks fail (captured in finally block)
- Zero overhead when persistence disabled (conditional allocation)
- Comprehensive test coverage: 13 proxy pattern unit tests + 5 storage tests + integration tests
- Full documentation in advanced-features.md, configuration-reference.md, and cheatsheet

**Related commits:** [squash merge from feature/logger]

---

### ✅ Lazy Handler Resolution for Memory Optimization
**Completed:** v3.1.0 (2025-10-22) | **Effort:** 14 hours

Optimized memory footprint for scheduled and recurring tasks by implementing lazy handler resolution.

**Delivered:**
- Handler instances disposed after dispatch validation (fail-fast preserved)
- Fresh handler instances created at execution time (70-90% memory reduction)
- Configurable via `LazyResolutionMode` enum (Eager/Lazy/Auto)
- Auto mode: lazy for tasks delayed >1 hour or recurring tasks
- Backward compatible (eager mode still available)
- Comprehensive test suite with 5 lazy mode integration tests
- Fixed handler disposal lifecycle bug in `WorkerExecutor`

**Related commits:** 2fc0e54, bec8e1f, e0f8294, 913921b

---

### ✅ Schedule Drift Fix for Recurring Tasks
**Completed:** v3.1.0 (2025-10-22) | **Effort:** 8 hours

Fixed schedule drift in recurring tasks by implementing consistent next-run calculation logic.

**Delivered:**
- `CalculateNextValidRun()` method skips past occurrences correctly
- Both `Dispatcher` and `WorkerExecutor` use consistent logic
- Prevents drift accumulation during system downtime
- Preserves `ExecutionTime` across rescheduling cycles
- 15+ integration tests verifying consistency

**Related commits:** d644166

---

### ✅ Zero-Flakiness Test Infrastructure
**Completed:** v3.1.0 (2025-10-22) | **Effort:** 12 hours

Eliminated all test flakiness through comprehensive infrastructure refactoring.

**Delivered:**
- `IsolatedIntegrationTestBase` pattern with per-test `IHost` instances
- Intelligent polling with `TaskWaitHelper` (replaces fixed delays)
- Thread-safe `TestTaskStateManager` for execution tracking
- N-consumer pattern for better channel throughput
- 445 tests pass consistently on .NET 6/7/8/9
- 4-12x faster test execution through safe parallelism
- 50% reduction in test timeouts without reliability loss

**Related commits:** 07fafaf, 48e0fd7, e7aa388

---

### ✅ Retry Policy Enhancements: OnRetry Callback and Exception Filters
**Completed:** v3.1.0 (2025-10-22) | **Effort:** 12 hours

Enhanced retry policy system with visibility into retry attempts and selective exception filtering.

**Delivered:**
- `OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)` lifecycle callback
- Exception filtering via `IRetryPolicy.ShouldRetry(Exception exception)` interface method
- LinearRetryPolicy fluent API: `Handle<T>()`, `DoNotHandle<T>()`, `HandleWhen(predicate)`
- Predefined exception sets: `HandleTransientDatabaseErrors()`, `HandleAllTransientErrors()`
- Fail-fast for non-retryable exceptions (OperationCanceledException, TimeoutException)
- Performance optimization: OnRetry MethodInfo cached per handler type
- Seamless integration with lazy handler resolution
- Backward compatible (new features opt-in with sensible defaults)
- 16 unit tests + 6 integration tests verifying all filtering scenarios

**Related commits:** [squash merge from feature/retry-policy-enhancements]