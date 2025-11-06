---
layout: default
title: Storage Overview
parent: Storage
nav_order: 1
---

# Storage Overview

Storage providers persist tasks and their state. With persistent storage, tasks can resume after application restarts, you can track task history, maintain an audit trail of execution, and store scheduled or recurring tasks for future execution.

## Choosing a Storage Provider

| Provider | Use Case | Pros | Cons |
|----------|----------|------|------|
| **In-Memory** | Development, Testing | Fast, no setup | Data lost on restart |
| **SQL Server** | Production, Enterprise | Robust, scalable, stored procedures | Requires SQL Server |
| **SQLite** | Small-scale production, Single-server | Simple, file-based, no server | Limited concurrent writes |

## Storage Provider Details

### In-Memory Storage

Perfect for development and testing when you don't need task persistence.

- Zero setup - works out of the box
- Fast performance
- No external dependencies
- Tasks lost on application restart
- Not suitable for production

**Learn more:** [In-Memory Storage](in-memory-storage.md)

### SQL Server Storage

Enterprise-grade storage for production environments.

- Production-ready
- Highly scalable
- ACID transactions
- Stored procedures for performance
- Rich querying capabilities
- Requires SQL Server instance
- Additional infrastructure cost

**Learn more:** [SQL Server Storage](sql-server-storage.md)

### SQLite Storage

Lightweight, file-based storage that works well for single-server deployments.

- Simple setup - single file
- No server required
- Perfect for small-scale production
- Easy backups (copy file)
- Lower infrastructure cost
- Limited concurrent writes
- Single server only (no clustering)
- Provider limitation with DateTimeOffset filtering

**Learn more:** [SQLite Storage](sqlite-storage.md)

### Custom Storage

Implement custom storage providers for Redis, MongoDB, PostgreSQL, or any other database.

**Learn more:** [Custom Storage](custom-storage.md)

## Quick Comparison

### When to Use Each Provider

**Use In-Memory when:**
- Developing locally
- Running integration tests
- Prototyping features
- Tasks don't need to survive restarts

**Use SQL Server when:**
- Running in production at scale
- Need high availability
- Require robust querying
- Have existing SQL Server infrastructure
- Need enterprise-grade reliability

**Use SQLite when:**
- Running a small application
- Single-server deployment
- Limited infrastructure budget
- Desktop or edge applications
- Need simple file-based persistence

**Use Custom Storage when:**
- Have specific database requirements
- Need integration with existing data stores
- Require specialized storage features
- Working with cloud-native databases (CosmosDB, DynamoDB)

## Next Steps

- **[Audit Configuration](audit-configuration.md)** - Control database bloat with audit levels
- **[In-Memory Storage](in-memory-storage.md)** - Development and testing setup
- **[SQL Server Storage](sql-server-storage.md)** - Production SQL Server configuration
- **[SQLite Storage](sqlite-storage.md)** - Lightweight production setup
- **[Custom Storage](custom-storage.md)** - Implement your own storage provider
- **[Best Practices](best-practices.md)** - Storage selection and optimization
