CREATE TABLE [dbo].[user_table_pin] (
    [company_id] NVARCHAR (10)  NOT NULL,
    [user_id]    NVARCHAR (450) NOT NULL,
    [dataset_id] NVARCHAR (450) NOT NULL,
    [table_name] NVARCHAR (450) NOT NULL,
    [created_at] DATETIME2 (7)  NOT NULL
);
GO

ALTER TABLE [dbo].[user_table_pin]
    ADD CONSTRAINT [PK_user_table_pin] PRIMARY KEY CLUSTERED ([company_id] ASC, [user_id] ASC, [dataset_id] ASC, [table_name] ASC);
GO
