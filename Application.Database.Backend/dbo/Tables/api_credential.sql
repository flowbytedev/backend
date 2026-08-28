-- A named HTTP credential used by source.api / destination.api pipeline steps.
--
-- Why the secret lives here and not on the pipeline node: pipeline.graph_json is stored in the clear, is
-- sent to the browser whole, and is editable as YAML by the pipeline author. A token in node config would
-- be readable by anyone who can open the editor and would be copied by "duplicate pipeline". The node
-- stores this row's [name] only.
CREATE TABLE [dbo].[api_credential] (
    [id]               NVARCHAR (450)  NOT NULL,
    [name]             NVARCHAR (200)  NOT NULL,
    [description]      NVARCHAR (1000) NULL,
    -- Optional base. A step's URL is resolved against it and must stay on this host, which is what stops a
    -- step from aiming this credential's token at an arbitrary server.
    [base_url]         NVARCHAR (1000) NULL,
    -- none | bearer | basic | header | query | form | oauth2
    [auth_type]        NVARCHAR (20)   DEFAULT ('none') NOT NULL,
    [username]         NVARCHAR (200)  NULL,
    [secret_encrypted] NVARCHAR (MAX)  NULL,
    -- Header or query-string parameter carrying the secret, for auth_type header/query respectively.
    [header_name]      NVARCHAR (100)  NULL,
    [query_param_name] NVARCHAR (100)  NULL,
    -- Form field carrying the secret, for auth_type 'form' - an OAuth2 client_secret. Nullable and added
    -- after the fact, so every existing row reads correctly as before.
    [form_field_name]  NVARCHAR (100)  NULL,
    -- OAuth2 client credentials: the token endpoint, and the non-secret fields posted to it as JSON.
    -- The client_secret is NOT here - it stays in secret_encrypted under form_field_name. Both nullable and
    -- added after the fact, so every existing row reads correctly as before.
    [token_url]        NVARCHAR (1000) NULL,
    [token_fields_json] NVARCHAR (MAX) NULL,
    -- Static headers sent on every request, as a JSON object.
    [extra_headers_json] NVARCHAR (MAX) NULL,
    -- Whether destination.api may SEND data with this credential. Off unless an operator says so: a read
    -- token that happens to have write scope must not become a write path because someone added a node.
    [allow_write]      BIT             DEFAULT ((0)) NOT NULL,
    [is_enabled]       BIT             DEFAULT ((1)) NOT NULL,
    [timeout_seconds]  INT             NULL,
    [company_id]       NVARCHAR (10)   NULL,
    [created_on]       DATETIME2 (7)   NULL,
    [modified_on]      DATETIME2 (7)   NULL,
    [created_by]       NVARCHAR (MAX)  NULL,
    [modified_by]      NVARCHAR (MAX)  NULL
);
GO

ALTER TABLE [dbo].[api_credential]
    ADD CONSTRAINT [PK_api_credential] PRIMARY KEY CLUSTERED ([id] ASC);
GO

-- The YAML view addresses a credential by name, so the name must resolve to exactly one row per company.
CREATE UNIQUE NONCLUSTERED INDEX [IX_api_credential_company_id_name]
    ON [dbo].[api_credential]([company_id] ASC, [name] ASC);
GO

ALTER TABLE [dbo].[api_credential]
    ADD CONSTRAINT [FK_api_credential_company_company_id] FOREIGN KEY ([company_id]) REFERENCES [dbo].[company] ([id]);
GO
