---
layout: default
title: API Reference
parent: Monitoring
nav_order: 3
---

# Monitoring API Reference

Complete REST API documentation for the EverTask Monitoring Dashboard.

> **Note**: All endpoints are **read-only** in v3.3. Task management endpoints (POST/PUT/DELETE operations for task control) will be added in future releases.

## Table of Contents

- [Base URL](#base-url)
- [Authentication](#authentication)
- [Tasks Endpoints](#tasks-endpoints)
- [Dashboard Endpoints](#dashboard-endpoints)
- [Queue Endpoints](#queue-endpoints)
- [Statistics Endpoints](#statistics-endpoints)
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
  "username": "admin"
}
```

> **Exception**: The `/config` and `/auth/magic` endpoints do not require authentication.

### Magic Link Authentication

If `MagicLinkToken` is configured, you can authenticate via a simple GET request:

**Request:**
```bash
GET /evertask-monitoring/api/auth/magic?token=your-configured-token
```

**Response (200 OK):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2025-01-16T02:00:00Z",
  "username": "admin"
}
```

**Error Responses:**
- `404 Not Found` - Magic link not configured (`MagicLinkToken` is null)
- `401 Unauthorized` - Invalid token

The returned JWT can be used like any other JWT token for subsequent API requests.

## Tasks Endpoints

### GET /tasks

Get paginated list of tasks with filtering and sorting.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `status` | string | No | - | Filter by status (`Queued`, `InProgress`, `Completed`, `Failed`, `Cancelled`) |
| `queueName` | string | No | - | Filter by queue name |
| `taskType` | string | No | - | Filter by task type (partial match) |
| `isRecurring` | bool | No | - | Filter recurring tasks (`true`/`false`) |
| `createdFrom` | DateTime | No | - | Filter by creation date (from) |
| `createdTo` | DateTime | No | - | Filter by creation date (to) |
| `sortBy` | string | No | `CreatedAtUtc` | Sort field |
| `sortDescending` | bool | No | `true` | Sort direction |
| `page` | int | No | `1` | Page number |
| `pageSize` | int | No | `20` | Page size (max: 100) |

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks?status=Completed&page=1&pageSize=20
Authorization: Bearer {token}
```

**Response:**
```json
{
  "tasks": [
    {
      "id": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "taskType": "SendEmailTask",
      "handlerType": "SendEmailHandler",
      "status": "Completed",
      "queueName": "default",
      "createdAtUtc": "2025-01-15T10:00:00Z",
      "lastExecutionUtc": "2025-01-15T10:00:05Z",
      "isRecurring": false,
      "nextRunUtc": null
    }
  ],
  "totalCount": 150,
  "page": 1,
  "pageSize": 20,
  "totalPages": 8
}
```

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
  "taskType": "MyApp.Tasks.SendEmailTask",
  "handlerType": "MyApp.Handlers.SendEmailHandler",
  "status": "Completed",
  "queueName": "default",
  "parameters": "{\"Email\":\"user@example.com\",\"Subject\":\"Welcome\"}",
  "errorDetails": null,
  "createdAtUtc": "2025-01-15T10:00:00Z",
  "scheduledAtUtc": null,
  "lastExecutionUtc": "2025-01-15T10:00:05Z",
  "completedAtUtc": "2025-01-15T10:00:05Z",
  "isRecurring": false,
  "recurringInfo": null,
  "maxRuns": null,
  "currentRunCount": null,
  "nextRunUtc": null,
  "runUntil": null
}
```

---

### GET /tasks/{id}/status-audit

Get status change history for a task.

**Path Parameters:**
- `id` (Guid, required): Task ID

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/dc49351d-476d-49f0-a1e8-3e2a39182d22/status-audit
Authorization: Bearer {token}
```

**Response:**
```json
[
  {
    "id": 1,
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "oldStatus": "Queued",
    "newStatus": "InProgress",
    "changedAtUtc": "2025-01-15T10:00:00Z",
    "errorDetails": null
  },
  {
    "id": 2,
    "taskId": "dc49351d-476d-476d-49f0-a1e8-3e2a39182d22",
    "oldStatus": "InProgress",
    "newStatus": "Completed",
    "changedAtUtc": "2025-01-15T10:00:05Z",
    "errorDetails": null
  }
]
```

---

### GET /tasks/{id}/runs-audit

Get execution history for a task (especially useful for recurring tasks).

**Path Parameters:**
- `id` (Guid, required): Task ID

**Example Request:**
```bash
GET /evertask-monitoring/api/tasks/dc49351d-476d-49f0-a1e8-3e2a39182d22/runs-audit
Authorization: Bearer {token}
```

**Response:**
```json
[
  {
    "id": 1,
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "executionStartedUtc": "2025-01-15T10:00:00Z",
    "executionCompletedUtc": "2025-01-15T10:00:05Z",
    "status": "Completed",
    "errorDetails": null
  }
]
```

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
  "totalTasks": 1234,
  "completedTasks": 1150,
  "failedTasks": 45,
  "activeTasks": 39,
  "successRate": 96.2,
  "averageExecutionTime": 1234.56,
  "activeQueues": 3,
  "recurringTasks": 12,
  "tasksOverTime": [
    {
      "timestamp": "2025-01-15T00:00:00Z",
      "completed": 100,
      "failed": 5
    }
  ],
  "queueSummaries": [
    {
      "queueName": "default",
      "totalTasks": 800,
      "activeTasks": 20,
      "completedTasks": 750,
      "failedTasks": 30
    }
  ]
}
```

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
  "uiEnabled": true
}
```

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
var tasksResponse = await client.GetAsync("/tasks?status=Completed&page=1&pageSize=20");
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
const tasksResponse = await fetch(`${API_BASE}/tasks?status=Completed&page=1&pageSize=20`, {
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
    params={'status': 'Completed', 'page': 1, 'pageSize': 20},
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
  "http://localhost:5000/evertask-monitoring/api/tasks?status=Completed&page=1&pageSize=20"

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
