-- The write-side credential for an ETL pipeline destination. Mirrors entity_database_admin_credential:
-- it overrides only WHO connects, while host/port/catalog/SSL still come from entity_database_connection.
CREATE TABLE [dbo].[entity_database_write_credential] (
    [id]                 NVARCHAR (450) NOT NULL,
    [entity_id]          NVARCHAR (450) NOT NULL,
    [username]           NVARCHAR (200) NULL,
    [secret_encrypted]   NVARCHAR (MAX) NULL,
    -- Whether a pipeline may issue CREATE TABLE through this credential. Off unless an operator says so.
    [allow_create_table] BIT            DEFAULT ((0)) NOT NULL,
    [company_id]         NVARCHAR (10)  NULL,
    [created_on]         DATETIME2 (7)  NULL,
    [modified_on]        DATETIME2 (7)  NULL,
    [created_by]         NVARCHAR (MAX) NULL,
    [modified_by]        NVARCHAR (MAX) NULL
);
GO

ALTER TABLE [dbo].[entity_database_write_credential]
    ADD CONSTRAINT [PK_entity_database_write_credential] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- One write credential per entity.
CREATE UNIQUE NONCLUSTERED INDEX [IX_entity_database_write_credential_entity_id]
    ON [dbo].[entity_database_write_credential]([entity_id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_entity_database_write_credential_company_id]
    ON [dbo].[entity_database_write_credential]([company_id] ASC);
GO

ALTER TABLE [dbo].[entity_database_write_credential]
    ADD CONSTRAINT [FK_entity_database_write_credential_entity_entity_id] FOREIGN KEY ([entity_id]) REFERENCES [dbo].[entity] ([id]);
GO
