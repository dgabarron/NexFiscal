# NexFiscal V1 — Etapa 03: Captura manual de NF-e

**Escopo desta branch: Funções 1 e 2 (homologadas) e Função 3 — captura manual de todos os XMLs completos de NF-e disponíveis e ainda ausentes.** Não há monitoramento automático (Função 4), NFS-e, CT-e nem envio de manifestação.

**Estado da Função 3:** testada com SEFAZ simulada e HANA simulado, seguindo a Nota Técnica 2014.002 v1.40. **Ainda não executou contra a SEFAZ real nem contra o seu HANA** — veja limites e roteiro em [`docs/09-ETAPA-03-CAPTURA-NFE-DECISOES-E-HOMOLOGACAO.md`](docs/09-ETAPA-03-CAPTURA-NFE-DECISOES-E-HOMOLOGACAO.md). Etapas anteriores: [`docs/07`](docs/07-ETAPA-01-HANA-DECISOES-E-HOMOLOGACAO.md) e [`docs/08`](docs/08-ETAPA-02-EMPRESAS-A1-DECISOES-E-HOMOLOGACAO.md).

**Plataforma:** aplicação exclusiva para **Windows**, integrada exclusivamente ao banco **SAP HANA do SAP Business One**. A interface segue o **Microsoft Fluent 2** (Fluent UI React v9) e acompanha o tema claro/escuro do Windows.

## Como executar (Windows)

1. Baixe/extraia a branch `v1/etapa-03-captura-nfe` **em uma pasta nova**. Não misture com a instalação anterior.
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

### Vindo de uma etapa anterior

A conexão salva e as empresas cadastradas continuam valendo. Na primeira abertura a seção **Conexão SAP HANA** mostra "estrutura a revalidar": clique em **Atualizar estrutura**. Isso cria somente as tabelas novas desta versão, sem redigitar senha e sem alterar dados.

### Empresas (aba Empresas)

1. **Adicionar empresa** → escolha o arquivo `.pfx`/`.p12` e informe a senha → **Validar certificado**.
2. Confira a prévia (CNPJ, razão social, UF, emissor, validade) → **Cadastrar empresa**. Nada é digitado à mão.
3. Na lista, cada empresa mostra o estado real do A1: guardado e válido, perto de vencer, vencido, não encontrado, ilegível ou inválido.
4. **Atualizar A1** aceita apenas certificado do mesmo CNPJ; o arquivo anterior vira backup e cadastro/NSUs/histórico não mudam.
5. **Remover empresa**: sem histórico fiscal, exclui cadastro e A1 após confirmação; com histórico, apenas arquiva (pode ser reativada com **Reativar com A1**).
6. **Selecionar** marca a empresa que será usada na captura (etapa 3).

### Captura de NF-e (seção Captura de NF-e)

1. Escolha a empresa e informe a **pasta de destino** (um caminho neste computador, por exemplo `C:\NexFiscal\XML`).
2. Clique em **Capturar todos os XMLs disponíveis**. Nenhuma chave ou número é digitado.
3. O NexFiscal confere os arquivos da pasta, consulta a SEFAZ a partir do ponto em que parou e grava cada NF-e completa como `<chave de acesso>.xml`.
4. A tela mostra: XMLs novos, já presentes, recuperados, resumos e eventos ignorados, notas ainda sem XML completo e falhas.
5. A SEFAZ exige **1 hora** entre consultas depois que não há mais documentos. Um novo clique nesse intervalo apenas confere os arquivos locais e fica aguardando; a captura continua sozinha no horário indicado, enquanto o serviço estiver aberto.

Três limites do serviço oficial, que o NexFiscal não tem como contornar:

- **Destinatário só recebe o XML completo depois da manifestação** (Ciência da Operação ou Confirmação). Antes disso a SEFAZ entrega apenas o resumo. O NexFiscal lista essas notas, mas não envia manifestação.
- **Documentos ficam disponíveis por até 90 dias**, e a numeração só é gerada para quem usou a distribuição nos últimos 60 dias — não há histórico retroativo.
- **Outro sistema consultando a distribuição de NF-e do mesmo CNPJ** (inclusive a versão antiga do NexFiscal) faz a SEFAZ bloquear as consultas por 1 hora. Use apenas um.

Permissões do usuário HANA: na primeira instalação, `CREATE SCHEMA` (ou um schema `BRAI_NEX` já criado e pertencente a esse usuário). Depois disso, basta acesso ao schema `BRAI_NEX`.

## Preservação de dados

- A preparação da estrutura nunca executa `DROP`, `TRUNCATE` ou `ALTER`. Tabelas existentes não são tocadas; tabela incompatível bloqueia e é listada na tela.
- O único `DELETE` do sistema é o do cadastro de uma empresa **sem histórico fiscal**, após confirmação. Cursor, envelopes e logs de captura nunca são apagados.
- O ponto de captura (NSU) só avança, e só depois que os XMLs do lote estão gravados na pasta e no HANA. Nunca é zerado nem retrocedido; cursores deixados por versões anteriores são continuados.
- Na pasta de destino nada é apagado nem sobrescrito: arquivo com conteúdo divergente é renomeado para `.invalido-<data>` antes de o XML correto ser gravado.
- Dados privados: `%LOCALAPPDATA%\NexFiscal\V1` (não depende da pasta de execução). Contém `.nexfiscal-hana.dat` (credenciais cifradas), `.nexfiscal-certs\` (um A1 cifrado por empresa, com backups) e `.nexfiscal-keys\` (chaves protegidas por DPAPI do usuário Windows). **Mantenha tudo junto, faça backup da pasta inteira e use sempre a mesma conta Windows.**
- O PFX e a senha do A1 não são gravados no HANA, não voltam em nenhuma resposta e não vão para log. O certificado não é instalado no repositório de certificados do Windows.
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

| `POST` | `/api/setup/hana/upgrade` | Cria as tabelas novas desta versão usando a conexão salva |
| `GET` | `/api/companies` | Lista empresas com o estado real do A1 (`stored`, `missing`, `unreadable`, `expired`, `invalid`) |
| `POST` | `/api/companies/certificates/inspect` | `{pfxBase64,password}`; prévia do certificado, sem gravar |
| `POST` | `/api/companies` | Mesmo corpo; cadastra empresa e guarda o A1 cifrado |
| `PUT` | `/api/companies/{id}/certificate` | Troca o A1 (mesmo CNPJ) ou reativa empresa arquivada |
| `DELETE` | `/api/companies/{id}` | Remove (sem histórico) ou arquiva (com histórico) |

| `GET` | `/api/companies/{id}/nfe/capture` | Pasta lembrada, último pedido com contagens, próxima consulta permitida e quantidade de notas só com resumo |
| `POST` | `/api/companies/{id}/nfe/captures` | `{destinationDirectory}`; cria o pedido de captura (ou devolve o que já está em andamento) |
| `GET` | `/api/companies/{id}/nfe/pending` | Notas aguardando manifestação do destinatário |

Erros retornam `{error, kind, issues}` com mensagem em português.

## Desenvolvimento

```powershell
dotnet test .\NexFiscal.sln -c Release
cd frontend; npm test
```

O GitHub Actions compila e testa em Windows (plataforma do produto) e também em Linux, apenas como apoio ao desenvolvimento. Os testes do backend usam um SAP HANA simulado em memória e certificados de teste autoassinados com CNPJs fictícios (`tests/NexFiscal.Tests`). **Eles e o GitHub Actions não substituem a homologação em HANA real.**

Documentação do projeto: [`CLAUDE.md`](CLAUDE.md) e [`docs/`](docs/).
