CREATE TABLE [dbo].[query_notebook] (
    [id]                        NVARCHAR (450) NOT NULL,
    [company_id]                NVARCHAR (10)  NOT NULL,
    [name]                      NVARCHAR (150) NOT NULL,
    [description]               NVARCHAR (500) NULL,
    [is_shared]                 BIT            DEFAULT ((0)) NOT NULL,
    [created_at]                DATETIME2 (7)  DEFAULT (GETUTCDATE()) NOT NULL,
    [created_by]                NVARCHAR (450) NULL,
    [modified_at]               DATETIME2 (7)  NULL,
    [cron_expression]           NVARCHAR (100) NULL,
    [schedule_enabled]          BIT            DEFAULT ((0)) NOT NULL,
    [schedule_time_zone]        NVARCHAR (100) NULL,
    [last_scheduled_run_at]     DATETIME2 (7)  NULL,
    [last_scheduled_run_status] NVARCHAR (20)  NULL,
    [last_scheduled_run_error]  NVARCHAR (MAX) NULL
);
GO

ALTER TABLE [dbo].[query_notebook]
    ADD CONSTRAINT [PK_query_notebook] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_query_notebook_company_id]
    ON [dbo].[query_notebook]([company_id] ASC);
GO

ALTER TABLE [dbo].[query_notebook]
    ADD CONSTRAINT [FK_query_notebook_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO
