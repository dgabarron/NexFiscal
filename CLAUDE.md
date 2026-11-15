# CLAUDE.md — NexFiscal V1

Leia obrigatoriamente `docs/00-CONTEXTO-E-ESCOPO.md`, `docs/01-ESPECIFICACAO-FUNCIONAL.md`, `docs/02-ARQUITETURA-E-DADOS.md`, `docs/03-IMPLEMENTACAO-PASSO-A-PASSO.md`, `docs/04-TESTES-E-CRITERIOS-DE-ACEITE.md` e `docs/05-SEFAZ-NSU-E-RECUPERACAO.md` antes de alterar o projeto.

## Missão
Construir o **NexFiscal V1**, aplicação local de captura de XML completo de NF-e destinado ou disponibilizado ao CNPJ da empresa, com configuração SAP HANA, certificado A1, captura manual de XMLs faltantes e monitoramento automático. O cliente quer uma única versão estável, validada etapa por etapa, não a acumulação de recursos anteriores.

## Regras inegociáveis
1. **Escopo aprovado: quatro funções macro**: (1) configuração SAP HANA; (2) empresa e certificado A1; (3) captura manual de NF-e completas ainda ausentes; (4) monitoramento automático de NF-e. Não construir CT-e, NFS-e, diagnósticos por chave, ZIP como resultado principal, dashboards supérfluos, ou handoff de sistemas antigos na V1.
2. **Execução por etapas:** terminar, testar e entregar a etapa 1 para aprovação do usuário antes de tocar na etapa 2. Repetir para 2 → 3 → 4. No momento, **etapa 1 foi codificada na branch `v1/etapa-01-hana`, mas ainda não foi homologada pelo usuário em SAP HANA real**. Não alegar aceite funcional dessa etapa.
3. **Não confundir teste CI com homologação**. Compilação .NET, Vite e smoke não validam HANA real, certificado A1, disponibilidade da SEFAZ e arquivos fiscais.
4. **Não perder dados**: nunca sobrescrever/excluir pastas privadas `.nexfiscal-*`, XMLs, tabelas HANA ou checkpoints; fazer migrações aditivas, com backup e sem `DROP`/`TRUNCATE`; não zerar NSUs.
5. **Acesso SEFAZ é restrito pelo serviço oficial**: implementar `distNSU` para avanço sequencial e `consNSU` apenas para NSUs conhecidos/relevantes, com cotas e intervalos; não prometer listagem universal de NSUs, consulta de todos os NSUs possíveis ou recuperação histórica ilimitada. Nunca executar força-bruta de NSUs.
6. Apenas XML **completo `procNFe`**, persistido como `<chave-44-digitos>.xml`. `resNFe`, eventos e outros `docZip` não contam como XML completo capturado. Identificar XML completo ausente mesmo se já houve envelope ou consulta anterior. Não duplicar XML presente; verificar integridade do existente.
7. O frontend deve ser simples, em PT-BR, com mensagens úteis e botões reais. O usuário não deve digitar chaves nem NSUs para a captura manual.
8. As credenciais HANA e A1 jamais aparecerão nos logs, resposta de API ou Git; usar proteção persistente, conta Windows estável e armazenamento em raiz determinística.
9. Aplicação atual é **local `127.0.0.1:5187`**, não expor em rede sem autenticação, TLS e planejamento explícito.
10. Nunca alterar `main` ou mesclar PRs sem pedido explícito; trabalhar em branches específicas e produzir mudanças pequenas, reversíveis e testadas.

## Repositório e branch de referência
- GitHub: https://github.com/dgabarron/NexFiscal
- V1 etapa 1 (base de trabalho): `v1/etapa-01-hana` — commit `002b45813ef424db776cd1c07a477143973e94c5`, PR #7: https://github.com/dgabarron/NexFiscal/pull/7
- Histórico de QA (para consulta, NÃO incorporar indiscriminadamente): `qa/refactor-estabilizacao-20261113`, PR #6: https://github.com/dgabarron/NexFiscal/pull/6
- `main` no momento da documentação tinha apenas `README.md`.
- Diretório histórico no Windows: `C:\Brainstorm Solucoes Tecnologicas\_Polyexcel\19 - NexFiscal`.

## Método de trabalho do Claude Code
1. Inspecionar arquivos reais da branch e verificar que testes existentes passam; não assumir que estes documentos substituem leitura do código.
2. Comparar estado atual com critérios da etapa correspondente.
3. Corrigir defeitos da etapa 1 e demonstrar conexão, provisionamento, persistência pós-reinício e mensagens claras, sem adicionar outras telas.
4. Entregar resumo de arquivos alterados, comandos, testes aprovados, bloqueios e evidências; aguardar aceite real para prosseguir.
5. Implementar etapas posteriores somente após autorização.
6. Manter registro de decisões e qualquer desvio de especificação antes de implementar.

## Comandos (raiz do repositório, Windows)
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Run-NexFiscalLocal.ps1
```
Ou:
```powershell
cd .\frontend
npm install --no-audit --no-fund
npm run build
cd ..
dotnet run --project .\src\NexFiscal.Web\NexFiscal.Web.csproj
```
Acessar http://127.0.0.1:5187.

A etapa 1 usa `%LOCALAPPDATA%\NexFiscal\V1` como raiz de estado privado por padrão, com override absoluto via `NEXFISCAL_DATA_DIR`. **Não apontar para a instalação legada sem validar cópias e chaves.**
