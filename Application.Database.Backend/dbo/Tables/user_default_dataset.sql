CREATE TABLE [dbo].[user_default_dataset] (
    [company_id]  NVARCHAR (10)  NOT NULL,
    [user_id]     NVARCHAR (450) NOT NULL,
    [dataset_id]  NVARCHAR (450) NOT NULL,
    [modified_at] DATETIME2 (7)  NOT NULL
);
GO

ALTER TABLE [dbo].[user_default_dataset]
    ADD CONSTRAINT [PK_user_default_dataset] PRIMARY KEY CLUSTERED ([company_id] ASC, [user_id] ASC);
GO
