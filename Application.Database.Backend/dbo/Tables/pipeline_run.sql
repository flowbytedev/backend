-- NOTE: deliberately NO foreign key to [pipeline]. A run is the audit record of something that happened
-- to real data, so it must outlive the pipeline; and because every relationship in this database is
-- DeleteBehavior.Restrict, an FK would instead make any pipeline with history undeletable. pipeline_name
-- and graph_json are denormalized onto the run so it stays readable and reproducible on its own.
CREATE TABLE [dbo].[pipeline_run] (
    [id]                  NVARCHAR (450) NOT NULL,
    [pipeline_id]         NVARCHAR (450) NOT NULL,
    [company_id]          NVARCHAR (10)  NOT NULL,
    [pipeline_name]       NVARCHAR (150) NULL,
    [status]              NVARCHAR (16)  NOT NULL,
    [trigger_type]        NVARCHAR (16)  NOT NULL,
    [triggered_by]        NVARCHAR (450) NULL,
    [graph_json]          NVARCHAR (MAX) NULL,
    [params_json]         NVARCHAR (MAX) NULL,
    -- Partial run: the node ids the operator selected, as a JSON array. NULL means the whole pipeline ran.
    -- Nullable and added after the fact, so an existing run row reads correctly as a full run.
    [selected_nodes_json] NVARCHAR (MAX) NULL,
    [scratch_dataset_id]  NVARCHAR (450) NULL,
    [error]               NVARCHAR (MAX) NULL,
    [error_type]          NVARCHAR (64)  NULL,
    [error_node_id]       NVARCHAR (128) NULL,
    [steps_total]         INT            DEFAULT ((0)) NOT NULL,
    [steps_completed]     INT            DEFAULT ((0)) NOT NULL,
    [steps_failed]        INT            DEFAULT ((0)) NOT NULL,
    [steps_skipped]       INT            DEFAULT ((0)) NOT NULL,
    [rows_read]           BIGINT         DEFAULT ((0)) NOT NULL,
    [rows_written]        BIGINT         DEFAULT ((0)) NOT NULL,
    -- Rows skipped as unparseable. A run can succeed with a non-zero value here, which
    -- is precisely why it is recorded rather than inferred from a row-count difference.
    [rows_rejected]   BIGINT         DEFAULT ((0)) NOT NULL,
    [duration_ms]         INT            DEFAULT ((0)) NOT NULL,
    [job_id]              NVARCHAR (100) NULL,
    [log]                 NVARCHAR (MAX) NULL,
    [runner_id]           NVARCHAR (64)  NULL,
    [heartbeat_at]        DATETIME2 (7)  NULL,
    [started_at]          DATETIME2 (7)  NOT NULL,
    [finished_at]         DATETIME2 (7)  NULL
);
GO

ALTER TABLE [dbo].[pipeline_run]
    ADD CONSTRAINT [PK_pipeline_run] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- The run-history read path: newest runs of one pipeline.
CREATE NONCLUSTERED INDEX [IX_pipeline_run_pipeline_id_started_at]
    ON [dbo].[pipeline_run]([pipeline_id] ASC, [started_at] DESC);
GO

-- Covers both the processor's claim (queued, oldest first) and the stale-run sweep.
CREATE NONCLUSTERED INDEX [IX_pipeline_run_status_started_at]
    ON [dbo].[pipeline_run]([status] ASC, [started_at] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_pipeline_run_company_id]
    ON [dbo].[pipeline_run]([company_id] ASC);
GO
