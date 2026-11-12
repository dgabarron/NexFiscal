# Integração frontend ↔ backend — contratos existentes
**Fonte:** `src/NexFiscal.Web/Program.cs` da branch original. O backend não está incluído neste ZIP intencionalmente.

## Rotas disponíveis
| Método | Endpoint | Função |
|---|---|---|
| GET | `/api/health` | estado básico, HANA configurado |
| POST | `/api/setup/hana/test` | testar conexão/estrutura |
| POST | `/api/setup/hana/save` | salvar conexão |
| POST | `/api/setup/hana/provision` | preparar schema/tabelas |
| POST | `/api/certificates/inspect` | validar PFX/P12 e senha, sem cadastro |
| POST | `/api/companies/register` | cadastrar empresa via A1 |
| GET | `/api/capture/status` | empresas cadastradas, NSUs, certificados e cooldown |
| POST | `/api/capture/test-connectivity` | diagnosticar SEFAZ |
| POST | `/api/capture/prepare-handoff` | preparar checkpoint/captura |
| GET | `/api/capture/archive/{companyId}/{archiveId}` | download ZIP local previamente arquivado |
| GET | `/api/recovery/nfe/preview/{companyId}?from=<ISO-date>` | inspecionar recuperação de arquivos locais |
| POST | `/api/recovery/nfe/restore` | restaurar XMLs de arquivos locais |
| POST | `/api/sefaz/nfe/catchup` | sincronização em lote por distNSU |
| GET | `/api/sefaz/nfe/catchup/{companyId}` | status da execução em lote |
| POST | `/api/recovery/nfe/sefaz/{companyId}` | recuperação pontual consNSU; sem payload |
| GET | `/api/capture/schedules/{companyId}` | configurações NFE/CTE/NFSE |
| POST | `/api/capture/schedules/{companyId}` | salvar configurações |
| GET | `/api/cte/status/{companyId}` | cursor/status CT-e |
| POST | `/api/cte/download` | uma consulta manual CT-e |
| POST | `/api/capture/download` | uma consulta manual NF-e |
| POST | `/api/capture/test-once` | consulta/teste específico do fluxo existente |

### Regras de compatibilidade
- Manter métodos e paths. Não alterar nomes de propriedades em JSON nem criar endpoints falsos.
- O arquivo `frontend/src/main.jsx` é a **fonte exata** dos payloads existentes; preservar como referência de integração ao extrair componentes.
- `POST /api/capture/download` recebe `{companyId}`.
- `POST /api/sefaz/nfe/catchup` recebe `{companyId}`.
- `POST /api/recovery/nfe/sefaz/{companyId}` não precisa de dados adicionais; retorna `state, requestedNsu, sefazStatus, xmlCreated, alreadyPresent, message`.
- `POST /api/cte/download` mantém seu próprio contrato `{companyId,previousConsumerStopped:true}` na versão atual.
- As configurações de agenda são um array para NFE, CTE e NFSE: `{documentType,enabled,intervalMinutes,destinationDirectory}`; a UI envia intervalMinutes em string numérica.
- Erros são HTTP 400/409/503 e podem ter `error` ou `detail` conforme rota. Respeitar o status HTTP e a mensagem retornada; não tratar qualquer 200 como “XML salvo”.
- Para `status=138`, podem existir apenas resumos/eventos, sem `procNFe` completo na pasta.
- O frontend novo deve consumir essas rotas reais; se quiser APIs de logs detalhados, paginação, métricas ou permissões, documentar como **proposta de backend**, não simular como entrega pronta.

### Mocks e ambiente de desenvolvimento
É aceitável criar dados **fictícios isolados em testes e Storybook**, desde que não participem do fluxo de produção. O backend atual limita o portal a acesso local e não possui login de usuários implementado.
