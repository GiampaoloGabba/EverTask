using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableOccurrences : Migration
    {
        // Same mechanics as AddHotWriteStoredProcedures: MySQL/MariaDB have read-only CTEs and no
        // UPDATE ... RETURNING, so a stored procedure is the only way to collapse these multi-statement
        // operations into one atomic round-trip. DROP and CREATE are separate Sql calls with
        // suppressTransaction (MySQL DDL implicitly commits, which would break a wrapping migration
        // transaction), and every proc runs one START TRANSACTION / COMMIT with an EXIT HANDLER that rolls
        // back and re-signals. Names are unqualified: a MySQL "schema" IS the connection's database.
        private const string DropMaterializeOccurrence   = "DROP PROCEDURE IF EXISTS usp_MaterializeOccurrence;";
        private const string DropCancelSchedule          = "DROP PROCEDURE IF EXISTS usp_CancelSchedule;";
        private const string DropUpdateCurrentRunCas     = "DROP PROCEDURE IF EXISTS usp_UpdateCurrentRunCas;";
        private const string DropCompleteRecurringRunCas = "DROP PROCEDURE IF EXISTS usp_CompleteRecurringRunCas;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                table: "QueuedTasks",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "RuntimeInfo",
                table: "QueuedTasks",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ScheduleVersion",
                table: "QueuedTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                table: "QueuedTasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedTasks_ParentTaskId_Status",
                table: "QueuedTasks",
                columns: new[] { "ParentTaskId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_QueuedTasks_Occurrence",
                table: "QueuedTasks",
                columns: new[] { "ParentTaskId", "ScheduledExecutionUtc" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                table: "QueuedTasks",
                sql: "ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                table: "QueuedTasks",
                column: "ParentTaskId",
                principalTable: "QueuedTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            CreateDurableOccurrenceProcedures(migrationBuilder);
        }

        /// <summary>
        /// The operations that run at every occurrence, as stored procedures — the same tier as the three
        /// pre-existing hot writes. Each is one round-trip and one transaction, with the compare-and-swap
        /// inside the procedure so two hosts reading the same cursor cannot both advance it.
        /// </summary>
        private static void CreateDurableOccurrenceProcedures(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropMaterializeOccurrence, suppressTransaction: true);
            // p_Outcome mirrors OccurrenceMaterializationOutcome: 0 Created, 1 AlreadyExists, 2 CursorMoved,
            // 3 VersionMismatch, 4 ParentInactive. The schedule row is read FOR UPDATE, which is what
            // serializes two materializers against the same cursor; a rows-affected count could not be
            // trusted here anyway (MySQL ROW_COUNT() reports CHANGED rows, not matched ones).
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_MaterializeOccurrence(
    IN p_ParentId CHAR(36),
    IN p_ExpectedScheduleVersion INT,
    IN p_ExpectedCursorUtc DATETIME(6),
    IN p_NewCursorUtc DATETIME(6),
    IN p_AuditLevel INT,
    IN p_OccurrenceId CHAR(36),
    IN p_SlotUtc DATETIME(6),
    IN p_CreatedAtUtc DATETIME(6),
    IN p_Type VARCHAR(500),
    IN p_Request LONGTEXT,
    IN p_Handler VARCHAR(500),
    IN p_QueueName LONGTEXT,
    IN p_OccurrenceAuditLevel INT,
    IN p_RuntimeInfo LONGTEXT,
    OUT p_Outcome INT
)
BEGIN
    DECLARE v_now DATETIME(6);
    DECLARE v_found INT DEFAULT 1;
    DECLARE v_version INT;
    DECLARE v_cursor DATETIME(6);
    DECLARE v_status VARCHAR(15);
    DECLARE v_existing INT DEFAULT 0;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_found = 0;

    SET v_now = UTC_TIMESTAMP(6);
    SET p_Outcome = 0;

    START TRANSACTION;

    SELECT ScheduleVersion, NextRunUtc, Status INTO v_version, v_cursor, v_status
    FROM QueuedTasks WHERE Id = p_ParentId FOR UPDATE;

    IF v_found = 0 OR v_status = 'Cancelled' OR v_cursor IS NULL THEN
        SET p_Outcome = 4;
        ROLLBACK;
    ELSEIF v_version <> p_ExpectedScheduleVersion THEN
        SET p_Outcome = 3;
        ROLLBACK;
    ELSEIF p_ExpectedCursorUtc IS NULL OR v_cursor <> p_ExpectedCursorUtc THEN
        SET p_Outcome = 2;
        ROLLBACK;
    ELSE
        SELECT COUNT(*) INTO v_existing
        FROM QueuedTasks WHERE ParentTaskId = p_ParentId AND ScheduledExecutionUtc = p_SlotUtc;

        IF v_existing > 0 THEN
            SET p_Outcome = 1;
            ROLLBACK;
        ELSE
            INSERT INTO QueuedTasks
                (Id, CreatedAtUtc, ExecutionTimeMs, ScheduledExecutionUtc, Type, Request, Handler, IsRecurring,
                 CurrentRunCount, QueueName, AuditLevel, Status, ParentTaskId, RuntimeInfo, ScheduleVersion)
            VALUES
                (p_OccurrenceId, p_CreatedAtUtc, 0, p_SlotUtc, p_Type, p_Request, p_Handler, 0,
                 0, p_QueueName, p_OccurrenceAuditLevel, 'WaitingQueue', p_ParentId, p_RuntimeInfo,
                 p_ExpectedScheduleVersion);

            UPDATE QueuedTasks
            SET NextRunUtc       = p_NewCursorUtc,
                CurrentRunCount  = CASE WHEN COALESCE(CurrentRunCount, 0) >= 2147483647
                                        THEN 2147483647 ELSE COALESCE(CurrentRunCount, 0) + 1 END,
                Status           = CASE WHEN p_NewCursorUtc IS NULL THEN 'Completed' ELSE Status END,
                Exception        = CASE WHEN p_NewCursorUtc IS NULL THEN NULL ELSE Exception END,
                LastExecutionUtc = CASE WHEN p_NewCursorUtc IS NULL THEN v_now ELSE LastExecutionUtc END
            WHERE Id = p_ParentId;

            IF p_NewCursorUtc IS NULL AND (p_AuditLevel = 0 OR p_AuditLevel NOT IN (0, 1, 2, 3)) THEN
                INSERT INTO StatusAudit (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
                VALUES (p_ParentId, v_now, 'Completed', NULL);
            END IF;

            COMMIT;
        END IF;
    END IF;
END;", suppressTransaction: true);

            migrationBuilder.Sql(DropCancelSchedule, suppressTransaction: true);
            // The parent lock serializes materialization, while the temporary table captures the exact child
            // rows locked and changed by this cancellation. Unknown audit levels fall back to Full.
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_CancelSchedule(
    IN p_ParentId CHAR(36),
    IN p_AuditLevel INT
)
BEGIN
    DECLARE v_now DATETIME(6);
    DECLARE v_parentId CHAR(36);
    DECLARE v_found INT DEFAULT 1;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        DROP TEMPORARY TABLE IF EXISTS tmp_CancelScheduleIds;
        ROLLBACK;
        RESIGNAL;
    END;
    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_found = 0;

    SET v_now = UTC_TIMESTAMP(6);

    START TRANSACTION;

    SELECT Id INTO v_parentId
    FROM QueuedTasks
    WHERE Id = p_ParentId FOR UPDATE;

    IF v_found = 0 THEN
        ROLLBACK;
    ELSE
        DROP TEMPORARY TABLE IF EXISTS tmp_CancelScheduleIds;
        CREATE TEMPORARY TABLE tmp_CancelScheduleIds (
            Id CHAR(36) COLLATE ascii_general_ci NOT NULL PRIMARY KEY
        ) ENGINE = MEMORY;

        INSERT INTO tmp_CancelScheduleIds (Id) VALUES (v_parentId);
        INSERT INTO tmp_CancelScheduleIds (Id)
        SELECT Id
        FROM QueuedTasks
        WHERE ParentTaskId = p_ParentId
          AND Status IN ('WaitingQueue', 'Queued', 'Pending', 'ServiceStopped')
        FOR UPDATE;

        UPDATE QueuedTasks AS task
        INNER JOIN tmp_CancelScheduleIds AS cancelled ON cancelled.Id = task.Id
        SET task.Status = 'Cancelled';

        IF p_AuditLevel = 0 OR p_AuditLevel NOT IN (0, 1, 2, 3) THEN
            INSERT INTO StatusAudit (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
            SELECT Id, v_now, 'Cancelled', NULL FROM tmp_CancelScheduleIds;
        END IF;

        DROP TEMPORARY TABLE tmp_CancelScheduleIds;
        COMMIT;
    END IF;
END;", suppressTransaction: true);

            migrationBuilder.Sql(DropUpdateCurrentRunCas, suppressTransaction: true);
            // usp_UpdateCurrentRun with the schedule version in the row lookup: a run that finishes after a
            // reschedule writes nothing and reports the loss instead of forcing its stale next run.
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_UpdateCurrentRunCas(
    IN p_TaskId CHAR(36),
    IN p_ExecutionTimeMs DOUBLE,
    IN p_NextRunUtc DATETIME(6),
    IN p_AuditLevel INT,
    IN p_ExpectedScheduleVersion INT,
    OUT p_Applied TINYINT
)
BEGIN
    DECLARE v_now DATETIME(6);
    DECLARE v_status VARCHAR(15);
    DECLARE v_exception LONGTEXT;
    DECLARE v_found INT DEFAULT 1;
    DECLARE v_shouldAudit INT DEFAULT 0;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_found = 0;

    SET v_now = UTC_TIMESTAMP(6);
    SET p_Applied = 0;

    START TRANSACTION;

    SELECT Status, Exception INTO v_status, v_exception
    FROM QueuedTasks
    WHERE Id = p_TaskId AND ScheduleVersion = p_ExpectedScheduleVersion FOR UPDATE;

    IF v_found = 0 THEN
        ROLLBACK;
    ELSE
        IF p_AuditLevel IN (0, 1) OR p_AuditLevel NOT IN (0, 1, 2, 3) THEN
            SET v_shouldAudit = 1;
        ELSEIF p_AuditLevel = 2 AND (v_status = 'Failed' OR (v_exception IS NOT NULL AND v_exception <> '')) THEN
            SET v_shouldAudit = 1;
        END IF;

        UPDATE QueuedTasks
        SET ExecutionTimeMs = p_ExecutionTimeMs,
            NextRunUtc      = p_NextRunUtc,
            CurrentRunCount = CASE WHEN COALESCE(CurrentRunCount, 0) >= 2147483647
                                   THEN 2147483647 ELSE COALESCE(CurrentRunCount, 0) + 1 END
        WHERE Id = p_TaskId;

        IF v_shouldAudit = 1 THEN
            INSERT INTO RunsAudit (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
            VALUES (p_TaskId, v_now, p_ExecutionTimeMs, v_status, v_exception);
        END IF;

        SET p_Applied = 1;
        COMMIT;
    END IF;
END;", suppressTransaction: true);

            migrationBuilder.Sql(DropCompleteRecurringRunCas, suppressTransaction: true);
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_CompleteRecurringRunCas(
    IN p_TaskId CHAR(36),
    IN p_ExecutionTimeMs DOUBLE,
    IN p_NextRunUtc DATETIME(6),
    IN p_CreateStatusAudit TINYINT,
    IN p_CreateRunsAudit TINYINT,
    IN p_ExpectedScheduleVersion INT,
    OUT p_Applied TINYINT
)
BEGIN
    DECLARE v_now DATETIME(6);
    DECLARE v_found INT DEFAULT 1;
    DECLARE v_dummy CHAR(36);
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_found = 0;

    SET v_now = UTC_TIMESTAMP(6);
    SET p_Applied = 0;

    START TRANSACTION;

    SELECT Id INTO v_dummy
    FROM QueuedTasks
    WHERE Id = p_TaskId AND ScheduleVersion = p_ExpectedScheduleVersion FOR UPDATE;

    IF v_found = 0 THEN
        ROLLBACK;
    ELSE
        UPDATE QueuedTasks
        SET Status           = 'Completed',
            Exception        = NULL,
            LastExecutionUtc = v_now,
            ExecutionTimeMs  = p_ExecutionTimeMs,
            NextRunUtc       = p_NextRunUtc,
            CurrentRunCount  = CASE WHEN COALESCE(CurrentRunCount, 0) >= 2147483647
                                    THEN 2147483647 ELSE COALESCE(CurrentRunCount, 0) + 1 END
        WHERE Id = p_TaskId;

        IF p_CreateStatusAudit = 1 THEN
            INSERT INTO StatusAudit (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
            VALUES (p_TaskId, v_now, 'Completed', NULL);
        END IF;
        IF p_CreateRunsAudit = 1 THEN
            INSERT INTO RunsAudit (QueuedTaskId, ExecutedAt, ExecutionTimeMs, Status, Exception)
            VALUES (p_TaskId, v_now, p_ExecutionTimeMs, 'Completed', NULL);
        END IF;

        SET p_Applied = 1;
        COMMIT;
    END IF;
END;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // All four procedures are NEW in this migration (the three pre-existing hot-write procs are
            // untouched), so undoing means dropping them — there is no previous body to restore.
            migrationBuilder.Sql(DropMaterializeOccurrence, suppressTransaction: true);
            migrationBuilder.Sql(DropCancelSchedule, suppressTransaction: true);
            migrationBuilder.Sql(DropUpdateCurrentRunCas, suppressTransaction: true);
            migrationBuilder.Sql(DropCompleteRecurringRunCas, suppressTransaction: true);

            migrationBuilder.Sql("DELETE FROM `QueuedTasks` WHERE `ParentTaskId` IS NOT NULL");

            migrationBuilder.DropForeignKey(
                name: "FK_QueuedTasks_QueuedTasks_ParentTaskId",
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_ParentTaskId",
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueuedTasks_ParentTaskId_Status",
                table: "QueuedTasks");

            migrationBuilder.DropIndex(
                name: "UX_QueuedTasks_Occurrence",
                table: "QueuedTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QueuedTasks_OccurrenceSlot",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "RuntimeInfo",
                table: "QueuedTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                table: "QueuedTasks");
        }
    }
}
