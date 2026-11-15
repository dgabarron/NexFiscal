-- NexFiscal / BRAI_NEX
-- Migration 005 (etapa 5): NFS-e nacional (ADN) inventory and per-company capture/monitoring configuration.
-- Additive only: applied by HanaSchemaInstaller when a table is missing.
-- Never add DROP, TRUNCATE or ALTER here. Cursor, envelopes and logs use the tables of migration 001 with DocumentType 'NFSE'.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_NfseDocument" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "AccessKey" NVARCHAR(50) NOT NULL,
    "Nsu" DECIMAL(15,0),
    "Sha256" NVARCHAR(64) NOT NULL,
    "Cancelled" TINYINT DEFAULT 0 NOT NULL,
    "IssuerId" NVARCHAR(14),
    "IssuerName" NVARCHAR(200),
    "IssuedAtUtc" TIMESTAMP,
    "TotalValue" DECIMAL(15,2),
    "FirstSeenAtUtc" TIMESTAMP NOT NULL,
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId", "AccessKey"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);

-- Destination folder and automatic monitoring of NFS-e. Monitoring is OFF unless "Enabled" = 1.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_NfseMonitor" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "Enabled" TINYINT DEFAULT 0 NOT NULL,
    "IntervalMinutes" INTEGER NOT NULL,
    "DestinationDirectory" NVARCHAR(500) NOT NULL,
    "NextRunAtUtc" TIMESTAMP,
    "LastRunAtUtc" TIMESTAMP,
    "LastMessage" NVARCHAR(500),
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);
