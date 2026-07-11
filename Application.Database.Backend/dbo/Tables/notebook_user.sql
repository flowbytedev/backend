CREATE TABLE [dbo].[notebook_user] (
    [notebook_id] NVARCHAR (450) NOT NULL,
    [user_id]     NVARCHAR (450) NOT NULL,
    [type]        INT            NOT NULL,
    [created_at]  DATETIME2 (7)  NOT NULL,
    [modified_at] DATETIME2 (7)  NULL
);
GO

ALTER TABLE [dbo].[notebook_user]
    ADD CONSTRAINT [PK_notebook_user] PRIMARY KEY CLUSTERED ([notebook_id] ASC, [user_id] ASC);
GO

ALTER TABLE [dbo].[notebook_user]
    ADD CONSTRAINT [FK_notebook_user_query_notebook_notebook_id] FOREIGN KEY ([notebook_id]) REFERENCES [dbo].[query_notebook] ([id]);
GO
