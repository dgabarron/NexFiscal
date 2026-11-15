# 07 — Etapa 01 (SAP HANA): auditoria, decisões, testes e homologação

Registro da auditoria da Função 1 feita em 15/11/2026 sobre a branch `v1/etapa-01-hana` (base: commit `002b458`).

## 1. Estado encontrado

Na base, `dotnet build`, `npm run build`, `npm test` (1 teste) e o smoke HTTP passavam. Não havia nenhum teste automatizado do backend. O driver `Sap.Data.Hana.Net.v8.0` 2.30.27 traz as bibliotecas nativas para Windows x64 e Linux x64, portanto não é preciso instalar o SAP HANA Client separadamente.

## 2. Divergências entre a implementação e a especificação

| # | Requisito | O que havia | Correção |
|---|---|---|---|
| D1 | H11/H12 e doc 00: configuração ilegível nunca é sobrescrita | Arquivo ilegível era informado como `configured=false` e a tela o mostrava como "não configurado". Um novo **Salvar** apagava o arquivo cifrado anterior (`File.Move(overwrite)`) sem aviso | Novo estado `unreadable`. Salvar é recusado (409) antes de tocar no HANA. Só substitui com confirmação explícita na tela, e o arquivo antigo é preservado como `.nexfiscal-hana.dat.ilegivel-<data>.bak` |
| D2 | Doc 01 F1 passo 2: quatro estados distintos | API só tinha booleanos; "ilegível" e "não configurado" eram indistinguíveis | Campo `state`: `not_configured`, `ready`, `schema_pending`, `unavailable`, `unreadable`. Campos antigos mantidos |
| D3 | Doc 03 Sprint A: mensagens específicas (recusa/credenciais, schema, permissão, criptografia) | Uma única mensagem genérica para qualquer falha de teste e outra para qualquer falha de salvar | Classificação pelo código numérico do HANA: host não encontrado, conexão recusada, tempo esgotado, usuário/senha, senha a trocar, usuário bloqueado, privilégio insuficiente, schema/tabela sem acesso, TLS exigido. O texto bruto do driver não é exibido nem gravado em log |
| D4 | H07: tabela incompatível bloqueia sem destruir, com diagnóstico | Só verificava nomes de colunas; a mensagem não dizia o que faltava; com tabela incompatível ainda **criava** as demais tabelas antes de bloquear; se a tabela de versão fosse a incompatível, estourava erro genérico | Verifica colunas **e tipos**; lista cada item (`Coluna ausente: X.Y`, `Tipo incompatível: ...`); com incompatibilidade não executa nenhum DDL |
| D5 | Regra 8 do CLAUDE.md: senha nunca em log | `HanaSetupRequest` era um `record` cujo `ToString()` automático imprime a senha (risco latente) | `ToString()` sobrescrito; teste garante |
| D6 | H13: somente loopback | `UseUrls` podia ser trocado por configuração; sem verificação do cabeçalho `Host` | Kestrel fixo em `127.0.0.1:5187` (verificado: `--urls` e `ASPNETCORE_URLS` são ignorados); requisição com `Host` externo recebe 403 (proteção contra DNS rebinding) |
| D7 | "Erros claros" | Porta 5187 ocupada (ex.: versão antiga aberta) derrubava o processo com stack trace | Mensagem em português e saída com código 1 |
| D8 | Doc 03: testes unitários/integrados | Nenhum teste de backend | 56 testes com HANA simulado + 6 de frontend, executados no CI |
| D9 | Validação de entrada | Qualquer `ArgumentException` do driver virava "informe dados válidos"; servidor com espaço nas pontas era rejeitado | Validação própria com mensagem por campo |
| D10 | Build reproduzível | `package-lock.json` não versionado; script usava `npm install` | Lockfile versionado; script e CI usam `npm ci` |
| D11 | Documentação na branch | `CLAUDE.md` e docs estavam só na `main`, na raiz, enquanto o `CLAUDE.md` os referencia em `docs/` | `CLAUDE.md` na raiz e docs 00–06 em `docs/` (conteúdo inalterado). O `README.md` da `main` não foi trazido: cita a branch `feat/nfe-capture-core`, que não é a da V1 |

Itens conferidos e mantidos: escrita atômica (agora também com flush em disco), raiz de dados em `%LOCALAPPDATA%\NexFiscal\V1`, DPAPI no Windows, rota explícita de `/`, formato do arquivo cifrado (compatível com o anterior).

## 3. Decisões tomadas (revisáveis)

1. **Substituição de configuração ilegível com confirmação.** Sem isso o operador ficaria preso se as chaves fossem realmente perdidas. Nada é apagado; o arquivo antigo vira backup.
2. **Status devolve servidor, porta e usuário salvos** (nunca a senha) e a tela os recoloca no formulário, para atender "a área de configuração deve continuar acessível". Para alterar, é preciso redigitar a senha e testar.
3. **Verificação de tipo de coluna** usa `DATA_TYPE_NAME` do catálogo (sem tamanho/precisão). Colunas extras em tabelas existentes são toleradas.
4. **Salvar exige "Testar conexão" antes** (comportamento já existente, mantido).
5. **Sem TLS para o HANA nesta etapa.** Se o servidor exigir conexão criptografada, a tela informa isso claramente.
6. No Linux as chaves de proteção não são cifradas pelo sistema (não há DPAPI); o alvo de produção é Windows.

## 4. Testes executados nesta entrega (resultados reais)

| Verificação | Resultado |
|---|---|
| `dotnet build NexFiscal.sln -c Release` | OK, 0 avisos |
| `dotnet test` — 56 testes (HANA simulado + API HTTP em memória) | 56 aprovados |
| `npm ci`, `npm run build`, `npm test` — 6 testes | OK |
| Inicialização real do processo a partir de outra pasta: `/`, JS, CSS, `/api/health`, `/api/setup/hana/status` | 200 |
| Driver SAP HANA real contra host inexistente e porta fechada | Mensagens "Servidor não encontrado" e "Conexão recusada em host:porta"; nada salvo; senha ausente do log |
| Escuta somente em loopback; `--urls`/`ASPNETCORE_URLS` ignorados; `Host` externo → 403 | OK |
| Segunda instância na mesma porta | Mensagem clara, código de saída 1 |
| Tela no navegador (Chromium): inicial, erro real de conexão, estrutura pendente, salvo, indisponível + incompatível, ilegível em largura de celular | OK, sem erros de console — imagens em `docs/evidencias-etapa-01/` |

Nas imagens 03 a 06 as respostas do HANA foram simuladas no navegador; 01 e 02 usam o backend e o driver reais.

### Cobertura da matriz H01–H14

| ID | Situação |
|---|---|
| H01 | Verificado (processo real + navegador) |
| H02 | Verificado com driver real (sem HANA) e por testes |
| H03–H09, H11 | Verificados **somente com HANA simulado** → dependem de homologação |
| H10 | Verificado (processo iniciado de outra pasta usa o mesmo frontend e a mesma raiz de dados) |
| H12 | Lógica coberta pelo teste de chaves perdidas; DPAPI com outra conta Windows **não foi testado** |
| H13 | Verificado |
| H14 | Ver resultado do GitHub Actions do commit entregue |

## 5. O que depende da sua homologação (HANA real, Windows)

Não tive acesso a um SAP HANA real nem a Windows. O HANA simulado reproduz o que eu **suponho** que o HANA faz; os pontos abaixo só o seu ambiente confirma:

1. Login real com servidor/porta/usuário/senha (inclusive senha com caracteres especiais).
2. Os códigos de erro reais para senha errada (esperado: 10) e falta de privilégio (esperado: 258) produzirem as mensagens específicas.
3. O `CREATE SCHEMA`/`CREATE COLUMN TABLE` da migração 001 executar no seu HANA e a leitura de `SYS.TABLE_COLUMNS` devolver os tipos esperados (`NVARCHAR`, `INTEGER`, `TINYINT`, `BIGINT`, `DECIMAL`, `TIMESTAMP`, `BLOB`). Se aparecer "Tipo incompatível" logo após a criação, me envie a lista exibida.
4. Se já existir um schema `BRAI_NEX` da versão antiga: ser reconhecido sem alteração de dados.
5. DPAPI: reabrir com a mesma conta Windows reconhece a conexão; com outra conta mostra "Configuração salva ilegível" sem apagar nada.
6. O script PowerShell no seu Windows (não pude executá-lo aqui).

### Roteiro

| Passo | Ação | Esperado |
|---|---|---|
| 1 | Feche o NexFiscal antigo. Em pasta nova, rode o script | Testes passam; tela abre com "Configuração pendente" |
| 2 | Host/porta errados → Testar | "Servidor não encontrado" ou "Conexão recusada…" |
| 3 | Senha errada → Testar (uma vez só) | "Usuário ou senha do SAP HANA incorretos" |
| 4 | Dados corretos → Testar | "Conexão válida; estrutura pendente…" (ou "validada" se o schema já existe). Confira no HANA que nada foi criado |
| 5 | Salvar | "Conexão salva. N tabela(s) criada(s)…". Confira as 5 tabelas em `BRAI_NEX` |
| 6 | Testar + Salvar de novo | "A estrutura já existia e foi validada sem nenhuma alteração" |
| 7 | Ctrl+C, rode o script de novo | Abre direto com "Conexão salva e estrutura validada", sem pedir login |
| 8 | (Opcional) Pare o HANA ou bloqueie a rede e atualize o status | "Conexão salva, SAP HANA indisponível"; ao voltar, fica verde |
| 9 | (Opcional) Execute com outra conta Windows apontando `NEXFISCAL_DATA_DIR` para a mesma pasta | "Configuração salva ilegível"; arquivo intacto |

Evidência útil: print da tela nos passos 4, 5 e 7 (sem senha).

## 6. Critérios pendentes

- Homologação H03–H09, H11, H12 em HANA real/Windows (acima).
- Conexão HANA com TLS: fora desta entrega; só será tratada se o seu servidor exigir.
- Função 2 **não iniciada**; aguarda o seu "APROVADO" da etapa 1.
