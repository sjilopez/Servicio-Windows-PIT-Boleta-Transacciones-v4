ALTER TABLE pipeline_logs
    CHANGE COLUMN occurred_at_guatemala occurred_at DATETIME(3) NOT NULL,
    DROP COLUMN utc_offset,
    DROP COLUMN occurred_at_utc;

ALTER TABLE pipeline_logs
    DROP INDEX ix_pipeline_logs_occurred_at,
    ADD INDEX ix_pipeline_logs_occurred_at (occurred_at);