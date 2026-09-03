-- How one external source enforces per-user column masking and row-level security on its LIVE path.
--
-- Keyed by source entity rather than by dataset: several datasets can be backed by the same connected
-- database, and whether policies can be created there is a property of the source. No FK on
-- source_entity_id -- [entity] lives in the Status database, the same cross-database arrangement as
-- dataset.source_entity_id.
--
-- A source with no row here is Undecided (0), which refuses restricted users on the live path. That is
-- deliberate: the absence of a decision must never read as "no restrictions apply".
CREATE TABLE [dbo].[rls_provisioning_mode] (
    [company_id]            NVARCHAR (10)   NOT NULL,
    [source_entity_id]      NVARCHAR (450)  NOT NULL,

    -- RlsEnforcementMode: 0 = Undecided, 1 = Native (source enforces), 2 = Rewrite (Relay rewrites SQL).
    [mode]                  INT             NOT NULL CONSTRAINT [DF_rls_provisioning_mode_mode] DEFAULT (0),

    -- The operator was offered a provisioning credential and turned it down. Distinct from mode = 2 so
    -- "chose rewriting" is distinguishable from "never asked", and so a re-probe does not re-prompt.
    [provisioning_declined] BIT             NOT NULL CONSTRAINT [DF_rls_provisioning_mode_declined] DEFAULT (0),

    [probed_at]             DATETIME2 (7)   NULL,
    -- Diagnostic text for the UI only. Never parsed to decide enforcement; [mode] is the authority.
    [probe_detail]          NVARCHAR (2000) NULL,

    [decided_by]            NVARCHAR (450)  NULL,
    [decided_at]            DATETIME2 (7)   NULL,
    [created_at]            DATETIME2 (7)   NOT NULL,
    [modified_at]           DATETIME2 (7)   NULL
);
GO

ALTER TABLE [dbo].[rls_provisioning_mode]
    ADD CONSTRAINT [PK_rls_provisioning_mode] PRIMARY KEY CLUSTERED ([company_id] ASC, [source_entity_id] ASC);
GO
