CREATE TABLE [dbo].[user_rls_filter] (
    [company_id]     NVARCHAR (10)  NOT NULL,
    [user_id]        NVARCHAR (450) NOT NULL,
    [dataset_id]     NVARCHAR (450) NOT NULL,
    [column_name]    NVARCHAR (450) NOT NULL,
    [allowed_values] NVARCHAR (MAX) NOT NULL,
    [created_at]     DATETIME2 (7)  NOT NULL,
    [modified_at]    DATETIME2 (7)  NULL
);
GO

ALTER TABLE [dbo].[user_rls_filter]
    ADD CONSTRAINT [PK_user_rls_filter] PRIMARY KEY CLUSTERED ([company_id] ASC, [user_id] ASC, [dataset_id] ASC, [column_name] ASC);
GO
