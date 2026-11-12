# Auditoria do motor de distribuição NF-e — 11/11/2026

## Objetivo
Executar uma consulta real ao serviço `NFeDistribuicaoDFe` e armazenar os DF-e recebidos no SAP HANA sem perda de NSU. Não modificar a configuração HANA/A1 nem redesenhar a Central de operação.

## Referência técnica
- Portal NF-e confirma **Nota Técnica 2014.002 v1.40**, publicada em 03/07/2026: https://www.nfe.fazenda.gov.br/portal/informe.aspx?Informe=9ydmiExbDZM%3D
- Portal NF-e lista o pacote **Distribuição DF-e v1.04**, publicado em 03/07/2026, em `Documentos > Esquemas XML`: https://www.nfe.fazenda.gov.br/portal/listaConteudo.aspx?tipoConteudo=BMPFMBoln3w%3D
- O XML de entrada permanece identificado por `distDFeInt` e versão `1.01` nos XSDs que conseguimos inspecionar. **Não confundir a versão da nota técnica ou do pacote de distribuição com o atributo `versao` do XML.**
- Os arquivos de validação incorporados nesta mudança são `distDFeInt_v1.01.xsd` e `tiposDistDFe_v1.01.xsd`, copiados do repositório público `nfephp-org/sped-nfe` em `schemes/PL_009_V4`. **Eles NÃO foram extraídos diretamente do ZIP oficial v1.04** e exigem comparação antes da homologação definitiva.

## Auditoria do código

| Área | Situação |
| --- | --- |
| A1 mTLS, TLS 1.2, HTTP 200 WSDL | Confirmado no ambiente do usuário; NÃO equivale a autorização de distribuição |
| `distDFeInt` com `tpAmb`, CNPJ, `distNSU/ultNSU` e UF opcional IBGE | Validação XSD local anterior ao POST SOAP |
| `cUFAutor=91` | Bloqueado; o endereço/UF do certificado é usado quando válido, caso contrário o campo é omitido |
| Versão XML 1.01 e NSU de 15 dígitos | Cobertos pelos XSDs locais e testes automatizados |
| SOAP Fault | Rejeitado explicitamente, sem tentar tratá-lo como lote de documentos |
| `138` + `docZip` | Descompactação GZip, XML bem formado, hash SHA-256, rejeição de NSU repetido e lote acima de 50 |
| NSU HANA | Reserva conservadora antes da chamada; gravação de documentos e avanço do NSU na mesma transação |
| `137`, `656` | Intervalo de espera e registro; não repetir automaticamente |
| Outros códigos, incluindo `215` | Rejeição registrada em `BRAI_NEX_CaptureLog` com `cStat` e `xMotivo`, sem gravar XML ou avançar NSU |

## Validação e limitações
- Testes automatizados e build de .NET/React são executados no GitHub Actions. Um build bem-sucedido não substitui ensaio de integração com o SAP HANA e com o ambiente real SEFAZ.
- **Sem teste real de XML baixado nesta alteração**. O próximo ensaio deve ocorrer após o horário `NextAllowedAtUtc` registrado no HANA; não apagar nem retroceder o cursor para forçar nova consulta.
- A consulta com `ultNSU=000000000000000` é modo de inicialização, não garante histórico anterior à janela de documentos disponibilizada pela SEFAZ.
- O protocolo SOAP real (versão do envelope, action e binding) deve ser comparado com o WSDL retornado pelo ambiente de produção com o A1; o HTTP 200 sozinho não prova validação desse binding.
- A validação de resposta por schema oficial completo e a integração automatizada com HANA real ainda não estão demonstradas. Para operacionalizar em múltiplas instâncias, substituir o `SemaphoreSlim` local por lease distribuído durável, e formalizar retry pós-timeout.

## Próximo teste único
1. Atualizar a branch `feat/nfe-capture-core` mantendo o cofre de A1 e o arquivo cifrado de conexão HANA.
2. Executar testes/build .NET e build manual React (`npm run build`).
3. Aguardar a liberação registrada em `NextAllowedAtUtc`.
4. Executar **Consultar SEFAZ e salvar XMLs** uma única vez e capturar `cStat`, `xMotivo` e contagem de envelopes.
5. Resultado esperado: `138` com `docZip` persistido ou `137` sem documentos. Rejeições devem ser analisadas no registro do HANA antes de nova tentativa.

## Foco prioritário: XML para download — revisão complementar 11/11/2026

Fontes cruzadas:
- [SEFAZ / NT 2014.002 v1.40](https://www.nfe.fazenda.gov.br/portal/listaConteudo.aspx?tipoConteudo=04BIflQt1aY%3D) e [pacote oficial de schemas de Distribuição DF-e v1.04](https://www.nfe.fazenda.gov.br/portal/listaConteudo.aspx?tipoConteudo=BMPFMBoln3w%3D).
- [NFePHP SPED-NFe — DistDFe](https://github.com/nfephp-org/sped-nfe/blob/master/docs/metodos/DistDFe.md): exemplo SOAP 1.2, execução de distNSU, resposta por lotes, decodificação `docZip` GZip e intervalo de consulta.
- [NFePHP SPED-NFe — Download](https://github.com/nfephp-org/sped-nfe/blob/master/docs/metodos/Download.md): retorna XML descompactado e distingue resumo de documento completo.

Correções efetivas:
- **Defeito de navegador**: `const window=pendingWindow(company)` impedia `window.confirm()`. Agora usa `cooldown` e a confirmação funciona.
- **Entrega do arquivo independente da gravação no HANA**: ao receber um lote `cStat 138`, o servidor grava um ZIP com XMLs originais sob `.nexfiscal-downloads/<CompanyId>/<ArchiveId>.zip`, fora de `wwwroot`; o navegador recebe um link `/api/capture/archive/{companyId}/{archiveId}`. O arquivo pode ser baixado mesmo se a persistência de envelopes no HANA falhar.
- **Arquivo ZIP**: documentos são nomeados pelo NSU, com `manifest.json` informando `schema`, NSU e hash SHA-256; nenhum campo não confiável é usado como caminho do arquivo.
- **Testes offline**: o teste `NfeZipExporterTests` cria um lote 138, gera ZIP sem HANA e valida os bytes do XML, o nome do arquivo, os metadados e a rejeição a lotes incorretos.
- **Confidencialidade**: pasta `.nexfiscal-downloads` ignorada pelo Git, arquivo servido somente no portal local, sem exposição pública direta.

**Atenção**: `docZip` pode trazer `resNFe` (resumo), `procNFe` (documento completo) ou eventos; o download **não garante XML completo da NF-e** para o destinatário sem manifestação quando exigida. Confirmar o `schema` no `manifest.json`. O download aqui é o arquivo do documento fiscal recebido, não o PDF DANFE. A resposta `137` significa sem arquivos no lote; não gera ZIP.

O HANA ainda é usado no serviço atual para identificar a empresa e reservar o NSU (intervalo de segurança), mas **a entrega do ZIP não depende do sucesso da gravação de envelopes no HANA**. Não reiniciar ou retroceder o NSU por falha de persistência; reconciliar a tentativa antes de novo clique.

**Limites não afirmados como resolvidos**: não houve requisição real à SEFAZ ou validação do ZIP oficial v1.04 nesta revisão. CI valida montagem de XML, parsing e exportação ZIP offline. A autorização de distribuição do CNPJ e a disponibilidade de `procNFe` dependem da SEFAZ e da condição fiscal do documento.

## Fluxo positivo/negativo acordado — arquivos individuais na pasta X

**Fluxo positivo (arquivo por XML):**
1. Operador informa a pasta X existente no computador ou servidor onde o NexFiscal executa; exemplo `C:\\NexFiscal\\XML`.
2. O backend valida a pasta antes de reservar a consulta fiscal.
3. NexFiscal consulta `NFeDistribuicaoDFe` com certificado A1 e NSU controlado (não é acesso SQL direto ao banco da SEFAZ).
4. Com `cStat 138`, descompacta `docZip` e grava **cada XML individualmente** como `<NSU de 15 dígitos>.xml` na pasta X. Conteúdo e SHA-256 são preservados; já existente com mesmo hash é mantido, com conteúdo diferente é erro.
5. Antes da cópia individual, mantém um ZIP de recuperação interno para evitar perder os XMLs se houver uma falha de disco. A gravação de envelopes no HANA é secundária; apenas o controle de NSU/empresa ainda depende de HANA nesta etapa.
6. A interface informa quantidade recebida, arquivos novos, arquivos preexistentes e pasta utilizada.

**Fluxos negativos:**
- Pasta ausente/inválida: bloquear **antes** de acessar a SEFAZ; não consumir NSU.
- `137`: nenhum XML recebido; nenhum arquivo é criado, registrar status e respeitar intervalo.
- `656`: consumo indevido; não gravar arquivo, respeitar intervalo de segurança; sinalizar erro em vermelho.
- Outro `cStat`, como `215`: rejeição, capturar `xMotivo`, não gravar XML nem avançar NSU.
- Falha ao gravar na pasta X depois da resposta `138`: não avançar NSU, manter ZIP interno disponível, sinalizar erro em vermelho e exigir reconciliação antes de consultar novamente.
- Arquivo com NSU existente e conteúdo diferente: não sobrescrever; preservar recuperação.
- Falha ao persistir envelopes no HANA: os XMLs individuais continuam preservados na pasta X; mensagem amarela alerta que o checkpoint requer reconciliação.

**Observação:** arquivos `resNFe` são resumos XML, não NF-e processada completa. Esta etapa preserva exatamente o XML recebido e identifica o `schema` no ZIP interno. O NexFiscal só consegue gravar um documento completo `procNFe` quando este é efetivamente disponibilizado pela SEFAZ. A escolha da pasta não elimina o mecanismo de espera para evitar `656`.

## Requisito obrigatório: somente XML completo procNFe (11/11/2026)
- O arquivo operacional salvo na pasta X deve conter raiz `nfeProc` e elementos `NFe/infNFe` e `protNFe/infProt`. O atributo `schema` do `docZip` deve iniciar com `procNFe_v`.
- `resNFe`, `procEventoNFe` e outros retornos são ignorados na pasta X; contados como não completos, nunca como XML fiscal baixado com sucesso.
- O lote bruto permanece em ZIP de diagnóstico e no checkpoint fiscal para recuperação, mas não é entrega de `procNFe`.
- Para destinatário, a distribuição pode liberar apenas `resNFe` antes da manifestação. Para receber `procNFe` será necessário um evento de manifestação apropriado, conforme a NT 2014.002. **Não enviar manifestação automaticamente** sem aprovação do responsável fiscal.
- Se somente resumos vierem, o sistema deve informar `0 procNFe`, preservar o NSU e aguardar a condição de distribuição; repetir `distNSU` sem motivo pode gerar `656`.
