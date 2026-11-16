-- NexFiscal / BRAI_NEX
-- Migration 006 (etapa 6): recipient manifestation events sent by the operator (NT 2020.001 v1.60).
-- Additive only: applied by HanaSchemaInstaller when a table is missing.
-- Never add DROP, TRUNCATE or ALTER here. The NF-e inventory, cursor and envelopes are not touched.

-- One row per NF-e and event type (210210 = Ciencia da Operacao): the last answer given by SEFAZ.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_NfeManifest" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "AccessKey" NVARCHAR(44) NOT NULL,
    "EventType" INTEGER NOT NULL,
    "State" NVARCHAR(16) NOT NULL,
    "Status" INTEGER,
    "Reason" NVARCHAR(500),
    "Protocol" NVARCHAR(20),
    "RegisteredAtUtc" TIMESTAMP,
    "Attempts" INTEGER DEFAULT 0 NOT NULL,
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId", "AccessKey", "EventType"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);
