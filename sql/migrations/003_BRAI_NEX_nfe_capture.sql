-- NexFiscal / BRAI_NEX
-- Migration 003 (V1 etapa 3): NF-e capture inventory, known-gap recovery queue, durable jobs and settings.
-- Additive only: applied by HanaSchemaInstaller when a table is missing.
-- Never add DROP, TRUNCATE or ALTER here. Cursor, envelopes and logs keep using the tables of migration 001.

-- One row per NF-e access key known for the company (from a summary resNFe and/or a complete procNFe).
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_NfeDocument" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "AccessKey" NVARCHAR(44) NOT NULL,
    "HasCompleteXml" TINYINT DEFAULT 0 NOT NULL,
    "CompleteNsu" DECIMAL(15,0),
    "CompleteSha256" NVARCHAR(64),
    "SummaryNsu" DECIMAL(15,0),
    "Situation" TINYINT,
    "IssuerId" NVARCHAR(14),
    "IssuerName" NVARCHAR(200),
    "IssuedAtUtc" TIMESTAMP,
    "TotalValue" DECIMAL(15,2),
    "FirstSeenAtUtc" TIMESTAMP NOT NULL,
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId", "AccessKey"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);

-- NSUs identified as missing inside a distributed sequence (NT 2014.002, item 3.6). Never arbitrary NSUs.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_NfeRecovery" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "Nsu" DECIMAL(15,0) NOT NULL,
    "State" NVARCHAR(16) NOT NULL,
    "Attempts" INTEGER DEFAULT 0 NOT NULL,
    "LastStatus" INTEGER,
    "CreatedAtUtc" TIMESTAMP NOT NULL,
    "LastAttemptAtUtc" TIMESTAMP,
    PRIMARY KEY ("CompanyId", "Nsu"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);

-- Durable capture requests: survive a backend restart and SEFAZ waiting periods.
CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_CaptureJob" (
    "JobId" NVARCHAR(36) NOT NULL,
    "CompanyId" NVARCHAR(36) NOT NULL,
    "Kind" NVARCHAR(16) NOT NULL,
    "State" NVARCHAR(24) NOT NULL,
    "DestinationDirectory" NVARCHAR(500) NOT NULL,
    "RequestedAtUtc" TIMESTAMP NOT NULL,
    "StartedAtUtc" TIMESTAMP,
    "FinishedAtUtc" TIMESTAMP,
    "NextRunAtUtc" TIMESTAMP,
    "Attempts" INTEGER DEFAULT 0 NOT NULL,
    "NewComplete" INTEGER DEFAULT 0 NOT NULL,
    "AlreadyPresent" INTEGER DEFAULT 0 NOT NULL,
    "Recovered" INTEGER DEFAULT 0 NOT NULL,
    "Ignored" INTEGER DEFAULT 0 NOT NULL,
    "Pending" INTEGER DEFAULT 0 NOT NULL,
    "Failures" INTEGER DEFAULT 0 NOT NULL,
    "LastStatus" INTEGER,
    "Message" NVARCHAR(2000),
    PRIMARY KEY ("JobId"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);

CREATE COLUMN TABLE "BRAI_NEX"."BRAI_NEX_CaptureSettings" (
    "CompanyId" NVARCHAR(36) NOT NULL,
    "DestinationDirectory" NVARCHAR(500) NOT NULL,
    "UpdatedAtUtc" TIMESTAMP NOT NULL,
    PRIMARY KEY ("CompanyId"),
    FOREIGN KEY ("CompanyId") REFERENCES "BRAI_NEX"."BRAI_NEX_Company" ("CompanyId")
);
