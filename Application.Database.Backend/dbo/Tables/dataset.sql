CREATE TABLE [dbo].[dataset] (
    [id]          NVARCHAR (450) NOT NULL,
    [company_id]  NVARCHAR (10)  NOT NULL,
    [name]        NVARCHAR (100) NOT NULL,
    [description] NVARCHAR (500) NOT NULL,
    [created_at]  DATETIME2 (7)  DEFAULT ('0001-01-01T00:00:00.0000000') NOT NULL,
    [created_by]  NVARCHAR (MAX) NULL,
    [modified_at] DATETIME2 (7)  NULL,
    [source_type]      INT            DEFAULT ((0)) NOT NULL,
    [source_entity_id] NVARCHAR (450) NULL,
    [path]             NVARCHAR (500) DEFAULT ('C:/duckdb') NULL
);
GO

ALTER TABLE [dbo].[dataset]
    ADD CONSTRAINT [PK_dataset] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_dataset_company_id]
    ON [dbo].[dataset]([company_id] ASC);
GO

ALTER TABLE [dbo].[dataset]
    ADD CONSTRAINT [FK_dataset_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO

