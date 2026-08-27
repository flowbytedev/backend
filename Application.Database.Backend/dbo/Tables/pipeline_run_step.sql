-- Step rows are high-volume telemetry rather than user data, so unlike everything else in this database
-- they are HARD-deleted by the retention sweep. No FK to pipeline_run for the same reason: the sweep
-- deletes steps independently of their run.
CREATE TABLE [dbo].[pipeline_run_step] (
    [id]                   NVARCHAR (450) NOT NULL,
    [run_id]               NVARCHAR (450) NOT NULL,
    [company_id]           NVARCHAR (10)  NOT NULL,
    [pipeline_id]          NVARCHAR (450) NULL,
    [node_id]              NVARCHAR (128) NOT NULL,
    [node_type]            NVARCHAR (64)  NULL,
    [node_label]           NVARCHAR (200) NULL,
    [step_index]           INT            DEFAULT ((0)) NOT NULL,
    [attempt]              INT            DEFAULT ((1)) NOT NULL,
    [status]               NVARCHAR (16)  NOT NULL,
    [rows_in]              BIGINT         NULL,
    [rows_out]             BIGINT         NULL,
    -- Rows skipped as unparseable. A run can succeed with a non-zero value here, which
    -- is precisely why it is recorded rather than inferred from a row-count difference.
    [rows_rejected]   BIGINT         NULL,
    [sql_text]             NVARCHAR (MAX) NULL,
    [output_preview_json]  NVARCHAR (MAX) NULL,
    [output_columns_json]  NVARCHAR (MAX) NULL,
    [error]                NVARCHAR (MAX) NULL,
    [error_type]           NVARCHAR (64)  NULL,
    [duration_ms]          INT            DEFAULT ((0)) NOT NULL,
    [started_at]           DATETIME2 (7)  NULL,
    [completed_at]         DATETIME2 (7)  NULL
);
GO

ALTER TABLE [dbo].[pipeline_run_step]
    ADD CONSTRAINT [PK_pipeline_run_step] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- The waterfall's read path, and the poll endpoint's delta query.
CREATE NONCLUSTERED INDEX [IX_pipeline_run_step_run_id_step_index]
    ON [dbo].[pipeline_run_step]([run_id] ASC, [step_index] ASC);
GO

-- Drives the retention sweep.
CREATE NONCLUSTERED INDEX [IX_pipeline_run_step_started_at]
    ON [dbo].[pipeline_run_step]([started_at] ASC);
GO
