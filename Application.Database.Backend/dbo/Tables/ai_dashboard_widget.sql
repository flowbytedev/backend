CREATE TABLE [dbo].[ai_dashboard_widget] (
    [id]           NVARCHAR (450) NOT NULL,
    [dashboard_id] NVARCHAR (450) NOT NULL,
    [company_id]   NVARCHAR (10)  NOT NULL,
    [title]        NVARCHAR (200) NOT NULL,
    [viz_type]     NVARCHAR (30)  NOT NULL,
    [sql_text]     NVARCHAR (MAX) NOT NULL,
    [config_json]  NVARCHAR (MAX) NULL,
    [sort_order]   INT            NOT NULL,
    [created_at]   DATETIME2 (7)  DEFAULT ('0001-01-01T00:00:00.0000000') NOT NULL
);
GO

ALTER TABLE [dbo].[ai_dashboard_widget]
    ADD CONSTRAINT [PK_ai_dashboard_widget] PRIMARY KEY CLUSTERED ([id] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_ai_dashboard_widget_dashboard_id]
    ON [dbo].[ai_dashboard_widget]([dashboard_id] ASC);
GO

ALTER TABLE [dbo].[ai_dashboard_widget]
    ADD CONSTRAINT [FK_ai_dashboard_widget_ai_dashboard_dashboard_id] FOREIGN KEY ([dashboard_id]) REFERENCES [dbo].[ai_dashboard] ([id]);
GO

ALTER TABLE [dbo].[ai_dashboard_widget]
    ADD CONSTRAINT [FK_ai_dashboard_widget_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO
