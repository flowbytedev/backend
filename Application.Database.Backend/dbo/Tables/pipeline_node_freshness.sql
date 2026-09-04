-- When each pipeline step last produced its output, plus the freshness verdict last alerted on.
--
-- Why this is stored rather than derived: "last successful materialization" is already in
-- [pipeline_run_step], but PurgeOldStepsAsync hard-deletes those at Pipelines:StepRetentionDays
-- (30 days by default). A step that last ran forty days ago would then have no row at all, and a
-- freshness check reading the history would call the most stale nodes in the system brand new --
-- which inverts exactly the signal the feature exists to give.
--
-- Keyed on (pipeline, node) like [pipeline_state], and no FK to [pipeline]: with the global
-- DeleteBehavior.Restrict an FK here would make a pipeline undeletable once it had run. Orphans are
-- cleared explicitly on delete.
CREATE TABLE [dbo].[pipeline_node_freshness] (
    [id]                   NVARCHAR (450) NOT NULL,
    [pipeline_id]          NVARCHAR (450) NOT NULL,
    [node_id]              NVARCHAR (200) NOT NULL,
    [last_success_at]      DATETIME2 (7)  NULL,
    [last_success_run_id]  NVARCHAR (450) NULL,
    [last_rows_out]        BIGINT         NULL,
    -- Verdict the last sweep recorded. The alert fires on the transition, not the state, so without
    -- this a pipeline stale for a week re-notifies on every pass of the maintenance job.
    [alerted_status]       NVARCHAR (20)  NULL,
    [alerted_at]           DATETIME2 (7)  NULL,
    [company_id]           NVARCHAR (10)  NULL,
    [created_on]           DATETIME2 (7)  NULL,
    [modified_on]          DATETIME2 (7)  NULL,
    [created_by]           NVARCHAR (MAX) NULL,
    [modified_by]          NVARCHAR (MAX) NULL
);
GO

ALTER TABLE [dbo].[pipeline_node_freshness]
    ADD CONSTRAINT [PK_pipeline_node_freshness] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- One row per step. Unique so the engine's upsert at the end of a run, and the sweep's own write,
-- cannot race into two rows that then disagree about when the step last succeeded.
CREATE UNIQUE NONCLUSTERED INDEX [IX_pipeline_node_freshness_pipeline_id_node_id]
    ON [dbo].[pipeline_node_freshness]([company_id] ASC, [pipeline_id] ASC, [node_id] ASC);
GO

ALTER TABLE [dbo].[pipeline_node_freshness]
    ADD CONSTRAINT [FK_pipeline_node_freshness_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO
