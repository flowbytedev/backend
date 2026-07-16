CREATE TABLE [dbo].[dataset_user_column] (
    [company_id]  NVARCHAR (10)  NOT NULL,
    [user_id]     NVARCHAR (450) NOT NULL,
    [dataset_id]  NVARCHAR (450) NOT NULL,
    [table_name]  NVARCHAR (450) NOT NULL,
    [column_name] NVARCHAR (450) NOT NULL,
    [created_at]  DATETIME2 (7)  NOT NULL
);
GO

ALTER TABLE [dbo].[dataset_user_column]
    ADD CONSTRAINT [PK_dataset_user_column] PRIMARY KEY CLUSTERED ([company_id] ASC, [user_id] ASC, [dataset_id] ASC, [table_name] ASC, [column_name] ASC);
GO
