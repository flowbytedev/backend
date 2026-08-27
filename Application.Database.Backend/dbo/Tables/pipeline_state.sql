-- What a pipeline remembers between runs: one incremental watermark per source step.
--
-- Keyed on (pipeline, node) rather than pipeline alone, because a pipeline reading two tables
-- incrementally needs two independent high-water marks. No FK to [pipeline]: with the global
-- DeleteBehavior.Restrict an FK here would make a pipeline undeletable once it had run, the same
-- reasoning that keeps pipeline_run free of one. Orphans are cleared explicitly on delete.
CREATE TABLE [dbo].[pipeline_state] (
    [id]                 NVARCHAR (450) NOT NULL,
    [pipeline_id]        NVARCHAR (450) NOT NULL,
    [node_id]            NVARCHAR (200) NOT NULL,
    -- Text, not a typed column: the watermark may be an integer, date, timestamp or string, and the
    -- source query has to embed it as text regardless.
    [watermark_value]    NVARCHAR (400) NULL,
    [watermark_type]     NVARCHAR (64)  NULL,
    [rows_last_run]      BIGINT         NULL,
    [advanced_at]        DATETIME2 (7)  NULL,
    [advanced_by_run_id] NVARCHAR (450) NULL,
    [company_id]         NVARCHAR (10)  NULL,
    [created_on]         DATETIME2 (7)  NULL,
    [modified_on]        DATETIME2 (7)  NULL,
    [created_by]         NVARCHAR (MAX) NULL,
    [modified_by]        NVARCHAR (MAX) NULL
);
GO

ALTER TABLE [dbo].[pipeline_state]
    ADD CONSTRAINT [PK_pipeline_state] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- One watermark per step. Unique so a concurrent double-run cannot create two rows that then
-- alternate, which would make the pipeline re-read the same window forever.
CREATE UNIQUE NONCLUSTERED INDEX [IX_pipeline_state_pipeline_id_node_id]
    ON [dbo].[pipeline_state]([company_id] ASC, [pipeline_id] ASC, [node_id] ASC);
GO

ALTER TABLE [dbo].[pipeline_state]
    ADD CONSTRAINT [FK_pipeline_state_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO
