CREATE TABLE [dbo].[pipeline] (
    [id]               NVARCHAR (450) NOT NULL,
    [company_id]       NVARCHAR (10)  NOT NULL,
    [name]             NVARCHAR (150) NOT NULL,
    [description]      NVARCHAR (500) NULL,
    [graph_json]       NVARCHAR (MAX) NULL,
    [schema_version]   INT            DEFAULT ((1)) NOT NULL,
    [is_enabled]       BIT            DEFAULT ((1)) NOT NULL,
    [api_enabled]      BIT            DEFAULT ((0)) NOT NULL,
    [cron_expression]  NVARCHAR (120) NULL,
    [time_zone]        NVARCHAR (80)  NULL,
    [next_run_at]      DATETIME2 (7)  NULL,
    [last_run_at]      DATETIME2 (7)  NULL,
    [last_run_status]  NVARCHAR (40)  NULL,
    [last_run_message] NVARCHAR (MAX) NULL,
    [last_run_rows]    BIGINT         NULL,
    [run_count]        INT            DEFAULT ((0)) NOT NULL,
    [node_count]       INT            DEFAULT ((0)) NOT NULL,
    [valid]            BIT            DEFAULT ((0)) NOT NULL,
    [validation_json]  NVARCHAR (MAX) NULL,
    [created_at]       DATETIME2 (7)  DEFAULT ('0001-01-01T00:00:00.0000000') NOT NULL,
    [created_by]       NVARCHAR (450) NULL,
    [modified_at]      DATETIME2 (7)  NULL,
    [modified_by]      NVARCHAR (450) NULL
);
GO

ALTER TABLE [dbo].[pipeline]
    ADD CONSTRAINT [PK_pipeline] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_pipeline_company_id]
    ON [dbo].[pipeline]([company_id] ASC);
GO

-- Covers the registrar job, which sweeps for schedulable pipelines every few minutes.
CREATE NONCLUSTERED INDEX [IX_pipeline_is_enabled_cron_expression]
    ON [dbo].[pipeline]([is_enabled] ASC, [cron_expression] ASC);
GO

ALTER TABLE [dbo].[pipeline]
    ADD CONSTRAINT [FK_pipeline_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO
