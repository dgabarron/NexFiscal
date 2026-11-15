# 11 — Etapa 05 (NFS-e nacional): norma, decisões, testes e homologação

Função 4 aprovada pelo usuário em 15/11/2026. Como combinado ("NF-e primeiro"), a etapa seguinte é a **NFS-e do padrão nacional**, implementada na branch `v1/etapa-05-nfse`, criada a partir de `v1/etapa-04-monitoramento`. Isso muda o escopo original, que deixava a NFS-e fora da V1; a mudança foi pedida pelo usuário e está registrada no `CLAUDE.md`.

## 1. Fonte normativa

### Conferido na fonte oficial (`www.gov.br/nfse`, em 15/11/2026)

| Documento | O que foi usado |
|---|---|
| **Manual dos Contribuintes — Guia de utilização das APIs do ADN**, v1.1 de 03/11/2026 | Método `GET /DFe/{NSU}`; parâmetro `cnpjConsulta` (14 dígitos, mesmo CNPJ base do certificado); perfis atendidos (emitente, tomador, intermediário); erros `E2210`–`E2247`, em especial `E2220` (HTTP 404, sem documentos a partir do NSU) |
| **Anexo IV — ADN** v1.00 | Regras de geração de NSU e de distribuição para os atores da NFS-e |
| **Esquemas XSD v1.01** (09/02/2026) | Estrutura do XML: `NFSe/infNFSe` com `Id = "NFS" + chave de 50 dígitos`, `emit`, `valores/vLiq`, `dhProc`; eventos `evento/infEvento` e os códigos de cancelamento |
| Página **APIs — Prod. Restrita e Produção** | Endereço de produção `https://adn.nfse.gov.br/contribuintes` |

### NÃO conferido na fonte oficial

O manual descreve os métodos, mas remete o **formato do corpo da resposta** ao Swagger do ADN. Esse Swagger exige certificado digital até para ser lido, e daqui não foi possível abri-lo. Por isso **não foram conferidos oficialmente**:

- os nomes dos campos JSON (`StatusProcessamento`, `LoteDFe`, `NSU`, `ChaveAcesso`, `TipoDocumento`, `ArquivoXml`);
- a compactação do `ArquivoXml` (GZip + Base64);
- o parâmetro `lote=true` e o limite de 50 documentos por resposta;
- a existência de limite de frequência (HTTP 429).

Esses pontos foram tirados de **implementações públicas independentes** que concordam entre si e com o manual. Para reduzir o risco:

- a leitura do JSON não depende de maiúsculas/minúsculas nem do tipo do NSU (número ou texto);
- cada documento é classificado **pelo próprio XML** (validado contra a estrutura do XSD oficial), não pelo rótulo do JSON;
- qualquer resposta fora do esperado vira o erro "O ADN da NFS-e devolveu uma resposta em formato inesperado (…)", sem gravar nada e sem avançar o ponto de captura.

**Como fechar essa lacuna:** abrindo `https://adn.nfse.gov.br/contribuintes/docs/index.html` no seu navegador (com o certificado da empresa) e me enviando o trecho que descreve a resposta do `GET /DFe/{NSU}` — ou simplesmente testando: se o formato for outro, a mensagem de erro dirá o que veio diferente.

## 2. Decisões tomadas (revisáveis)

| # | Decisão | Motivo |
|---|---|---|
| 1 | NFS-e **independente** da NF-e: checkpoint (`DocumentType = 'NFSE'`), inventário (`BRAI_NEX_NfseDocument`), pasta e monitoramento (`BRAI_NEX_NfseMonitor`) próprios | São serviços diferentes; um não pode atrasar nem confundir o outro. Nada das tabelas ou do código aprovado da NF-e foi alterado |
| 2 | **Mesma fila e mesmo serviço em segundo plano**; pedidos de NFS-e ficam na tabela de pedidos com tipo próprio | Pedido durável, retomada após reinício e exclusão mútua já homologados |
| 3 | O `cnpjConsulta` é **sempre** enviado, com o CNPJ da empresa | Obrigatório desde 03/11/2026 segundo o manual v1.1 |
| 4 | Depois que a fila esvazia, **espera de 1 hora** antes de nova consulta | O manual não fixa intervalo; adotei o mesmo da NF-e para não sobrecarregar o serviço. Ajustável se você quiser menos |
| 5 | HTTP 429: espera o tempo indicado pelo ADN (ou 1 hora), sem repetir; após 3 vezes, encerra o pedido | Mesmo tratamento do "consumo indevido" da NF-e |
| 6 | Todas as NFS-e distribuídas são gravadas, **emitidas e recebidas** | O ADN distribui ao contribuinte nos três papéis. Se você quiser só as recebidas (tomador), é um filtro simples de acrescentar |
| 7 | Evento de cancelamento marca a nota como cancelada no inventário; **o XML não é apagado** | Preservação de dados |
| 8 | Sem consulta por chave e sem recuperação de lacunas na NFS-e | O manual não descreve regra de lacuna para contribuintes |
| 9 | Documento cuja chave do JSON difere da chave do XML, ou que não é NFS-e nem evento, **não vira arquivo** e conta como falha | Integridade |
| 10 | Só o padrão nacional | NFS-e de município que não usa nem compartilha com o sistema nacional não existe no ADN |

## 3. Testes executados (resultados reais)

| Verificação | Resultado |
|---|---|
| `dotnet build` | OK, 0 avisos |
| `dotnet test` — 212 testes (27 novos) | 212 aprovados |
| `npm ci`, `npm run build`, `npm test` — 15 testes | OK |
| Transporte HTTPS da NFS-e contra servidor local que exige certificado do cliente (200, 404, 429 com espera, servidor não confiável) | OK |
| Navegador (Chromium) contra o programa real com SEFAZ, ADN e HANA simulados: NF-e inalterada; NFS-e com pasta e pedido próprios; 2 NFS-e gravadas e 1 evento ignorado; sem cartão de manifestação; monitoramento de NFS-e ligado com o da NF-e desligado | OK, sem erros de console — imagens em `docs/evidencias-etapa-05/` |

Os 185 testes das etapas 1 a 4 continuam passando sem alteração de comportamento.

## 4. O que depende da sua homologação

**Nenhuma consulta real ao ADN foi feita.** Em ordem de risco:

1. **Formato da resposta do ADN** (item 1). É o ponto com maior chance de ajuste.
2. Autenticação no ADN com o A1 no Windows.
3. Comandos da NFS-e no HANA real (conferidos só estaticamente).
4. Se o seu município (e os dos seus prestadores) tem notas no ADN.

### Roteiro

| Passo | Ação | Esperado |
|---|---|---|
| 1 | Baixe a branch `v1/etapa-05-nfse` em pasta nova, rode o script e clique em **Atualizar estrutura** | "2 tabela(s) criada(s)" |
| 2 | Seção **Captura** → aba **NFS-e** → pasta `C:\NexFiscal\NFSe` → **Capturar todos os XMLs disponíveis** | "Captura concluída" com a contagem de NFS-e novas |
| 3 | Abra a pasta | Um arquivo de 50 dígitos `.xml` por NFS-e |
| 4 | Aba **NF-e** da mesma seção | Estado e pasta da NF-e inalterados |
| 5 | Seção **Monitoramento** → aba **NFS-e** → ligar, intervalo, **Salvar configuração** | "Monitoramento ativo — próxima consulta prevista para…" |

Se o passo 2 mostrar "resposta em formato inesperado" ou "O ADN da NFS-e rejeitou a consulta (código)", me envie o texto exato. **Não envie XMLs, PFX nem senha.**

## 5. Critérios pendentes

- Homologação acima, em especial o formato da resposta.
- Manifestação do destinatário da NF-e: continua aguardando a sua decisão.
- Release (doc 03): validação de ponta a ponta, backup, plano de rollback e proposta de merge na `main` — não feitos; a `main` não foi tocada.
