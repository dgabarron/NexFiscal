-- NexFiscal / BRAI_NEX
-- Migration 004 (V1 etapa 4): automatic NF-e monitoring configuration and schedule per company.
-- Additive only: applied by HanaSchemaInstaller when the table is missing.
-- Never add DROP, TRUNCATE or ALTER here. Monitoring is OFF unless a row has "Enabled" = 1.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_NfeMonitor" (
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
