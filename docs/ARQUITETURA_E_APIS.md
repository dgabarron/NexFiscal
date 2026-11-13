# NexFiscal — arquitetura e contratos da versão atual

**Referência técnica para QA:** `qa/frontend-enxuto-20261113` em 13/11/2026. Este documento descreve o comportamento implementado no código, não a homologação de uma instalação real.

## Componentes e dados

| Componente | Responsabilidade |
|---|---|
| `frontend/` — React 18, Vite 5, Tailwind 4 | Abas NF-e (duas funções) e CT-e (consulta manual), empresas e configuração técnica |
| `src/NexFiscal.Web/` — ASP.NET Core .NET 8 | API local, cofre A1, HANA, monitoramento e consulta sob demanda |
| `NfeMonitoringWorker` | Avaliar agendamento configurado e consultar quando permitidos intervalo e janela fiscal |
| `NfeSefazCatchup` | Job manual sequencial de distribuição disponível, com progresso |
| `NfeFirstDownload` | Executar uma chamada fiscal `distNSU`, validar A1, reservar consulta, salvar lotes e XMLs |
| `NfeDistributionClient` | SOAP 1.2/mTLS com o serviço `NFeDistribuicaoDFe`, interpretar `retDistDFeInt` e descompactar `docZip` |
| `NfeIndividualXmlStore` | Validar pasta, salvar XML completo com verificação de duplicidade/integridade |
| `NfeXmlArchive` | Preservar internamente resposta do lote para diagnóstico em caso de erro, **sem fluxo de restauração por ZIP** |
| `CaptureScheduleSettings` | Persistir configuração por empresa/tipo em arquivos locais |
| `HanaSetup` / `CompanyCertificateVault` | Conexão HANA e certificados A1 protegidos |

A configuração atual do HANA utiliza o schema `BRAI_NEX` com as tabelas `BRAI_NEX_SchemaVersion`, `BRAI_NEX_Company`, `BRAI_NEX_CaptureCursor`, `BRAI_NEX_Envelope` e `BRAI_NEX_CaptureLog`. O HANA é o repositório de estado e envelopes da captura; o diretório configurado é o destino de XMLs individuais completos. A integração fiscal de documentos no SAP Business One não foi implementada nesta entrega.

## Fluxo da operação automática

`Operador configura X minutos + pasta + habilita → POST /api/capture/schedules/{companyId} → NfeMonitoringWorker → NfeFirstDownload → SEFAZ distNSU → XML completo + envelope HANA + checkpoint.`

O monitoramento verifica periodicamente o estado local, e **respeita** tanto o intervalo do usuário quanto o horário de consulta permitido. A frequência do loop do Worker não equivale à frequência de chamadas SEFAZ.

## Fluxo do botão "Consultar tudo disponível"

`Operador clica → POST /api/sefaz/nfe/catchup → NfeSefazCatchup → NfeFirstDownload → SEFAZ distNSU → atualização do progresso por GET.`

- O job usa o checkpoint persistido no HANA. O usuário não fornece NSU.
- O backend interrompe ou pausa ao atingir o fim disponível, receber `137`/`656`, esbarrar no cooldown, perder persistência ou falhar.
- O job é guardado em memória no processo (`ConcurrentDictionary`). Recarregar a página permite recuperar o progresso **enquanto o backend mantém o estado**; reiniciar o servidor não preserva o job em execução.
- O manual e o automático compartilham destino, certificado e checkpoint, mas a opção `enabled` do agendamento controla **apenas o Worker**.
- O botão de NF-e permanece clicável em cooldown: a interface informa a próxima janela sem iniciar consulta antecipada; o backend também respeita o bloqueio.
- As respostas ``cStat`` devem distinguir documentos novos, arquivos já existentes, resumos/eventos e erros de persistência.

## Rotas usadas pelo produto atual

| Método | Caminho | Uso |
|---|---|---|
| GET | `/api/health` | Estado da configuração HANA (não é teste de tabelas) |
| POST | `/api/setup/hana/test` | Testar conexão HANA com credenciais explícitas |
| POST | `/api/setup/hana/provision` | Preparar/verificar schema HANA com credenciais explícitas |
| POST | `/api/setup/hana/save` | Validar e salvar conexão HANA protegida |
| POST | `/api/certificates/inspect` | Inspecionar A1 fornecido por formulário multipart |
| POST | `/api/companies/register` | Registrar empresa e A1 protegido |
| GET | `/api/capture/status` | Empresas, A1, contagem HANA, última tentativa e janela fiscal |
| GET | `/api/capture/schedules/{companyId}` | Ler configuração de captura |
| POST | `/api/capture/schedules/{companyId}` | Salvar configuração, inclusive intervalo e diretório NF-e |
| POST | `/api/sefaz/nfe/catchup` | Iniciar consulta manual de tudo disponível (HTTP 202) |
| GET | `/api/sefaz/nfe/catchup/{companyId}` | Consultar progresso do job; 404 se não existe estado |
| POST | `/api/capture/test-connectivity` | Diagnóstico HTTPS com A1; não executa distribuição fiscal |

### CT-e manual

A aba CT-e usa `GET /api/cte/status/{companyId}`, `POST /api/cte/download` com `{"companyId":"<GUID>","previousConsumerStopped":true}` e o registro `CTE` no endpoint compartilhado de agendamentos. Não há Worker automático CT-e. O operador confirma que não há outro consumidor CT-e em execução. Checkpoint CT-e é independente do HANA e do cursor de NF-e.

### Formatos essenciais

`POST /api/sefaz/nfe/catchup` usa JSON:

```json
{"companyId":"<GUID da empresa>"}
```

`POST /api/setup/hana/test`, `/save` e `/provision` exigem:

```json
{"server":"hana.exemplo.local","port":30015,"username":"usuario","password":"senha"}
```

A inspeção/cadastro A1 usa `multipart/form-data` com campos `certificate` (arquivo `.pfx`/`.p12`) e `password`.

`POST /api/capture/schedules/{companyId}` continua exigindo uma **lista de três objetos** (`NFE`, `CTE`, `NFSE`), cada qual com `documentType`, `enabled`, `intervalMinutes` **string numérica positiva** e `destinationDirectory`. O frontend atual exibe apenas configuração NF-e e mantém os dois registros auxiliares para compatibilidade com o contrato existente. Isso **não** significa que CT-e/NFS-e estejam ativos no Main.

## Rotas técnicas/legadas — não são funções do Main

| Método e caminho | Situação |
|---|---|
| `POST /api/capture/download` | Consulta unitária técnica existente; não exibida ao usuário |
| `POST /api/capture/test-once` | Teste controlado legado; não usar como terceira função |
| `POST /api/capture/prepare-handoff` | Rotina administrativa legada de checkpoint; **não é pré-requisito da navegação normal** |
| `GET /api/capture/archive/{companyId}/{archiveId}` | Exportação técnica do lote já recebido; não restaura ZIP nem consulta SEFAZ |
| `GET /api/cte/status/{companyId}` e `POST /api/cte/download` | CT-e manual na aba CT-e do Main, com cursor e janela fiscal próprios |

**Ausentes da API de QA:** `POST /api/recovery/nfe/sefaz/{companyId}`, `GET /api/recovery/nfe/preview/{companyId}`, `POST /api/recovery/nfe/restore`. Não recriar esses endpoints sem nova definição de produto.

## Integridade e segurança

- O servidor usa `http://127.0.0.1:5187` por padrão e rejeita IPs não loopback. **Não há autenticação multiusuário/AD/LDAPS** implementada; mudar o endereço de escuta não torna a aplicação segura para a rede.
- O A1 e a conexão HANA são protegidos no servidor usando ASP.NET Core Data Protection; no Windows a chave pode usar DPAPI vinculado ao contexto de execução.
- Preservar `.nexfiscal-keys/`, `.nexfiscal-certificates/` e `.nexfiscal-hana.dat` ao atualizar. Não publicar senhas, PFX nem XMLs em Git.
- `NfeXmlArchive` pode salvar um ZIP **interno** antes de gravar os XMLs individuais para reduzir perda de dados. Não há interface ou rotina de restauração pelo operador.
- `procNFe` completo pode ser salvo como XML individual; `resNFe` e eventos não devem ser contados como notas fiscais completas.
- A mesma reserva/checkpoint e bloqueio de concorrência fiscal precisam ser respeitados por captura manual e automática. Não forçar uma consulta durante cooldown nem reinicializar o checkpoint.

## Validação

Testes automatizados: `dotnet test tests/NexFiscal.Capture.Tests/NexFiscal.Capture.Tests.csproj -c Release` e `cd frontend && npm test && npm run build`. A CI compila também o projeto ASP.NET. Isso verifica código e contratos simulados; **não substitui homologação real com SEFAZ/HANA**.
