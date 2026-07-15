CREATE TABLE [dbo].[company_settings] (
    [id]                    INT            IDENTITY (1, 1) NOT NULL,
    [company_id]            NVARCHAR (10)  NULL,
    [debug_logging_enabled] BIT            DEFAULT ((0)) NOT NULL,
    [created_on]            DATETIME2 (7)  NULL,
    [modified_on]           DATETIME2 (7)  NULL,
    [created_by]            NVARCHAR (MAX) NULL,
    [modified_by]           NVARCHAR (MAX) NULL
);
GO

ALTER TABLE [dbo].[company_settings]
    ADD CONSTRAINT [PK_company_settings] PRIMARY KEY CLUSTERED ([id] ASC);
GO

ALTER TABLE [dbo].[company_settings]
    ADD CONSTRAINT [FK_company_settings_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO

CREATE NONCLUSTERED INDEX [IX_company_settings_company_id]
    ON [dbo].[company_settings]([company_id] ASC);
GO
