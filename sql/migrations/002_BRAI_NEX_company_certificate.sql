-- NexFiscal / BRAI_NEX
-- Migration 002 (V1 etapa 2): certificate metadata and archive state per company.
-- Additive only: applied by HanaSchemaInstaller when the table is missing.
-- Never add DROP, TRUNCATE or ALTER here. The PFX and its password are NOT stored in HANA.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_CompanyCertificate" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "Uf" NVARCHAR(2),
    "Issuer" NVARCHAR(200),
    "Thumbprint" NVARCHAR(64),
    "NotBeforeUtc" TIMESTAMP,
    "NotAfterUtc" TIMESTAMP,
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    "ArchivedAtUtc" TIMESTAMP,
    PRIMARY KEY ("CompanyId"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);
