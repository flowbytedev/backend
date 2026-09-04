CREATE TABLE [dbo].[company_settings] (
    [id]                    INT            IDENTITY (1, 1) NOT NULL,
    [company_id]            NVARCHAR (10)  NULL,
    [debug_logging_enabled] BIT            DEFAULT ((0)) NOT NULL,
    -- Date pattern used when writing dates into CSV exports (e.g. 'dd/MM/yyyy'). NULL means "not chosen
    -- yet" and the app falls back to its dd/MM/yyyy default, so existing rows need no backfill.
    [export_date_format]    NVARCHAR (20)  NULL,
    -- Whether this company's pipelines are checked against their freshness policies. NULL means "not
    -- chosen yet" and reads as enabled, so existing rows need no backfill. Subordinate to the
    -- Pipelines:FreshnessChecksEnabled deployment kill switch.
    [freshness_checks_enabled]   BIT            NULL,
    -- ';'-separated emails for stale-step alerts. NULL/empty falls back to
    -- Pipelines:FreshnessAlertRecipients; empty everywhere means verdicts are recorded but no mail is sent.
    [freshness_alert_recipients] NVARCHAR (1000) NULL,
    -- Folder this company's pipeline runs stage files in (source CSVs, API JSON, blob downloads).
    -- NULL means "not chosen yet" and falls back to the OS temp folder, which is what every one of
    -- those call sites used before this column existed - so existing rows need no backfill.
    [pipeline_working_directory]  NVARCHAR (400)  NULL,
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
