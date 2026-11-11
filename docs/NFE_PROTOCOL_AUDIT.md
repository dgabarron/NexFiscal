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
