CREATE TABLE [dbo].[query_notebook_cell] (
    [id]                     NVARCHAR (450)  NOT NULL,
    [notebook_id]            NVARCHAR (450)  NOT NULL,
    [company_id]             NVARCHAR (10)   NOT NULL,
    [dataset_id]             NVARCHAR (450)  NULL,
    [cell_type]              NVARCHAR (20)   NOT NULL,
    [name]                   NVARCHAR (100)  NULL,
    [sql_text]               NVARCHAR (MAX)  NULL,
    [markdown_text]          NVARCHAR (MAX)  NULL,
    [referenced_cell_ids]    NVARCHAR (MAX)  NULL,
    [snapshot_mode]          BIT             DEFAULT ((0)) NOT NULL,
    [sort_order]             INT             DEFAULT ((0)) NOT NULL,
    [last_run_status]        NVARCHAR (20)   NULL,
    [last_run_error]         NVARCHAR (MAX)  NULL,
    [last_materialized_object] NVARCHAR (150) NULL,
    [created_at]             DATETIME2 (7)   DEFAULT (GETUTCDATE()) NOT NULL,
    [modified_at]            DATETIME2 (7)   NULL
);
GO

ALTER TABLE [dbo].[query_notebook_cell]
    ADD CONSTRAINT [PK_query_notebook_cell] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_query_notebook_cell_company_notebook]
    ON [dbo].[query_notebook_cell]([company_id] ASC, [notebook_id] ASC);
GO

ALTER TABLE [dbo].[query_notebook_cell]
    ADD CONSTRAINT [FK_query_notebook_cell_query_notebook_notebook_id] FOREIGN KEY ([notebook_id]) REFERENCES [dbo].[query_notebook] ([id]);
GO

ALTER TABLE [dbo].[query_notebook_cell]
    ADD CONSTRAINT [FK_query_notebook_cell_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO

ALTER TABLE [dbo].[query_notebook_cell]
    ADD CONSTRAINT [FK_query_notebook_cell_dataset_dataset_id] FOREIGN KEY ([dataset_id]) REFERENCES [dbo].[dataset] ([id]);
GO
