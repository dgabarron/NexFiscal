# 09 — Etapa 03 (Captura manual de NF-e): norma, decisões, testes e homologação

Função 2 aprovada pelo usuário em 15/11/2026, com a ordem "NF-e primeiro, manual e depois automático". Função 3 implementada na branch `v1/etapa-03-captura-nfe`, criada a partir de `v1/etapa-02-empresas-a1`. NFS-e e monitoramento automático **não** fazem parte desta entrega.

## 1. Fonte normativa conferida (exigência do doc 05)

Lida na íntegra, direto do Portal Nacional da NF-e (`www.nfe.fazenda.gov.br`), em 15/11/2026:

- **Nota Técnica 2014.002 v1.40**, publicada em 03/07/2026 (18 páginas) — versão vigente.
- **Pacote de schemas "Distribuição de DF-e v1.04"** (`PL_NFeDistDFe_104`), publicado em 03/07/2026. Os XSDs estão em `tests/NexFiscal.Tests/Schemas/` e validam os pedidos nos testes.
- **Relação de Serviços Web**, Ambiente Nacional: `https://www1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx`.

O código antigo (branch de QA) foi lido apenas como referência; nada foi importado.

### O que a norma determina e como foi implementado

| Norma (item da NT) | Implementação |
|---|---|
| Pedido `distDFeInt` versão `1.01`; `cUFAutor` opcional; CNPJ passou a aceitar letras na v1.40 (3.1) | Pedido validado no schema oficial; UF enviada só quando o certificado a informa; CNPJ alfanumérico aceito |
| `distNSU` devolve lotes de até 50 documentos, em ordem crescente (3.5) | Laço que sempre usa o `ultNSU` devolvido, até `ultNSU = maxNSU` |
| Após `137` ou `ultNSU = maxNSU`, aguardar **1 hora** (3.5, 3.11.4.1) | Próxima consulta gravada no HANA (hora + 2 min de folga); antes disso nada é enviado à SEFAZ |
| `656 Consumo indevido`: CNPJ bloqueado por 1 hora; consultar antes zera a contagem (3.11.4) | Pedido fica em espera e é retomado sozinho após 1 hora; depois de 3 bloqueios seguidos, encerra com aviso |
| Consultar sempre em sequência; vários sistemas no mesmo CNPJ precisam seguir a mesma numeração (3.11.4.1) | Checkpoint único por empresa no HANA; aviso explícito sobre outro sistema |
| `consNSU` serve para NSU **identificado como faltante**; `137` significa que o NSU não existe (3.6) | Só entra na fila o NSU que falta dentro de uma sequência recebida; `137` encerra aquele NSU |
| `consNSU`/`consChNFe`: limite de 20 consultas por hora (3.11.4.2) | No máximo 10 por hora e 2 tentativas por NSU |
| `docZip` em GZip + Base64; atributo `NSU` opcional desde a v1.15 (3.2, 3.3) | Descompactação com limite de tamanho; documento sem NSU é aceito |
| Só o destinatário que se manifestou recebe a NF-e; antes, só o resumo (item 3 e nota 1 da tabela) | `resNFe` nunca conta como XML; fica listado como "aguardando manifestação" |
| Documentos disponíveis por até 90 dias; NSU só é gerado para quem usou o serviço nos últimos 60 dias, sem retroativo (3, 3.4) | Mensagem honesta no primeiro acesso; nenhuma promessa de histórico |

## 2. Três fatos da norma que afetam diretamente a sua expectativa

1. **Manifestação do destinatário.** Se a sua empresa é a destinatária das notas (compras), a SEFAZ entrega só o **resumo** até que haja manifestação (a partir da etapa 6 a Ciência da Operação pode ser registrada pelo próprio NexFiscal — ver `docs/12`) ("Ciência da Operação", "Confirmação" ou "Operação não Realizada"). Depois dela, o XML completo chega sozinho em uma captura seguinte. **O NexFiscal não envia manifestação** — o doc 05 proíbe incluir esse fluxo sem a sua decisão expressa. Sem manifestação feita em algum lugar (portal da SEFAZ, SAP ou outro sistema), a captura trará poucos ou nenhum XML completo de compra.
2. **Sem histórico retroativo.** Para um CNPJ que não consultou a distribuição nos últimos 60 dias, a numeração começa no primeiro acesso, que responde "nenhum documento". Documentos anteriores não são numerados depois. E nada com mais de 90 dias é entregue.
3. **Um único sistema por CNPJ.** Se outro programa (a versão antiga do NexFiscal, um add-on do SAP, um serviço de terceiros) também consulta a distribuição de NF-e do mesmo CNPJ, os dois se atrapalham e a SEFAZ bloqueia por 1 hora.

## 3. Decisões tomadas (revisáveis)

| # | Decisão | Motivo |
|---|---|---|
| 1 | **Sem manifestação automática** | Ato fiscal; exige a sua decisão (ver item 2.1). Posso implementar como etapa própria, se quiser |
| 2 | **`consChNFe` (consulta por chave) não é usado** | Para nota não manifestada ele não entrega o XML completo, e cada tentativa consome o limite de 20/hora, podendo bloquear o CNPJ. Após a manifestação o XML já chega pelo `distNSU` |
| 3 | Ordem de gravação: **arquivo na pasta → HANA (envelope, inventário, checkpoint) na mesma transação** | Se cair no meio, o NSU não avançou e a nova consulta traz o mesmo lote; arquivo igual não é regravado. Nunca há NSU avançado com XML perdido |
| 4 | **XMLs completos que já estão no HANA** (inclusive de versões antigas, que só entregavam ZIP) viram arquivos individuais na pasta, sem consultar a SEFAZ | Era a principal queixa histórica; não depende da SEFAZ |
| 5 | Arquivo faltando ou corrompido na pasta é **restaurado da cópia íntegra do HANA**; o arquivo ruim é renomeado `.invalido-<data>` | N06/N13 da matriz, sem gastar consulta |
| 6 | Salto entre um checkpoint antigo e o primeiro documento recebido **não** é tratado como lacuna | São documentos fora da janela de 90 dias; consultá-los seria varredura |
| 7 | O NSU devolvido em um `656` **não é adotado** automaticamente | Poderia pular documentos; a situação é informada |
| 8 | Pedido de captura **durável**: gravado no HANA e executado por um serviço em segundo plano; retoma após reinício e após a espera da SEFAZ | Item 10 da F3. Não é o monitoramento automático da Função 4: só existe pedido quando o operador clica |
| 9 | Falha passageira (SEFAZ fora do ar, sem internet, HANA indisponível): nova tentativa em 10 minutos, no máximo 3 | Sem martelar a SEFAZ |
| 10 | Ambiente fixo **Produção**; envelope SOAP 1.2 | SOAP 1.2 é o usado pelas bibliotecas de mercado; os exemplos da NT estão em SOAP 1.1 e o leitor aceita ambos. **A confirmar na SEFAZ real** |
| 11 | Lista de notas pendentes mostra emitente, data, valor e chave | Necessário para o operador manifestar; não há digitação de chave |
| 12 | A cada captura todos os arquivos conhecidos são conferidos por SHA-256 | Integridade exigida; pode levar alguns minutos com dezenas de milhares de notas |
| 13 | Migração 003, aditiva: `BRAI_NEX_NfeDocument`, `BRAI_NEX_NfeRecovery`, `BRAI_NEX_CaptureJob`, `BRAI_NEX_CaptureSettings` | Cursor, envelopes e log continuam nas tabelas originais, com `DocumentType = 'NFE'` como a versão antiga |

## 4. Testes executados (resultados reais)

| Verificação | Resultado |
|---|---|
| `dotnet build` | OK, 0 avisos |
| `dotnet test` — 166 testes (56 novos) | 166 aprovados, 3 execuções seguidas |
| `npm ci`, `npm run build`, `npm test` — 12 testes | OK |
| Pedidos `distNSU`/`consNSU` validados no **schema oficial** `PL_NFeDistDFe_104`; exemplos de retorno da NT lidos | OK |
| Autenticação com o A1 guardado contra um **servidor HTTPS local real** que exige certificado do cliente; servidor não confiável recusado; 403 e 500 tratados | OK no Linux; no Windows, ver GitHub Actions |
| Navegador (Chromium) contra o programa real com SEFAZ e HANA simulados: sem empresa, pasta inválida, captura com 2 XMLs e 2 resumos, lista de pendentes, novo clique dentro da hora com restauração de arquivo apagado, recarregar a página, janela estreita | OK, sem erros de console — imagens em `docs/evidencias-etapa-03/` |

### Matriz N01–N14

Todos os cenários têm teste automatizado com a SEFAZ simulada (`CaptureEngineTests`): N01 documentos novos, N02 já presente, N03 só resumo, N04 lacuna conhecida, N05 sem varredura, N06 arquivo corrompido, N07 pedido duplicado, N08 `137`, N09 `138` em vários lotes, N10 `656`, N11 falha entre arquivo e checkpoint (HANA e disco), N12 reinício com pedido pendente, N13 arquivo apagado, N14 nada disponível. **Nenhum foi executado contra a SEFAZ real.**

## 5. O que depende da sua homologação

Não tenho o seu A1, e não devo consultar a SEFAZ em seu nome. Por isso **nenhuma consulta real foi feita**. Pontos que só o seu ambiente confirma:

1. **A SEFAZ aceitar o pedido** (SOAP 1.2, certificado, CNPJ): é a primeira coisa a ver. Se vier "A SEFAZ rejeitou a consulta (código N)", me envie o código e o texto.
2. **Conexão segura no Windows** usando o A1 guardado, e a confiança do Windows na cadeia ICP-Brasil do servidor da SEFAZ.
3. **Comandos de captura no HANA real** (inclusive gravação do XML em coluna BLOB). Foram conferidos só estaticamente contra as tabelas.
4. O que a SEFAZ realmente entrega para o seu CNPJ: quantos XMLs completos e quantos resumos.
5. Continuação de um cursor deixado pela versão antiga, se existir para a sua empresa.

### Roteiro

| Passo | Ação | Esperado |
|---|---|---|
| 1 | **Feche qualquer outro sistema que consulte NF-e deste CNPJ**, inclusive o NexFiscal antigo | — |
| 2 | Baixe a branch `v1/etapa-03-captura-nfe` em pasta nova e rode o script | Abre em Conexão SAP HANA com "estrutura a revalidar" |
| 3 | **Atualizar estrutura** | "4 tabela(s) criada(s)"; faixa verde |
| 4 | Seção **Captura de NF-e** → escolha a empresa → pasta `C:\NexFiscal\XML` → **Capturar todos os XMLs disponíveis** | Em segundos: "Captura concluída" (ou "com pendências") e as contagens |
| 5 | Abra a pasta | Um arquivo `<chave>.xml` por NF-e completa contada em "XMLs completos novos" ou "Recuperados" |
| 6 | Clique em capturar de novo | "Aguardando a SEFAZ — continua sozinho em…"; nenhum arquivo duplicado |
| 7 | (Opcional) Apague um XML da pasta e clique em capturar | "Recuperados de pendência: 1"; o arquivo volta |
| 8 | (Opcional) Ctrl+C durante a espera, rode o script de novo e abra a seção | O pedido continua "Aguardando a SEFAZ" e é retomado no horário |

Se o passo 4 mostrar "A SEFAZ ainda não tem documentos numerados para este CNPJ", é o comportamento descrito no item 2.2: aguarde uma hora e capture de novo.

Evidência útil: print da tela após o passo 4. **Não envie XMLs, PFX, senha nem chaves de acesso reais.**

## 6. Critérios pendentes

- Primeira consulta real à SEFAZ e ao HANA (acima).
- **Decisão sua sobre manifestação do destinatário** (item 2.1): sem ela, compras não manifestadas ficam só como resumo.
- Função 4 (monitoramento automático) **não iniciada**; aguarda o seu "APROVADO" da etapa 3. NFS-e fica para depois, como combinado.
