# NexFiscal V1 — Etapa 01: Conexão SAP HANA

**Escopo desta branch: somente a Função 1 (configuração e validação do SAP HANA).** Não há cadastro de empresa/A1, captura manual ou automática, CT-e, NFS-e ou outras telas.

**Estado:** código auditado e testado com HANA simulado. **Ainda não homologado em SAP HANA real** — veja o roteiro em [`docs/07-ETAPA-01-HANA-DECISOES-E-HOMOLOGACAO.md`](docs/07-ETAPA-01-HANA-DECISOES-E-HOMOLOGACAO.md).

## Como executar (Windows)

1. Baixe/extraia a branch `v1/etapa-01-hana` **em uma pasta nova**. Não misture com a instalação anterior.
2. Instale o .NET SDK 8 e o Node.js 20+.
3. Feche qualquer versão antiga do NexFiscal (ela usa a mesma porta 5187).
4. Na raiz da pasta, execute:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Run-NexFiscalLocal.ps1
```

O script instala dependências, compila, roda os testes automatizados e inicia o serviço. Abra http://127.0.0.1:5187.

## Como usar

1. Informe servidor, porta SQL, usuário e senha do SAP HANA.
2. **Testar conexão** — só lê. Não cria tabelas e não salva nada. Informa se a estrutura está validada, pendente ou incompatível.
3. **Salvar configuração** — cria somente o schema/tabelas `BRAI_NEX` ausentes, valida colunas e tipos e só então grava as credenciais cifradas. Se algo falhar, nada é salvo.
4. Feche o serviço (Ctrl+C), execute o script de novo e confirme que a tela abre com **Conexão salva e estrutura validada**, sem pedir login.

Permissões do usuário HANA: na primeira instalação, `CREATE SCHEMA` (ou um schema `BRAI_NEX` já criado e pertencente a esse usuário). Depois disso, basta acesso ao schema `BRAI_NEX`.

## Preservação de dados

- O NexFiscal nunca executa `DROP`, `TRUNCATE`, `ALTER`, `DELETE` ou `UPDATE` nesta etapa. Tabelas existentes não são tocadas; tabela incompatível bloqueia o salvamento e é listada na tela.
- Dados privados: `%LOCALAPPDATA%\NexFiscal\V1` (não depende da pasta de execução). Contém `.nexfiscal-hana.dat` (credenciais cifradas) e `.nexfiscal-keys\` (chaves protegidas por DPAPI do usuário Windows). **Mantenha os dois juntos e use sempre a mesma conta Windows.**
- Se a configuração salva não puder ser descriptografada, a tela mostra **Configuração salva ilegível**. Ela só é substituída com confirmação explícita, e o arquivo antigo é preservado como `.nexfiscal-hana.dat.ilegivel-<data>.bak`.
- Para usar outra raiz, defina `NEXFISCAL_DATA_DIR` com caminho absoluto antes da primeira execução. Não aponte para a instalação legada.

O serviço escuta somente `127.0.0.1:5187` (não configurável) e recusa requisições de outras máquinas. Não há login multiusuário; não é solução para rede.

## Endpoints

| Verbo | Rota | Comportamento |
|---|---|---|
| `GET` | `/api/health` | Produto, versão e etapa |
| `GET` | `/api/setup/hana/status` | `state` (`not_configured`, `ready`, `schema_pending`, `unavailable`, `unreadable`), `configured`, `connected`, `schemaReady`, `tablesFound`, `message`, `server`, `port`, `username`, `issues`. Nunca retorna senha |
| `POST` | `/api/setup/hana/test` | `{server,port,username,password}`; somente leitura |
| `POST` | `/api/setup/hana/save` | Mesmo corpo (+ `replaceUnreadable` opcional); cria só o que falta, valida e salva cifrado |

Erros retornam `{error, kind, issues}` com mensagem em português.

## Desenvolvimento

```powershell
dotnet test .\NexFiscal.sln -c Release
cd frontend; npm test
```

Os testes do backend usam um SAP HANA simulado em memória (`tests/NexFiscal.Tests/FakeHana.cs`). **Eles e o GitHub Actions não substituem a homologação em HANA real.**

Documentação do projeto: [`CLAUDE.md`](CLAUDE.md) e [`docs/`](docs/).
