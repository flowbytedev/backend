CREATE TABLE [dbo].[user_dataset_pin] (
    [company_id] NVARCHAR (10)  NOT NULL,
    [user_id]    NVARCHAR (450) NOT NULL,
    [dataset_id] NVARCHAR (450) NOT NULL,
    [created_at] DATETIME2 (7)  NOT NULL
);
GO

ALTER TABLE [dbo].[user_dataset_pin]
    ADD CONSTRAINT [PK_user_dataset_pin] PRIMARY KEY CLUSTERED ([company_id] ASC, [user_id] ASC, [dataset_id] ASC);
GO
