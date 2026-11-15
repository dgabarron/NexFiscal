# 06 — Histórico, referências, comandos e entrega ao novo agente

## Código-fonte
**GitHub:** https://github.com/dgabarron/NexFiscal

### Branch principal para Claude Code
`v1/etapa-01-hana` — snapshot revisado em 14/11/2026, commit `002b45813ef424db776cd1c07a477143973e94c5`.
ZIP: https://github.com/dgabarron/NexFiscal/archive/refs/heads/v1/etapa-01-hana.zip
PR #7: https://github.com/dgabarron/NexFiscal/pull/7

A branch tem a etapa 1 **implementada isoladamente** e passou por checks de compilação, testes frontend e smoke de servidor em Linux e Windows em execução anterior. Ainda falta o teste real pelo usuário com SAP HANA e reinicialização. O Claude deve consultar CI atual e executar novamente os testes; não pressupor que o status permaneça igual.

### Somente para consultar lógica anterior
`qa/refactor-estabilizacao-20261113` — snapshot `3d9980fc2d2af3f59f1d2fde980137254bf6ee01`.
PR #6: https://github.com/dgabarron/NexFiscal/pull/6

A QA anterior contém código de NF-e, CT-e, A1, UI operacional, recuperação sequencial e outras funcionalidades **que não devem ser importadas em bloco**. Reutilizar módulos pontuais somente após revisão de segurança, testes e aprovação da etapa correspondente.

### Main
A `main` no momento da reconstrução continha essencialmente `README.md`. Não fazer merge automaticamente.

## Stack
ASP.NET Core .NET 8, React 18, Vite 5, SAP HANA, certificado A1 Windows, protocolo NF-e Distribuição DF-e. Backend local HTTP loopback `127.0.0.1:5187`; o frontend Vite compila para `src/NexFiscal.Web/wwwroot`.

## Instalação Windows original
Diretório histórico: `C:\Brainstorm Solucoes Tecnologicas\_Polyexcel\19 - NexFiscal`.
Não sobrescrever essa pasta com o ZIP da V1 sem backup. Para a V1, usar **pasta de checkout nova** e o script `scripts\Run-NexFiscalLocal.ps1`.

## Contratos atuais da etapa 1
- `/api/health`
- `/api/setup/hana/status`
- `/api/setup/hana/test`
- `/api/setup/hana/save`

O método `HanaSetup.SaveAsync` deve criar tabelas ausentes e só persistir credenciais depois de validar o schema. Testar comportamento no HANA real antes de chamar etapa de "aprovada".

## Queixas e decisões já tomadas
1. Várias versões mudavam comportamento sem estabilizar; o usuário quer **entrega por etapa com autorização explícita**.
2. O usuário rejeitou tela de digitar múltiplas chaves de NF-e: **não incluir diagnóstico por chave no fluxo principal**.
3. Busca manual significa baixar **XMLs completos que faltam**, não apenas continuar a partir do último lote; inventariar documentos e lacunas conhecidas.
4. O usuário deseja "identificar todos os NSUs consultáveis"; traduzir isso como **todos os NSUs que podem ser descobertos e consultados legitimamente pelo serviço oficial e histórico local**, nunca brute force.
5. O usuário não quer botões de debug, diagnóstico extenso e painéis não aprovados.
6. Manual e automático são funções distintas na UI, porém compartilham motor de captura e checkpoint.
7. Nenhuma função CT-e/NFS-e agora. Elas são produto futuro, não bloqueio do escopo V1.

## Entregas que Claude Code deve produzir
- Código na branch específica da etapa.
- MD de decisões e testes atualizados.
- Comando único PowerShell para build e start.
- Captura de screenshot ou teste manual documentado da tela.
- CI real; relatório dos cenários aprovados/falhos, com limitações.
- Link de ZIP da branch, se GitHub disponível.
- **Parar após etapa implementada e testada; solicitar o OK do usuário.**
