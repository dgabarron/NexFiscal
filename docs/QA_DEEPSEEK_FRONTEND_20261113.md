# QA — Redesign do frontend NexFiscal (DeepSeek + correções)

**Branch de validação:** `qa/deepseek-frontend-20261113`
**Base funcional:** `feat/nfe-capture-core` (commit `81f8a0d2b2723cb2b8c394a2dc2de04921f9e6db`).
**Origem do redesign:** XML enviado pelo usuário em 13/11/2026, com 43 arquivos de frontend.
**Escopo:** somente frontend e testes/workflow; serviços .NET e APIs fiscais não foram modificados.

## Correções incluídas
1. Componentização do frontend original (páginas, hooks e componentes React).
2. Reposição de captura NF-e manual de lote e diagnóstico de persistência.
3. Configuração CT-e novamente editável (habilitação, pasta e intervalo) sem prometer Worker CT-e.
4. Estado `busy` real em consulta CT-e, sincronização NF-e, retroação e restauração ZIP.
5. Evita restauração após mudança de data/empresa sem nova prévia.
6. Recuperação `consNSU` coordena o botão com sincronização `distNSU` em andamento. A exclusão definitiva de concorrência pertence ao backend.
7. Erros de `/api/capture/status` não se confundem com ausência de empresas.
8. Feedback do wizard HANA/A1 aparece na tela de instalação.
9. Retirada da chamada inválida `POST /api/setup/hana/provision` com corpo `{}`. Exibe estado via `GET /api/health` e oferece formulário técnico com credenciais explícitas.
10. Reidratação do status de jobs de sincronização ao entrar na tela, usando apenas GET.
11. Rotina de CI compilando backend e frontend e executando testes JavaScript do frontend.
12. Ícones compatíveis com a versão instalada do `lucide-react`.

## Comportamentos que devem permanecer separados
- **Monitoramento NF-e**: `NfeMonitoringWorker` agendado no servidor; utiliza cursor de distribuição e intervalos configurados.
- **Sincronização imediata**: `POST /api/sefaz/nfe/catchup`; percorre lotes com cursor sequencial.
- **Consulta de um lote NF-e**: `POST /api/capture/download`; uma consulta manual.
- **Recuperação retroativa**: `POST /api/recovery/nfe/sefaz/{companyId}`; consulta uma lacuna conhecida via consNSU, sem alterar LastNsu.
- **Restaurar ZIPs locais**: `GET /api/recovery/nfe/preview/{companyId}` e `POST /api/recovery/nfe/restore`; nunca consulta SEFAZ. Somente o filtro de ZIP tem limite atual de 90 dias.

## Checklist operacional de QA (ambiente controlado)
- [ ] Iniciar sem HANA salvo e concluir o wizard; testar senha incorreta e mensagem de erro.
- [ ] Validar certificado A1 e cadastrar empresa; não guardar senha no navegador.
- [ ] Interromper HANA e confirmar que a tela mostra erro de consulta, não cadastro vazio.
- [ ] Reabrir com HANA/empresa registrados e verificar central operacional por CNPJ.
- [ ] Configurar NF-e com pasta existente, habilitar e salvar; verificar intervalo/cooldown.
- [ ] Abrir sincronização de lotes e recarregar página; conferir GET de progresso.
- [ ] Testar consulta manual NF-e com status 137/138/656; não reconsultar em cooldown.
- [ ] Configurar CT-e e conferir que a consulta manual é habilitada só após salvar.
- [ ] Conferir que retroação não é permitida durante sincronização em execução.
- [ ] Alterar data e empresa na recuperação ZIP: a prévia precisa ser refeita.
- [ ] Usar credenciais HANA explícitas na área técnica; nunca chamar provision com corpo vazio.
- [ ] Testar layout responsivo, foco/teclado e alertas com dados reais de QA.
- [ ] Conferir que NFS-e permanece informada como não implementada.

## Validação automatizada
- `dotnet test tests/NexFiscal.Capture.Tests/NexFiscal.Capture.Tests.csproj -c Release`
- `dotnet build src/NexFiscal.Web/NexFiscal.Web.csproj -c Release`
- `cd frontend && npm install && npm run build && npm test`

## Não cobre esta entrega
- Não houve deploy, homologação da SEFAZ real, importação SAP B1 ou teste em HANA de produção.
- Monitoramento é do backend já existente; frontend não adiciona scheduler nem faz chamadas fiscais por timer.
- Autenticação em rede / AD / LDAPS ainda depende de backend e configuração de segurança; portal permanece limitado a loopback.
- Status de job `NfeSefazCatchup` é mantido em memória pelo backend. O frontend consegue recuperar somente o estado que o backend ainda conserva após o refresh; **não** torna jobs persistentes após reiniciar servidor.
- `consNSU` consulta lacunas identificadas entre NSUs conhecidos, não representa recuperação universal de documentos anteriores.
