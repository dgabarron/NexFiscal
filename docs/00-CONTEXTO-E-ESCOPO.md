# 00 — Contexto, decisões e escopo aprovado

## Problema e objetivo
O NexFiscal passou por várias versões de QA em que uma correção do frontend, cadastro ou captura introduzia comportamento diferente. Ocorreram confusões entre cadastro no HANA e presença do A1 no servidor, localização das chaves por `ContentRootPath`, funcionalidade de captura manual por NSU, funções secundárias e sucessão de ZIPs/branches. O usuário determinou **reconstruir o V1 por função, uma por vez**, podendo descartar implementação anterior fora do escopo.

O usuário quer recuperar **todos os arquivos XML de NF-e completos que estiverem efetivamente acessíveis para seu CNPJ e ainda não tenham sido baixados**, inclusive arquivos cujos documentos já sejam conhecidos no histórico local mas cujo XML completo esteja faltando. A aplicação não pode afirmar que recuperou XMLs que a SEFAZ não fornece, nem tratar resumos/eventos como XML completo.

## Quatro funções macro aprovadas
1. **Conexão SAP HANA**: login inicial com servidor/porta/usuário/senha; testar, validar/criar apenas objetos necessários, salvar protegidamente; ao reiniciar não solicitar login de novo se configuração válida. **Detalhamento aprovado; a implementação está em QA e depende de teste do usuário.**
2. **Cadastro de empresas e certificado A1**: upload PFX/P12 + senha, extração automática de razão social, CNPJ, UF quando presente, validade e emissor; múltiplas empresas; consulta/atualização/remoção segura; A1 criptografado e utilizável após reiniciar. **Especificação apresentada; ainda não foi expressamente homologada função a função pelo usuário.**
3. **Captura manual NF-e**: botão único para descobrir/distribuir e tentar recuperar XMLs completos acessíveis ainda ausentes. Percorrer `distNSU` legalmente, reconhecer faltas no inventário e usar `consNSU` apenas onde elegível; salvar XML individual; resultados e limitações claros. **Macro aprovada e refinamento de recuperação de ausentes confirmado pelo usuário.**
4. **Monitoramento automático NF-e**: habilitar/desabilitar, intervalo em minutos ≥1, destino; worker autônomo enquanto backend ativo; respeitar cotas e checkpoint; mesmo motor e mesma deduplicação da captura manual. **Macro aprovada; detalhamento fino ainda aberto.**

## Fora da V1
- CT-e e NFS-e (etapas futuras).
- Inserção manual de chaves de NF-e para diagnóstico/captura.
- ZIP de lote como entrega principal.
- Interface com NSUs expostos ao operador; análise fiscal por chave, múltiplos painéis, métricas supérfluas.
- Handoff de sistema anterior, migração automática de acervos sem plano aprovado.
- Recuperação "desde a abertura da empresa" como garantia, retroação irrestrita, varredura de todos os NSUs por tentativa.

## Etapas e bloqueios
| Etapa | Função | Estado | Gate obrigatório |
|---|---|---|---|
| 1 | HANA | Codificada isoladamente em `v1/etapa-01-hana`, CI anteriormente verificado; sem teste real do usuário | Acesso real, schema, reinício, conexão salva, erros e permissões |
| 2 | Empresas/A1 | Ainda não implementada na V1 isolada | Certificado real, CNPJ, proteção, reabertura e gestão segura |
| 3 | Manual NF-e | Ainda não implementada na V1 isolada | Simulações, ambiente SEFAZ autorizado, integridade, NSUs e faltantes |
| 4 | Auto NF-e | Ainda não implementada na V1 isolada | Intervalo, agendamento, restart, concorrência, regras SEFAZ |

**Não partir para a etapa seguinte sem aceite do usuário.**

## Repositório
https://github.com/dgabarron/NexFiscal

- Branch de implementação isolada: `v1/etapa-01-hana`.
- PR da etapa 1: https://github.com/dgabarron/NexFiscal/pull/7.
- Branch anterior completa, para consulta de algoritmos: `qa/refactor-estabilizacao-20261113`.
- PR anterior: https://github.com/dgabarron/NexFiscal/pull/6.
- Versões antigas não são releases homologadas.

## Histórico de falhas a evitar
- Ter cadastro no HANA **não significa** que há arquivo PFX acessível: separar status `missing`, `unreadable` e `stored`, sem assumir validade.
- Chaves Data Protection vinculadas a usuário Windows (DPAPI) e diretórios inconsistentes podem tornar a configuração ilegível. Raiz única de dados, backup e diagnóstico; nunca reescrever silenciando o erro.
- `dotnet build` pode ocorrer antes de `npm run build`: garantir que `wwwroot/index.html` seja servido explicitamente mesmo se o manifesto estático não foi gerado no build .NET.
- Jobs exclusivamente em RAM se perdem em restart; não afirmar que captura agendada continua automaticamente após fechar o backend.
- O status SEFAZ 137/656 e janela fiscal não devem disparar consultas repetidas em loop.
