CREATE TABLE [dbo].[whats_new_seen] (
    [user_id]      NVARCHAR (450) NOT NULL,
    [last_seen_at] DATETIME2 (7)  NOT NULL
);
GO

ALTER TABLE [dbo].[whats_new_seen]
    ADD CONSTRAINT [PK_whats_new_seen] PRIMARY KEY CLUSTERED ([user_id] ASC);
GO
