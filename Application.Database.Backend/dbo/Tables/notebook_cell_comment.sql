CREATE TABLE [dbo].[notebook_cell_comment] (
    [id]                 NVARCHAR (450) NOT NULL,
    [notebook_id]        NVARCHAR (MAX) NOT NULL,
    [cell_id]            NVARCHAR (MAX) NOT NULL,
    [user_id]            NVARCHAR (MAX) NOT NULL,
    [content]            NVARCHAR (MAX) NOT NULL,
    [mentioned_user_ids] NVARCHAR (MAX) NOT NULL,
    [created_at]         DATETIME2 (7)  NOT NULL,
    [updated_at]         DATETIME2 (7)  NULL,
    [user_name]          NVARCHAR (MAX) NOT NULL,
    [user_email]         NVARCHAR (MAX) NOT NULL
);
GO

ALTER TABLE [dbo].[notebook_cell_comment]
    ADD CONSTRAINT [PK_notebook_cell_comment] PRIMARY KEY CLUSTERED ([id] ASC);
GO
