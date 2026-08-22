global using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EverTask.Tests.Storage")]
// First-party providers share the source-generated log definitions (EfCoreStorageLog, issue #32).
[assembly: InternalsVisibleTo("EverTask.Storage.SqlServer")]
[assembly: InternalsVisibleTo("EverTask.Storage.Postgres")]
[assembly: InternalsVisibleTo("EverTask.Storage.MySql")]
[assembly: InternalsVisibleTo("EverTask.Storage.Sqlite")]
