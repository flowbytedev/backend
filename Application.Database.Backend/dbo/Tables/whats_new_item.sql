CREATE TABLE [dbo].[whats_new_item] (
    [id]          NVARCHAR (450)  NOT NULL,
    [title]       NVARCHAR (200)  NOT NULL,
    [description] NVARCHAR (1000) NOT NULL,
    [category]    NVARCHAR (40)   NULL,
    [is_active]   BIT             DEFAULT ((1)) NOT NULL,
    [created_at]  DATETIME2 (7)   DEFAULT (GETUTCDATE()) NOT NULL,
    [created_by]  NVARCHAR (450)  NULL
);
GO

ALTER TABLE [dbo].[whats_new_item]
    ADD CONSTRAINT [PK_whats_new_item] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_whats_new_item_created_at]
    ON [dbo].[whats_new_item]([created_at] DESC);
GO
