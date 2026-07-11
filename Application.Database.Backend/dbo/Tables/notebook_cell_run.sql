CREATE TABLE [dbo].[notebook_cell_run] (
    [id]                   NVARCHAR (450) NOT NULL,
    [notebook_id]          NVARCHAR (450) NOT NULL,
    [cell_id]              NVARCHAR (450) NOT NULL,
    [company_id]           NVARCHAR (10)  NOT NULL,
    [status]               NVARCHAR (20)  NOT NULL,
    [error]                NVARCHAR (MAX) NULL,
    [rows_returned]        INT            NULL,
    [elapsed_ms]           BIGINT         NULL,
    [materialized_object]  NVARCHAR (150) NULL,
    [triggered_by]         NVARCHAR (20)  NOT NULL,
    [started_at]           DATETIME2 (7)  NOT NULL
);
GO

ALTER TABLE [dbo].[notebook_cell_run]
    ADD CONSTRAINT [PK_notebook_cell_run] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- Cell rows aren't FK'd to query_notebook_cell — a cell can be deleted while its run history is kept
-- (matches this project's "no cascade deletes" convention without requiring history cleanup on cell delete).
CREATE NONCLUSTERED INDEX [IX_notebook_cell_run_cell_started]
    ON [dbo].[notebook_cell_run]([cell_id] ASC, [started_at] DESC);
GO

ALTER TABLE [dbo].[notebook_cell_run]
    ADD CONSTRAINT [FK_notebook_cell_run_query_notebook_notebook_id] FOREIGN KEY ([notebook_id]) REFERENCES [dbo].[query_notebook] ([id]);
GO
