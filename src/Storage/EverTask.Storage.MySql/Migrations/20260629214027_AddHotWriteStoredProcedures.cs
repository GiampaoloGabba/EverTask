using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EverTask.Storage.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddHotWriteStoredProcedures : Migration
    {
        // MySQL/MariaDB hot-write single-roundtrip optimization (the analog of SQL Server's stored procedures /
        // PostgreSQL's writable CTEs). MySQL has read-only CTEs and no UPDATE ... RETURNING, so stored procedures
        // are the only way to collapse the audit insert + row update into one atomic round-trip. Each proc runs a
        // single transaction (START TRANSACTION / COMMIT) with an EXIT HANDLER that rolls back and re-signals, so a
        // mid-statement failure persists nothing. Schema is unqualified: MySQL "schema" == database, so the procs
        // live in (and reference tables in) the connection's database. DROP + CREATE are separate statements and
        // run with suppressTransaction (MySQL DDL implicitly commits, which would break a wrapping migration tx).
        private const string DropSetTaskStatus       = "DROP PROCEDURE IF EXISTS usp_SetTaskStatus;";
        private const string DropUpdateCurrentRun    = "DROP PROCEDURE IF EXISTS usp_UpdateCurrentRun;";
        private const string DropCompleteRecurringRun = "DROP PROCEDURE IF EXISTS usp_CompleteRecurringRun;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropSetTaskStatus, suppressTransaction: true);
            // usp_SetTaskStatus: the audit gate (createAudit) and the terminal-stamp flag (stampLast) are computed
            // in C# from the INPUT status/exception (the audited values are inputs, mirroring the base SetStatus),
            // so they arrive as parameters. The audit is inserted whenever p_CreateAudit = 1 and is NOT gated on a
            // rows-affected count: the base EfCoreTaskStorage.SetStatus likewise inserts the StatusAudit
            // unconditionally and lets the FK reject an orphan. This is deliberate — MySQL ROW_COUNT() returns
            // CHANGED rows (it honors the connection's UseAffectedRows / CLIENT_FOUND_ROWS flag), NOT matched rows
            // like SQL Server's @@ROWCOUNT, so a no-op same-status UPDATE on an existing row could report 0 and
            // wrongly drop the audit. For a non-existent task the INSERT violates the FK -> the EXIT HANDLER rolls
            // back and re-signals -> the C# SetStatus swallows it (same observable result as the base).
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_SetTaskStatus(
    IN p_TaskId CHAR(36),
    IN p_Status VARCHAR(15),
    IN p_Exception LONGTEXT,
    IN p_CreateAudit TINYINT,
    IN p_StampLast TINYINT,
    IN p_ExecutionTimeMs DOUBLE
)
BEGIN
    DECLARE v_now DATETIME(6);
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    SET v_now = UTC_TIMESTAMP(6);

    START TRANSACTION;

    UPDATE QueuedTasks
    SET Status           = p_Status,
        Exception        = p_Exception,
        LastExecutionUtc = CASE WHEN p_StampLast = 1 THEN v_now ELSE LastExecutionUtc END,
        ExecutionTimeMs  = CASE WHEN p_ExecutionTimeMs IS NOT NULL THEN p_ExecutionTimeMs ELSE ExecutionTimeMs END
    WHERE Id = p_TaskId;

    IF p_CreateAudit = 1 THEN
        INSERT INTO StatusAudit (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
        VALUES (p_TaskId, v_now, p_Status, p_Exception);
    END IF;

    COMMIT;
END;", suppressTransaction: true);

            migrationBuilder.Sql(DropUpdateCurrentRun, suppressTransaction: true);
            // usp_UpdateCurrentRun: the RunsAudit gate for ErrorsOnly depends on the ROW's Status/Exception, so it
            // is decided SERVER-SIDE here (never a single C# bool). The row is read FOR UPDATE before the counter
            // update; the UPDATE never touches Status/Exception, so the audited values are the row's own. The run
            // counter SATURATES at int.MaxValue (the base's Option B), and the NOT FOUND handler makes a missing
            // task a no-op (matching the base's early return). AuditLevel: 0=Full, 1=Minimal, 2=ErrorsOnly, 3=None.
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_UpdateCurrentRun(
    IN p_TaskId CHAR(36),
    IN p_ExecutionTimeMs DOUBLE,
    IN p_NextRunUtc DATETIME(6),
    IN p_AuditLevel INT
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

    START TRANSACTION;

    SELECT Status, Exception INTO v_status, v_exception
    FROM QueuedTasks WHERE Id = p_TaskId FOR UPDATE;

    IF v_found = 0 THEN
        ROLLBACK;
    ELSE
        IF p_AuditLevel IN (0, 1) THEN
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

        COMMIT;
    END IF;
END;", suppressTransaction: true);

            migrationBuilder.Sql(DropCompleteRecurringRun, suppressTransaction: true);
            // usp_CompleteRecurringRun: marks the occurrence Completed AND advances the counter / next run in one
            // transaction, so a crash can never split them and resurrect a finished occurrence at recovery. The
            // audited Status/Exception are CONSTANTS (Completed/NULL), so both audit gates are C#-computable from
            // the level and arrive as bits. NextRunUtc is assigned UNCONDITIONALLY (a NULL clears it, making a
            // terminal series unrecoverable). The counter saturates at int.MaxValue. Existence is established
            // up front with SELECT ... FOR UPDATE + a NOT FOUND handler (mirrors the base, which loads the task
            // and returns early when it is gone): a missing task is a clean no-op, NOT a ROW_COUNT() guess
            // (MySQL ROW_COUNT() is changed-rows, config-dependent — see usp_SetTaskStatus).
            migrationBuilder.Sql(@"
CREATE PROCEDURE usp_CompleteRecurringRun(
    IN p_TaskId CHAR(36),
    IN p_ExecutionTimeMs DOUBLE,
    IN p_NextRunUtc DATETIME(6),
    IN p_CreateStatusAudit TINYINT,
    IN p_CreateRunsAudit TINYINT
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

    START TRANSACTION;

    SELECT Id INTO v_dummy FROM QueuedTasks WHERE Id = p_TaskId FOR UPDATE;

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

        COMMIT;
    END IF;
END;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropSetTaskStatus, suppressTransaction: true);
            migrationBuilder.Sql(DropUpdateCurrentRun, suppressTransaction: true);
            migrationBuilder.Sql(DropCompleteRecurringRun, suppressTransaction: true);
        }
    }
}
