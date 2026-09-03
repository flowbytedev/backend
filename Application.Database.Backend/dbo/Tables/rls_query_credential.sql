-- The UNPRIVILEGED identity that runs end-user queries against one external source in Native mode.
--
-- Why a second credential exists at all: in ClickHouse (and PostgreSQL) privileges are the UNION of a
-- principal's own grants and whatever roles are enabled. Enabling a restricted role on a privileged
-- connection therefore restricts nothing -- the admin keeps its own SELECT on everything. So queries must
-- run as a principal holding no grants of its own, whose only access comes from the role named on the
-- request. Verified against ClickHouse 25.12: with DEFAULT ROLE NONE and no direct grants, a request
-- naming no role is refused outright.
--
-- The provisioning credential (entity_database_admin_credential, Status DB) creates the roles and
-- policies; this one only reads. They must never be the same principal.
CREATE TABLE [dbo].[rls_query_credential] (
    [company_id]       NVARCHAR (10)   NOT NULL,
    [source_entity_id] NVARCHAR (450)  NOT NULL,

    -- Login this app created on the source. Generated, not operator-supplied.
    [username]         NVARCHAR (200)  NOT NULL,

    -- Encrypted with ICredentialProtector, as every other stored secret here is. Never returned to a
    -- browser: it is a working database login, and the whole enforcement model rests on it staying
    -- ungranted.
    [secret_encrypted] NVARCHAR (MAX)  NOT NULL,

    [created_at]       DATETIME2 (7)   NOT NULL,
    [modified_at]      DATETIME2 (7)   NULL
);
GO

ALTER TABLE [dbo].[rls_query_credential]
    ADD CONSTRAINT [PK_rls_query_credential] PRIMARY KEY CLUSTERED ([company_id] ASC, [source_entity_id] ASC);
GO
