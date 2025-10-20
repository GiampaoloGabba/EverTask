# EverTask Roadmap

This document outlines planned features and improvements for EverTask.

---

## Version 3.0.0

### ⚡ Lazy Handler Resolution for Memory Optimization
**Status:** Planned | **Priority:** High | **Effort:** 12-16 hours

Optimize memory footprint for scheduled and recurring tasks by implementing lazy handler resolution.

**Features:**
- Handler instances disposed after dispatch validation (fail-fast preserved)
- Fresh handler instances created at execution time (reduced memory pressure)
- Configurable threshold for delayed tasks (default: 1 hour)
- Always lazy for recurring tasks (handlers don't live for months)
- 70-90% memory savings for scheduled tasks
- Backward compatible (configurable, enabled by default)

**Details:** See `.claude/tasks/lazy-handler-resolution-optimization.md`

---

### 🔄 Retry Policy Enhancements: OnRetry Callback and Exception Filters
**Status:** Planned | **Priority:** High | **Effort:** 10-14 hours

Enhance retry policy system with visibility into retry attempts and selective exception filtering.

**Features:**
- OnRetry lifecycle callback for logging, metrics, and debugging
- Exception filtering with ShouldRetry(Exception) interface method
- LinearRetryPolicy fluent API: Handle<T>() and DoNotHandle<T>()
- Fail-fast for non-transient errors (ArgumentException, NullReferenceException)
- Backward compatible (new features are opt-in with sensible defaults)
- Integration-friendly (Polly policies can implement ShouldRetry easily)

**Details:** See `.claude/tasks/retry-policy-enhancements.md`

---

### 📝 Task Execution Log Capture
**Status:** Planned | **Priority:** High | **Effort:** 25-35 hours

Capture and store all logs written during task execution in the database for complete audit trails and debugging.

**Features:**
- Opt-in log capture (zero overhead when disabled)
- Store logs per task execution with full context (message, level, timestamp, exception)
- Configurable filtering by log level and max logs per task
- Access logs via storage API and monitoring dashboard
- Thread-safe log collection during async handler execution
- Integration with SignalR monitoring for real-time log viewing
- Backward compatible (disabled by default)

**Details:** See `.claude/tasks/task-execution-log-capture.md`

---

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

_No completed features yet. Features will be listed here as they are implemented._
