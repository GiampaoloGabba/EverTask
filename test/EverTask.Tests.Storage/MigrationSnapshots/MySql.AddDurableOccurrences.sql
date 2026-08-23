START TRANSACTION;
ALTER TABLE `QueuedTasks` ADD `ParentTaskId` char(36) COLLATE ascii_general_ci NULL;

ALTER TABLE `QueuedTasks` ADD `RuntimeInfo` longtext CHARACTER SET utf8mb4 NULL;

ALTER TABLE `QueuedTasks` ADD `ScheduleVersion` int NOT NULL DEFAULT 0;

CREATE INDEX `IX_QueuedTasks_ParentTaskId` ON `QueuedTasks` (`ParentTaskId`);

CREATE UNIQUE INDEX `UX_QueuedTasks_Occurrence` ON `QueuedTasks` (`ParentTaskId`, `ScheduledExecutionUtc`);

ALTER TABLE `QueuedTasks` ADD CONSTRAINT `CK_QueuedTasks_OccurrenceSlot` CHECK (ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL);

ALTER TABLE `QueuedTasks` ADD CONSTRAINT `FK_QueuedTasks_QueuedTasks_ParentTaskId` FOREIGN KEY (`ParentTaskId`) REFERENCES `QueuedTasks` (`Id`) ON DELETE RESTRICT;

COMMIT;

DROP PROCEDURE IF EXISTS usp_MaterializeOccurrence;

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

            IF p_NewCursorUtc IS NULL AND p_AuditLevel = 0 THEN
                INSERT INTO StatusAudit (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
                VALUES (p_ParentId, v_now, 'Completed', NULL);
            END IF;

            COMMIT;
        END IF;
    END IF;
END;

DROP PROCEDURE IF EXISTS usp_CancelSchedule;

CREATE PROCEDURE usp_CancelSchedule(
    IN p_ParentId CHAR(36),
    IN p_AuditLevel INT
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

    IF p_AuditLevel = 0 THEN
        INSERT INTO StatusAudit (QueuedTaskId, UpdatedAtUtc, NewStatus, Exception)
        SELECT Id, v_now, 'Cancelled', NULL
        FROM QueuedTasks
        WHERE Id = p_ParentId
           OR (ParentTaskId = p_ParentId AND Status IN ('WaitingQueue', 'Queued', 'Pending'));
    END IF;

    UPDATE QueuedTasks
    SET Status = 'Cancelled'
    WHERE Id = p_ParentId
       OR (ParentTaskId = p_ParentId AND Status IN ('WaitingQueue', 'Queued', 'Pending'));

    COMMIT;
END;

DROP PROCEDURE IF EXISTS usp_UpdateCurrentRunCas;

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

        SET p_Applied = 1;
        COMMIT;
    END IF;
END;

DROP PROCEDURE IF EXISTS usp_CompleteRecurringRunCas;

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
END;

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20260822182836_AddDurableOccurrences', '<EFCORE-VERSION>');