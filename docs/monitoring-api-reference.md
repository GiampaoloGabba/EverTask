---
layout: default
title: API Reference
parent: Monitoring
nav_order: 3
---

# Monitoring API Reference

Complete REST API documentation for the EverTask Monitoring Dashboard.

> **Note**: every endpoint is **read-only** except the three under [`/management`](#management-endpoints), which a host has to enable and authorize explicitly (`EnableManagementEndpoints`, off by default). Changing a schedule at runtime from application code — reschedule, resume a halted catch-up, requeue a failed occurrence, cancel a series — still goes through [`ITaskScheduleManager`](recurring-tasks/managing-tasks.md), behind your own authorization.

## Table of Contents

- [Base URL](#base-url)
- [Authentication](#authentication)
- [Tasks Endpoints](#tasks-endpoints)
- [Dashboard Endpoints](#dashboard-endpoints)
- [Queue Endpoints](#queue-endpoints)
- [Statistics Endpoints](#statistics-endpoints)
- [Management Endpoints](#management-endpoints)
- [Configuration Endpoint](#configuration-endpoint)
- [Examples](#examples)

## Base URL

All endpoints are relative to `{BasePath}/api` (default: `/evertask-monitoring/api`).

Example: `http://localhost:5000/evertask-monitoring/api`

## Authentication

Most endpoints require JWT authentication. Include the token in the `Authorization` header:

```bash
Authorization: Bearer {token}
```

To obtain a token, POST to `/auth/login`:

**Request:**
```bash
POST /evertask-monitoring/api/auth/login
Content-Type: application/json

{
  "username": "admin",
  "password": "admin"
}
```

**Response:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2025-01-16T02:00:00Z",
  "username": "admin",
  "canManage": false
}
```

`canManage` says whether this session carries the **operate** role, i.e. whether the
[management endpoints](#management-endpoints) will accept it. Only the second credential
(`ManagementUsername` / `ManagementPassword`) grants it: the dashboard credential is a read credential, and
so is every magic link. `POST /auth/validate` reports the same field for a token it validated.

> **Exception**: The `/config` and `/auth/magic` endpoints do not require authentication.

### Magic Link Authentication

If `MagicLinkToken` is configured, exchange it for a JWT by sending the token in the request body (since 4.0.0):

**Request:**
```bash
POST /evertask-monitoring/api/auth/magic
Content-Type: application/json

{
  "token": "your-configured-token"
}
```

**Response (200 OK):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2025-01-16T02:00:00Z",
  "username": "admin"
}
```

The response carries `Cache-Control: no-store`.

**Error Responses:**
- `404 Not Found` - Magic link not configured (`MagicLinkToken` is null)
- `401 Unauthorized` - Invalid token
- `429 Too Many Requests` - Login rate limit exhausted (5 attempts per 15 minutes per client IP, only when the host runs `UseRateLimiter()`)

The returned JWT can be used like any other JWT token for subsequent API requests.

**Deprecated: `GET /evertask-monitoring/api/auth/magic?token=...`**

The query-string form is still served for existing integrations, with the same responses, but it is deprecated (flagged as such in the OpenAPI document). The query string ends up in server request logs (Serilog request logging records `RawTarget`), reverse-proxy access logs and browser history, and the token is a permanent credential. Use the POST form; see [Magic Link Access](monitoring-dashboard.md#magic-link-access) for the dashboard URL shape and host-side logging advice.

## Tasks Endpoints

### GET /tasks

Get paginated list of tasks with filtering and sorting.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `statuses` | string[] | No | - | Filter by status (`WaitingQueue`, `Queued`, `InProgress`, `Pending`, `Cancelled`, `Completed`, `Failed`, `ServiceStopped`); repeat the parameter for more than one |
| `queueName` | string | No | - | Filter by queue name |
| `taskType` | string | No | - | Filter by task type (partial match) |
| `isRecurring` | bool | No | - | Filter recurring tasks (`true`/`false`) |
| `createdAfter` | DateTime | No | - | Filter by creation date (from) |
| `createdBefore` | DateTime | No | - | Filter by creation date (to) |
| `searchTerm` | string | No | - | Partial match on type, handler or task key |
| `parentTaskId` | Guid | No | - | Keep only the occurrences of this durable schedule |
| `onlyOccurrences` | bool | No | - | `true` keeps only materialized occurrences, `false` keeps only rows that are not one |
| `onlyCatchUp` | bool | No | - | `true` keeps only the occurrences that stand for missed work (replayed or collapsed), `false` keeps only the rows that stand for none |
| `sortBy` | string | No | `CreatedAtUtc` | Sort field |
| `sortDescending` | bool | No | `true` | Sort direction |
| `page` | int | No | `1` | Page number |
| `pageSize` | int | No | `20` | Page size |

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks?statuses=Completed&page=1&pageSize=20
Authorization: Bearer {token}
```

**Response:**
```json
{
  "items": [
    {
      "id": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "type": "SendEmailTask",
      "status": "Completed",
      "queueName": "default",
      "createdAtUtc": "2025-01-15T10:00:00Z",
      "lastExecutionUtc": "2025-01-15T10:00:05Z",
      "startedAtUtc": "2025-01-15T10:00:00Z",
      "scheduledExecutionUtc": "2025-01-15T10:00:00Z",
      "isRecurring": false,
      "executionTimeMs": 12.5,
      "parentTaskId": "6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11",
      "occurrenceMode": "Durable",
      "timeZoneId": "Europe/Rome",
      "scheduleVersion": 2,
      "nominalSlotUtc": "2025-01-15T10:00:00Z",
      "misfireKind": "CatchUp"
    }
  ],
  "totalCount": 150,
  "page": 1,
  "pageSize": 20,
  "totalPages": 8
}
```

Nulls are omitted, and the schedule fields are null together on a task that belongs to no schedule —
`scheduleVersion` included, which is why a plain one-shot carries none of them rather than a version of 0. On
a **schedule row** they describe the definition (`occurrenceMode`, `misfirePolicy`, `timeZoneId`,
`scheduleVersion`); on an **occurrence** they describe the row's own identity (`parentTaskId`,
`nominalSlotUtc`, `misfireKind`).

`startedAtUtc` is when the row's last run began — the current one, while it is still running. Measure
lateness against that one and never against `lastExecutionUtc`, which is written on terminal transitions and
so says when a run ENDED: subtract a nominal slot from it and a punctual delivery with a three-minute handler
reports three minutes of tardiness.

It is read from the row's own `InProgress` transition in the status audit trail, so a run still in flight
answers for itself. That transition is recorded at `AuditLevel.Full`, the default; below it, a run that has
FINISHED is derived instead from the end of the run less the duration a completion measured around it.

Everything else answers nothing rather than an instant nobody measured. A row that has not run yet, and one
failed without ever reaching a handler, never started. Deriving needs a duration somebody actually measured,
and only a completion writes one. A failure leaves it at zero; so does a finalization, which is how a durable
schedule ends on its last materialization and how recovery closes a series whose remaining slots fall past its
bound. Both stamp an end on a row no handler ever ran for. And a row waiting for its next delivery reports
nothing on purpose: the run its previous attempt began is not the one it stands for now.

---

### GET /tasks/{id}

Get detailed information about a specific task.

**Path Parameters:**
- `id` (Guid, required): Task ID

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/dc49351d-476d-49f0-a1e8-3e2a39182d22
Authorization: Bearer {token}
```

**Response:**
```json
{
  "id": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
  "type": "MyApp.Tasks.SendEmailTask, MyApp",
  "handler": "MyApp.Handlers.SendEmailHandler, MyApp",
  "request": "{\"Email\":\"user@example.com\",\"Subject\":\"Welcome\"}",
  "status": "Completed",
  "queueName": "default",
  "createdAtUtc": "2025-01-15T10:00:00Z",
  "scheduledExecutionUtc": "2025-01-15T10:00:00Z",
  "lastExecutionUtc": "2025-01-15T10:00:05Z",
  "startedAtUtc": "2025-01-15T10:00:00Z",
  "isRecurring": false,
  "executionTimeMs": 12.5,
  "statusAudits": [],
  "runsAudits": [],
  "statusAuditsTotalCount": 0,
  "runsAuditsTotalCount": 0,
  "parentTaskId": "6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11",
  "occurrenceMode": "Durable",
  "timeZoneId": "Europe/Rome",
  "scheduleVersion": 2,
  "nominalSlotUtc": "2025-01-15T10:00:00Z",
  "misfireKind": "CatchUp",
  "occurrence": {
    "slotUtc": "2025-01-15T10:00:00Z",
    "runNumber": 41,
    "timeZoneId": "Europe/Rome",
    "misfireKind": "CatchUp",
    "missedFromUtc": "2025-01-15T08:00:00Z",
    "missedThroughUtc": "2025-01-15T10:00:00Z",
    "missedCount": 3,
    "missedCountIsExact": true
  }
}
```

`statusAudits` and `runsAudits` both come back **newest first**, read from the audit tables themselves — the
same source as `GET /tasks/{id}/status-audit` and `GET /tasks/{id}/runs-audit`, so the two blocks and the two
endpoints always agree. Each block holds the FIRST PAGE of its trail (100 entries), and
`statusAuditsTotalCount` / `runsAuditsTotalCount` report how many there are in total: a schedule that has run
for a year holds one transition per state per run, and the two paged endpoints are where the rest is asked
for. What they hold is whatever the task's [audit level](configuration-reference.md) let storage record.

A **durable schedule row** answers with `occurrenceMode: "Durable"`, its `misfirePolicy`, and — while its
catch-up has stopped itself over the overflow cap — a `halt` block:

```json
{
  "halt": {
    "atUtc": "2025-01-15T10:00:03Z",
    "reason": "The backlog exceeds the configured cap",
    "detectedAtLeast": 501,
    "isExact": false,
    "cursorUtc": "2025-01-14T22:00:00Z",
    "scheduleVersion": 2
  }
}
```

A halt never releases itself, not even across a restart. An explicit schedule change is what clears it
(see [Managing schedules at runtime](recurring-tasks/managing-tasks.md)).

---

### GET /tasks/{id}/status-audit

Get one page of the status change history of a task, newest transition first. It is read from the status audit
table, so it answers the same history whichever storage provider is behind the API.

**Path Parameters:**
- `id` (Guid, required): Task ID

**Query Parameters:**
- `skip` (int, optional): Number of transitions to skip, from the newest. Default: `0`
- `take` (int, optional): Number of transitions to return. Default: `100`, `0` asks for the total alone

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/dc49351d-476d-49f0-a1e8-3e2a39182d22/status-audit?skip=0&take=100
Authorization: Bearer {token}
```

**Response:**
```json
{
  "audits": [
    {
      "id": 3,
      "queuedTaskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "updatedAtUtc": "2025-01-15T10:00:05Z",
      "newStatus": "Completed"
    },
    {
      "id": 2,
      "queuedTaskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "updatedAtUtc": "2025-01-15T10:00:00Z",
      "newStatus": "InProgress"
    },
    {
      "id": 1,
      "queuedTaskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "updatedAtUtc": "2025-01-15T09:59:58Z",
      "newStatus": "Queued"
    }
  ],
  "totalCount": 3,
  "skip": 0,
  "take": 100
}
```

`totalCount` is the whole trail, not the page: a long-lived recurring row records one transition per state per
run, so the endpoint pages it (`skip`/`take`) instead of answering the entire history — exactly as
`GET /tasks/{id}/occurrences` does on the other side of the detail. The page is ordered, counted and sliced by
the storage, not in the API. `skip` and `take` are clamped to non-negative.

An entry records the status the row moved TO. There is no `oldStatus`: the one it left is the `newStatus` of
the entry under it.

The order is the audit `id` descending. The transitions of one row are inserted as they happen, so the highest
id is the newest one and no timestamp has to be compared to find it.

`exception` carries the error text of a transition that had one and is absent everywhere else, because the API
omits nulls instead of writing them out.

---

### GET /tasks/{id}/runs-audit

Get one page of the execution history of a task (especially useful for recurring tasks), newest run first, read
from the runs audit table.

**Path Parameters:**
- `id` (Guid, required): Task ID

**Query Parameters:**
- `skip` (int, optional): Number of runs to skip, from the newest. Default: `0`
- `take` (int, optional): Number of runs to return. Default: `100`, `0` asks for the total alone

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/b7c1a4e2-5d3f-4a90-8c11-9f2e5a6d70b3/runs-audit?skip=0&take=100
Authorization: Bearer {token}
```

**Response:**
```json
{
  "audits": [
    {
      "id": 2,
      "queuedTaskId": "b7c1a4e2-5d3f-4a90-8c11-9f2e5a6d70b3",
      "executedAt": "2025-01-15T10:00:05Z",
      "executionTimeMs": 12.5,
      "status": "Completed"
    },
    {
      "id": 1,
      "queuedTaskId": "b7c1a4e2-5d3f-4a90-8c11-9f2e5a6d70b3",
      "executedAt": "2025-01-15T09:00:07Z",
      "executionTimeMs": 0,
      "status": "Failed",
      "exception": "System.Net.Http.HttpRequestException: Connection refused"
    }
  ],
  "totalCount": 2,
  "skip": 0,
  "take": 100
}
```

`totalCount`, `skip` and `take` mean here exactly what they mean on the status trail.

`executedAt` is when the outcome of the run was recorded, which is the end of it and the same instant
`lastExecutionUtc` reports. There is no start column here: the start of the last run is `startedAtUtc` on the
task. `executionTimeMs` is the duration the worker measured around the run, and a run that ended by throwing
reads 0: the measurement is taken where the handler returns, which a failure never reaches.

A run is written by the advance of a recurring row that ran its own handler, so an inline recurring task is
the one that accumulates them. A plain one-shot answers with an empty list, and so do both rows of a durable
schedule: the schedule row runs no handler (its slot means "materialize what is due"), and every occurrence is
a one-shot of its own. What an occurrence did is in its status audit, and how long it took in
`executionTimeMs` on the task.

The row's [audit level](configuration-reference.md) decides how much of this it keeps: `ErrorsOnly` records
the failed runs alone, `None` records nothing. `exception` is absent on a run that carried none.

---

### GET /tasks/{id}/occurrences

The materialized occurrences of a [durable schedule](recurring-tasks/durable-occurrences.md), newest slot
first. Answers an empty list for an inline schedule and for a task that is not a schedule at all.

**Path Parameters:**
- `id` (Guid, required): the schedule row id

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `nonTerminalOnly` | bool | No | `false` | Keep only the occurrences that can still lead to an execution |
| `skip` | int | No | `0` | Occurrences to skip |
| `take` | int | No | `100` | Occurrences to return |

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11/occurrences?take=50
Authorization: Bearer {token}
```

**Response:**
```json
{
  "occurrences": [
    {
      "id": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "parentTaskId": "6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11",
      "status": "Completed",
      "occurrence": {
        "slotUtc": "2025-01-15T10:00:00Z",
        "runNumber": 41,
        "timeZoneId": "Europe/Rome",
        "misfireKind": "CatchUp",
        "missedFromUtc": "2025-01-15T08:00:00Z",
        "missedThroughUtc": "2025-01-15T10:00:00Z",
        "missedCount": 3,
        "missedCountIsExact": true
      },
      "createdAtUtc": "2025-01-15T10:00:01Z",
      "lastExecutionUtc": "2025-01-15T10:00:05Z",
      "startedAtUtc": "2025-01-15T10:00:01Z",
      "executionTimeMs": 12.5,
      "scheduleVersion": 2
    }
  ],
  "totalCount": 41,
  "skip": 0,
  "take": 50
}
```

`totalCount` is the whole series, not the page — and the page is a page all the way down to the database: the
storage orders, counts and slices, so a schedule with a year of retention behind it costs the same request as
a young one. Each occurrence is a task row in its own right, so `GET /tasks/{occurrenceId}` gives it the full
detail treatment — audits and execution logs included.

---

### GET /tasks/counts

Task counts by category, for the dashboard badges.

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/counts
Authorization: Bearer {token}
```

**Response:**
```json
{
  "all": 1234,
  "standard": 1200,
  "recurring": 34,
  "failed": 45,
  "occurrences": 820
}
```

`occurrences` counts the rows a durable schedule materialized. They are one-shot rows, so they are already
inside `standard`: this is that slice, not a sixth disjoint bucket.

---

## Dashboard Endpoints

### GET /dashboard/overview

Get overview statistics for the dashboard.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `range` | string | No | `Today` | Time range (`Today`, `Week`, `Month`, `All`) |

**Example Request:**
```bash
GET /evertask-monitoring/api/dashboard/overview?range=Today
Authorization: Bearer {token}
```

**Response:**
```json
{
  "totalTasksToday": 1234,
  "totalTasksWeek": 8123,
  "successRate": 96.2,
  "failedCount": 45,
  "avgExecutionTimeMs": 1234.56,
  "statusDistribution": {
    "Completed": 1150,
    "Failed": 45,
    "InProgress": 39
  },
  "tasksOverTime": [
    {
      "timestamp": "2025-01-15T00:00:00Z",
      "completed": 100,
      "failed": 5,
      "total": 105
    }
  ],
  "queueSummaries": [
    {
      "queueName": "default",
      "pendingCount": 20,
      "inProgressCount": 4,
      "completedCount": 750,
      "failedCount": 30,
      "throttledCount": 0
    }
  ],
  "throttledTasks": 0,
  "catchUpBacklog": {
    "pending": 12,
    "active": 1,
    "failed": 2,
    "skipped": 0,
    "completed": 805,
    "oldestPendingSlotUtc": "2025-01-15T08:00:00Z",
    "lagSeconds": 7200,
    "haltedSchedules": 1
  }
}
```

`avgExecutionTimeMs` averages the durations the worker measured around the runs of the completed tasks in
range. It is the same column `executionTimeMs` reports per task, and the same average the queue metrics use.
A run nobody measured is left out instead of counted as zero: a failure writes no duration, and neither does a
series finalized without a run.

`catchUpBacklog` counts the occurrences of every [durable schedule](recurring-tasks/durable-occurrences.md)
in the store, whatever the selected range: a backlog is what is owed right now, and a downtime that
materialized its occurrences yesterday is exactly the case the tile exists for.

| Field | Meaning |
|-------|---------|
| `pending` | Materialized and waiting to start |
| `active` | Running right now |
| `failed` | Ended `Failed`, after their retries |
| `skipped` | Cancelled on their own or with their schedule: they will never run |
| `completed` | Ran to completion and still in the store |
| `oldestPendingSlotUtc` | The nominal slot of the oldest occurrence that has not started |
| `lagSeconds` | How far past that slot it already is; `0` when nothing is pending or the slot is still ahead |
| `haltedSchedules` | Durable schedules whose catch-up halted itself over the overflow cap |

Slots a schedule **dropped** never became rows and are therefore absent here — outside the misfire window,
over the overflow cap under `SkipOldest`, or no longer current under `Skip`. Those are reported when they
happen, by the `OccurrenceSkipped` [monitoring event](monitoring-events.md).

---

### GET /dashboard/recent-activity

Get recent task activity.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | `50` | Maximum number of activities to return (max: 100) |

**Example Request:**
```bash
GET /evertask-monitoring/api/dashboard/recent-activity?limit=50
Authorization: Bearer {token}
```

**Response:**
```json
[
  {
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "taskType": "SendEmailTask",
    "status": "Completed",
    "timestamp": "2025-01-15T10:00:05Z",
    "message": "Task completed successfully"
  }
]
```

---

## Queue Endpoints

### GET /queues

Get metrics for all queues.

**Example Request:**
```bash
GET /evertask-monitoring/api/queues
Authorization: Bearer {token}
```

**Response:**
```json
[
  {
    "queueName": "default",
    "totalTasks": 800,
    "queuedTasks": 10,
    "inProgressTasks": 5,
    "completedTasks": 750,
    "failedTasks": 30,
    "cancelledTasks": 5,
    "successRate": 96.2
  }
]
```

---

### GET /queues/{name}/tasks

Get tasks for a specific queue.

**Path Parameters:**
- `name` (string, required): Queue name

**Query Parameters:**
Same as [GET /tasks](#get-tasks)

**Example Request:**
```bash
GET /evertask-monitoring/api/queues/default/tasks?page=1&pageSize=20
Authorization: Bearer {token}
```

**Response:**
Same format as [GET /tasks](#get-tasks)

---

## Statistics Endpoints

### GET /statistics/success-rate-trend

Get success rate trend over time.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `period` | string | No | `Last7Days` | Time period (`Last7Days`, `Last30Days`, `Last90Days`) |

**Example Request:**
```bash
GET /evertask-monitoring/api/statistics/success-rate-trend?period=Last7Days
Authorization: Bearer {token}
```

**Response:**
```json
{
  "period": "Last7Days",
  "dataPoints": [
    {
      "timestamp": "2025-01-15T00:00:00Z",
      "successRate": 96.5,
      "totalTasks": 120,
      "successfulTasks": 116,
      "failedTasks": 4
    }
  ]
}
```

---

### GET /statistics/task-types

Get task distribution by type.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `range` | string | No | `Today` | Time range (`Today`, `Week`, `Month`, `All`) |

**Example Request:**
```bash
GET /evertask-monitoring/api/statistics/task-types?range=Today
Authorization: Bearer {token}
```

**Response:**
```json
{
  "SendEmailTask": 450,
  "ProcessPaymentTask": 320,
  "GenerateReportTask": 150
}
```

---

### GET /statistics/execution-times

Get execution time statistics by task type.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `range` | string | No | `Today` | Time range (`Today`, `Week`, `Month`, `All`) |

**Example Request:**
```bash
GET /evertask-monitoring/api/statistics/execution-times?range=Today
Authorization: Bearer {token}
```

**Response:**
```json
[
  {
    "taskType": "SendEmailTask",
    "averageExecutionTime": 1234.56,
    "minExecutionTime": 500.0,
    "maxExecutionTime": 3000.0,
    "taskCount": 450
  }
]
```

---

## Management Endpoints

The only endpoints of this API that WRITE. They exist behind an authorization of their own, and a host that
upgrades does not gain them:

- `EnableManagementEndpoints` is `false` by default. While it is, **every** path under
  `/evertask-monitoring/api/management` answers `404` — the prefix does not exist.
- Once enabled, a call must still carry the **operate** role: the token returned by logging in with
  `ManagementUsername` / `ManagementPassword`. The dashboard credential and every magic link are read-only
  and get `403`.
- A host with its own authorization can decide instead, with the
  `ManagementAuthorization` hook (`Func<HttpContext, Task<bool>>`), which **replaces** the role check. It runs
  inside routing, after the host's `UseAuthentication`, so `context.User` is the principal the application
  authenticated.

The gate is an MVC authorization filter on these routes, so it holds behind an `app.UsePathBase(...)` too —
the request routing resolves is the request it judges.

Configuration and the full decision order are in
[Monitoring Configuration](configuration-reference.md#enablemanagementendpoints-managementusername--managementpassword-managementauthorization).
No anti-forgery token is needed: the API authenticates with a Bearer header and never with a cookie, so a
cross-site request cannot carry a session.

All three take the task id in the path and no body, and answer the same object:

```json
{
  "status": "Succeeded",
  "message": "The occurrence was put back in the queue",
  "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22"
}
```

| Status | HTTP | Meaning |
|--------|------|---------|
| `Succeeded` | 200 | The operation was applied |
| `NotFound` | 404 | No task carries that id (or the endpoints are disabled) |
| `Conflict` | 409 | The row is not in a state the operation applies to |
| `NotSupported` | 501 | No EverTask host is registered, or the storage lacks the capability |
| `Unavailable` | 503 | The schedule's occurrence provider could not answer; nothing was written |

---

### POST /management/tasks/{id}/requeue

Put a terminal occurrence (`Failed` or `Cancelled`) back in the queue, keeping its id, its history and its
audit trail. It spends no run of the series: on a durable schedule the run budget counts materializations,
and a requeue materializes nothing.

**Path Parameters:**
- `id` (Guid, required): the occurrence row. A schedule row is refused with `409`.

**Example Request:**
```bash
POST /evertask-monitoring/api/management/tasks/dc49351d-476d-49f0-a1e8-3e2a39182d22/requeue
Authorization: Bearer {operate-token}
```

`409` also answers an occurrence that is not terminal any more, and one whose schedule was cancelled — which
is terminal for every row under it.

---

### POST /management/tasks/{id}/resume

Release a durable catch-up that halted itself and hand the schedule back to the materializer, **keeping its
cursor** and therefore its backlog. A halt never releases itself, not even across a restart, so this is the
only thing that puts a halted schedule back to work.

**Path Parameters:**
- `id` (Guid, required): the schedule row.

**Example Request:**
```bash
POST /evertask-monitoring/api/management/tasks/6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11/resume
Authorization: Bearer {operate-token}
```

**Response:**
```json
{
  "status": "Succeeded",
  "message": "The halted catch-up was released and the schedule keeps its backlog",
  "taskId": "6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11",
  "nextRunUtc": "2025-01-15T11:00:00Z",
  "releasedHalt": true
}
```

Because the cursor is kept, the backlog that caused the halt is re-planned against the definition as it
stands now: if it still exceeds the cap, the schedule halts again. To drop the backlog instead, call
`ITaskScheduleManager.ReevaluateSchedule` from application code.

---

### POST /management/tasks/{id}/cancel

Cancel a schedule and, with it, every occurrence of it still pending. Occurrences already executing are left
to finish. The cancellation is **terminal**: the schedule cannot be rescheduled afterwards, nor can one of its
occurrences be requeued — it has to be dispatched again.

**Path Parameters:**
- `id` (Guid, required): the schedule row.

**Example Request:**
```bash
POST /evertask-monitoring/api/management/tasks/6f0f6b0e-6f3e-4b1a-9d5f-2b2f1c0f4a11/cancel
Authorization: Bearer {operate-token}
```

---

**A schedule is addressed by its task key.** `resume` and `cancel` resolve the row's `taskKey` and call
`ITaskScheduleManager` with it, which is the only way a schedule can be named. A schedule dispatched without
a key cannot be operated on from here and answers `409`.

---

## Configuration Endpoint

### GET /config

Get runtime configuration (no authentication required - needed for dashboard initialization).

**Example Request:**
```bash
GET /evertask-monitoring/api/config
```

**Response:**
```json
{
  "apiBasePath": "/evertask-monitoring/api",
  "uiBasePath": "/evertask-monitoring",
  "signalRHubPath": "/evertask-monitoring/hub",
  "requireAuthentication": true,
  "uiEnabled": true,
  "eventDebounceMs": 1000,
  "managementEnabled": false
}
```

`managementEnabled` says whether the write surface exists at all on this host. Whether the CALLER may use it
is a property of the session (`canManage` on the login response), not of the API.

---

## Examples

### C# / HttpClient

```csharp
using System.Net.Http.Json;

var client = new HttpClient
{
    BaseAddress = new Uri("http://localhost:5000/evertask-monitoring/api")
};

// Login
var loginResponse = await client.PostAsJsonAsync("/auth/login", new
{
    username = "admin",
    password = "admin"
});

var loginData = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
var token = loginData.Token;

// Add token to subsequent requests
client.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", token);

// Get tasks
var tasksResponse = await client.GetAsync("/tasks?statuses=Completed&page=1&pageSize=20");
var tasks = await tasksResponse.Content.ReadFromJsonAsync<TasksResponse>();

Console.WriteLine($"Total tasks: {tasks.TotalCount}");
```

### JavaScript / Fetch

```javascript
const API_BASE = 'http://localhost:5000/evertask-monitoring/api';

// Login
const loginResponse = await fetch(`${API_BASE}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'admin', password: 'admin' })
});

const { token } = await loginResponse.json();

// Get tasks
const tasksResponse = await fetch(`${API_BASE}/tasks?statuses=Completed&page=1&pageSize=20`, {
    headers: { 'Authorization': `Bearer ${token}` }
});

const { tasks, totalCount } = await tasksResponse.json();
console.log(`Total tasks: ${totalCount}`);
```

### Python / requests

```python
import requests

API_BASE = 'http://localhost:5000/evertask-monitoring/api'

# Login
login_response = requests.post(f'{API_BASE}/auth/login', json={
    'username': 'admin',
    'password': 'admin'
})

token = login_response.json()['token']

# Get tasks
tasks_response = requests.get(
    f'{API_BASE}/tasks',
    params={'statuses': 'Completed', 'page': 1, 'pageSize': 20},
    headers={'Authorization': f'Bearer {token}'}
)

tasks_data = tasks_response.json()
print(f"Total tasks: {tasks_data['totalCount']}")
```

### cURL

```bash
# Login
TOKEN=$(curl -X POST http://localhost:5000/evertask-monitoring/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin"}' \
  | jq -r '.token')

# Get tasks
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/evertask-monitoring/api/tasks?statuses=Completed&page=1&pageSize=20"

# Get task details
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/evertask-monitoring/api/tasks/dc49351d-476d-49f0-a1e8-3e2a39182d22"

# Get dashboard overview
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/evertask-monitoring/api/dashboard/overview?range=Today"
```

## Next Steps

- **[Monitoring Dashboard](monitoring-dashboard.md)** - Setup, configuration, security
- **[Dashboard UI Guide](monitoring-dashboard-ui.md)** - Visual interface and screenshots
- **[Custom Event Monitoring](monitoring-events.md)** - Event-based monitoring and integrations
- **[Configuration Reference](configuration-reference.md)** - All configuration options
