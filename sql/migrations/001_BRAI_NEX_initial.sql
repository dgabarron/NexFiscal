-- NexFiscal / BRAI_NEX
-- Migration 001: initial persistence schema for NF-e capture.
-- Applied by the NexFiscal setup (HanaSchemaInstaller), which creates ONLY the schema/tables that are missing.
-- Never add DROP, TRUNCATE or ALTER here. Existing tables and data are never touched.
-- All timestamps are UTC and must be written consistently by the application.
-- First install needs CREATE SCHEMA; afterwards the account only needs access to schema BRAI_NEX.
CREATE SCHEMA "BRAI_NEX";

CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_SchemaVersion" (
    "Version" INTEGER NOT NULL,
    "Name" NVARCHAR(120) NOT NULL,
    "AppliedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("Version")
);

CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_Company" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "Cnpj" NVARCHAR(14) NOT NULL,
    "LegalName" NVARCHAR(200) NOT NULL,
    "Environment" TINYINT NOT NULL,
    "Enabled" TINYINT DEFAULT 0 NOT NULL,
    "CreatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId"),
    UNIQUE ("Cnpj", "Environment")
);

CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_CaptureCursor" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "DocumentType" NVARCHAR(16) NOT NULL,
    "LastNsu" DECIMAL(15,0) DEFAULT 0 NOT NULL,
    "NextAllowedAtUtc" TIMESTAMP NOT NULL,
    "Version" BIGINT DEFAULT 0 NOT NULL,
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId", "DocumentType"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);

-- Store all distributed payloads (full XML, summary, event), preserving the original XML bytes.
-- Distinct NSUs may refer to the same fiscal access key: never deduplicate by access key here.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_Envelope" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "DocumentType" NVARCHAR(16) NOT NULL,
    "Nsu" DECIMAL(15,0) NOT NULL,
    "SchemaName" NVARCHAR(200) NOT NULL,
    "XmlSha256" NVARCHAR(64) NOT NULL,
    "XmlBytes" BLOB NOT NULL,
    "ReceivedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId", "DocumentType", "Nsu"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);

CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_CaptureLog" (
    "AttemptId" NVARCHAR(36) NOT NULL,
    "CompanyId" NVARCHAR(36) NOT NULL,
    "DocumentType" NVARCHAR(16) NOT NULL,
    "RequestedNsu" DECIMAL(15,0) NOT NULL,
    "Status" INTEGER,
    "Result" NVARCHAR(64) NOT NULL,
    "Message" NVARCHAR(2000),
    "StartedAtUtc" TIMESTAMP NOT NULL,
    "FinishedAtUtc" TIMESTAMP,
    PRIMARY KEY ("AttemptId")
);

INSERT INTO "BRAI_NEX"."BRAI_NEX_SchemaVersion" ("Version", "Name", "AppliedAtUtc")
VALUES (1, 'initial_nfe_capture', CURRENT_UTCTIMESTAMP);
