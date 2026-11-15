# 02 — Arquitetura técnica, esquema de dados e APIs

## Stack e execução
- Backend **ASP.NET Core / .NET 8**, local em `http://127.0.0.1:5187`.
- Frontend **React + Vite**; UI enxuta PT-BR, pode usar Tailwind + shadcn/ui na evolução (não exigir cópia da UI legada).
- Banco **SAP HANA** intermediário, schema **`BRAI_NEX`**, driver existente `Sap.Data.Hana.Net.v8.0` (ver versão real no `.csproj`).
- Certificado digital A1 `.pfx` / `.p12`, TLS cliente, SEFAZ Distribuição DF-e.
- Persistência protegida ASP.NET Data Protection; no Windows, DPAPI do usuário que executa a aplicação. Evitar guardar senha/PFX em base HANA ou frontend.
- Cliente local não tem autenticação multiusuário; não mudar binding de loopback sem autenticação/TLS e autorização explícita.

## V1 etapa 1 existente (ver código real antes de codificar)
Branch `v1/etapa-01-hana` contém:
- `src/NexFiscal.Web/Program.cs` com endpoints de saúde e setup HANA.
- `src/NexFiscal.Web/HanaSetup.cs` (`TestAsync`, `SaveAsync`, `StatusAsync`).
- `src/NexFiscal.Web/HanaSchemaVerifier.cs` e `HanaSchemaInstaller.cs`.
- `src/NexFiscal.Web/NexFiscalDataDirectory.cs`.
- `sql/migrations/001_BRAI_NEX_initial.sql`.
- `frontend/src/App.jsx`, `frontend/src/api.js`, `frontend/src/styles.css`.
- `scripts/Run-NexFiscalLocal.ps1`.
- CI Linux/Windows em `.github/workflows/etapa01.yml`.

Dados privados por padrão: `%LOCALAPPDATA%\NexFiscal\V1`, override `NEXFISCAL_DATA_DIR` **absoluto**. Preservar `\.nexfiscal-keys\` e `\.nexfiscal-hana.dat` juntos.

## Tabelas de referência herdadas
| Tabela | Finalidade | Colunas centrais |
|---|---|---|
| `BRAI_NEX_SchemaVersion` | Controle de versões | `Version`, `Name`, `AppliedAtUtc` |
| `BRAI_NEX_Company` | Empresas / CNPJ | `CompanyId`, `Cnpj`, `LegalName`, `Environment`, `Enabled`, `CreatedAtUtc` |
| `BRAI_NEX_CaptureCursor` | Estado de distribuição | `CompanyId`, `DocumentType`, `LastNsu`, `NextAllowedAtUtc`, `Version`, `UpdatedAtUtc` |
| `BRAI_NEX_Envelope` | Respostas distribuídas | `CompanyId`, `DocumentType`, `Nsu`, `SchemaName`, `XmlSha256`, `XmlBytes`, `ReceivedAtUtc` |
| `BRAI_NEX_CaptureLog` | Tentativas de consulta | `AttemptId`, `CompanyId`, `DocumentType`, `RequestedNsu`, `Status`, `Result`, `Message`, `StartedAtUtc`, `FinishedAtUtc` |

**Atenção**: esse esquema legado é ponto de partida, não garante sozinho recuperação de lacunas/documentos sem XML completo. Evoluir apenas com migrations idempotentes e compatíveis. Não sobrescrever tabelas antigas.

## Modelo aditivo proposto para F3/F4 (validar em design antes do DDL)
- `NfeDocumentInventory` por empresa/CNPJ, chave 44 caracteres, identificação, NSU de origem (quando conhecido), `HasCompleteXml`, `FilePath`, `Sha256`, `LastSeenUtc`, `EligibilityStatus`.
- `NfeRecoveryQueue` para NSUs/chaves **realmente conhecidos**, causa (XML ausente, arquivo inválido, lacuna explícita), tentativas, estado, horário permitido, resultado de última consulta; impedir fila infinita.
- `CaptureJob` com `JobId`, `CompanyId`, `Kind` (`manual` / `automatic`), estado, `RequestedAtUtc`, `NextRunAtUtc`, contadores, erro categorizado, dados para recuperar execução após reinício.
- `CaptureSchedule` por empresa com `Enabled`, `IntervalMinutes`, `DestinationDirectory`, `NextRunAtUtc`, `UpdatedAtUtc`.
- Registro de consumo SEFAZ (pelo menos último pedido e próximo permitido por CNPJ/documento), com leasing/reserva atômica; evitar disputa entre jobs.

O implementador decide nomes finais de tabelas, chaves, índices e estratégia de persistência, mas precisa garantir:
- atomicidade ou recuperação consistente entre arquivo XML e checkpoint HANA;
- idempotência de salvar o mesmo `procNFe`;
- checkpoint monotônico sem retrocesso;
- recuperação de captura interrompida;
- separação completa de status `resNFe` vs `procNFe`;
- manutenção de histórico quando empresa desativada;
- escalabilidade: não carregar 100k envelopes com BLOBs na memória para cada consulta.

## APIs da etapa 1 já existentes
| Verbo | Endpoint | Contrato |
|---|---|---|
| `GET` | `/api/health` | Produto, versão e etapa |
| `GET` | `/api/setup/hana/status` | `configured`, `connected`, `schemaReady`, `tablesFound`, `message` |
| `POST` | `/api/setup/hana/test` | `{server,port,username,password}`; testa SEM alterar dados |
| `POST` | `/api/setup/hana/save` | Mesmo body; somente se conexão/estrutura válidas, salva credenciais protegidas |

### Exemplo de entrada HANA
```json
{
  "server": "hana.empresa.local",
  "port": 30015,
  "username": "NEXFISCAL_APP",
  "password": "<senha-informada-pelo-usuario>"
}
```
Exemplo não é credencial real; não codificar segredo no repositório.

## APIs PROPOSTAS (não existentes na V1 isolada)
As rotas abaixo são contratos sugeridos, sujeitos ao detalhamento/aceite das etapas 2–4.

| Função | Verbo | Rota | Resultado |
|---|---|---|---|
| F2 | `POST` | `/api/companies/certificates/inspect` | Prévia do certificado sem gravar |
| F2 | `POST` | `/api/companies` | Registra empresa com A1 no cofre |
| F2 | `GET` | `/api/companies` | Lista empresas e status do certificado |
| F2 | `PUT` | `/api/companies/{id}/certificate` | Atualização controlada A1 mesmo CNPJ |
| F2 | `POST` | `/api/companies/{id}/archive` | Desativa/arquiva, preservando histórico |
| F3 | `POST` | `/api/companies/{id}/nfe/captures` | Cria job manual durável |
| F3 | `GET` | `/api/companies/{id}/nfe/captures/{jobId}` | Estado, contagens, próximo horário permitido |
| F3 | `GET` | `/api/companies/{id}/nfe/pending` | Pendências conhecidas, sem chave de entrada manual |
| F4 | `GET` | `/api/companies/{id}/nfe/monitor` | Configuração e status |
| F4 | `PUT` | `/api/companies/{id}/nfe/monitor` | Liga/desliga e define intervalo/destino |

Não adicionar endpoints de inspeção/consulta por chave na interface V1. Não expor credenciais ou PFX em respostas.

## Layout de módulos recomendado
```text
src/
  NexFiscal.Web/            # ASP.NET Core, rotas, DI, arquivos estáticos
  NexFiscal.Capture/        # SOAP mTLS, parser e classificação de documentos
  NexFiscal.Application/    # casos de uso, jobs, regras de rate limit (novo se necessário)
  NexFiscal.Persistence/    # repositórios HANA, migrações (novo se necessário)
frontend/
  src/
    pages/                  # HANA, Empresas, Captura
    components/             # formulários e indicadores simples
    lib/                    # cliente API e utilidades
sql/migrations/
tests/
scripts/
```
NÃO criar camadas só para ornamentar o projeto; simplicidade operacional prevalece.
