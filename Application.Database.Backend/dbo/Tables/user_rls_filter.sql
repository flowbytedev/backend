CREATE TABLE [dbo].[user_rls_filter] (
    [company_id]     NVARCHAR (10)  NOT NULL,
    [user_id]        NVARCHAR (450) NOT NULL,
    [dataset_id]     NVARCHAR (450) NOT NULL,
    -- The table this filter applies to. A column name does not mean the same thing in every table
    -- ("region", "year", "customer_no"), so a filter that named only a column silently applied to every
    -- referenced table having a column of that name, and not at all to tables lacking it.
    --
    -- NOT NULL with a '' default so the DACPAC can add it to a populated table and to the primary key
    -- (SQL Server forbids NULL in a PK). '' is the legacy sentinel meaning "every table having this
    -- column" — the behaviour rows created before this column had. The UI always writes a real table
    -- name, so '' only ever appears on pre-existing rows and should be migrated away: a native RLS
    -- provisioner cannot express it, because a row policy has to name one table.
    [table_name]     NVARCHAR (450) NOT NULL CONSTRAINT [DF_user_rls_filter_table_name] DEFAULT (''),
    [column_name]    NVARCHAR (450) NOT NULL,
    [allowed_values] NVARCHAR (MAX) NOT NULL,
    [created_at]     DATETIME2 (7)  NOT NULL,
    [modified_at]    DATETIME2 (7)  NULL
);
GO

-- table_name sits before column_name so the key stays useful for the common lookup ("this user's filters
-- on this table"), which is what both the query path and the provisioners read.
ALTER TABLE [dbo].[user_rls_filter]
    ADD CONSTRAINT [PK_user_rls_filter] PRIMARY KEY CLUSTERED ([company_id] ASC, [user_id] ASC, [dataset_id] ASC, [table_name] ASC, [column_name] ASC);
GO
