CREATE TABLE [dbo].[dataset_column_doc] (
    [id]              NVARCHAR (450)  NOT NULL,
    [company_id]      NVARCHAR (10)   NOT NULL,
    [dataset_id]      NVARCHAR (450)  NOT NULL,
    [table_name]      NVARCHAR (150)  NOT NULL,
    [column_name]     NVARCHAR (150)  NOT NULL,
    [display_name]    NVARCHAR (200)  NULL,
    [description]     NVARCHAR (1000) NULL,
    [semantic_type]   NVARCHAR (60)   NULL,
    [unit]            NVARCHAR (60)   NULL,
    [is_pii]          BIT             DEFAULT ((0)) NOT NULL,
    [pii_type]        NVARCHAR (60)   NULL,
    [is_ai_generated] BIT             DEFAULT ((0)) NOT NULL,
    [generated_at]    DATETIME2 (7)   NULL,
    [edited_by]       NVARCHAR (450)  NULL,
    [modified_at]     DATETIME2 (7)   NULL
);
GO

ALTER TABLE [dbo].[dataset_column_doc]
    ADD CONSTRAINT [PK_dataset_column_doc] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_dataset_column_doc_company_dataset_table]
    ON [dbo].[dataset_column_doc]([company_id] ASC, [dataset_id] ASC, [table_name] ASC);
GO

ALTER TABLE [dbo].[dataset_column_doc]
    ADD CONSTRAINT [FK_dataset_column_doc_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO

ALTER TABLE [dbo].[dataset_column_doc]
    ADD CONSTRAINT [FK_dataset_column_doc_dataset_dataset_id] FOREIGN KEY ([dataset_id]) REFERENCES [dbo].[dataset] ([id]);
GO
