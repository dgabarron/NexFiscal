> **MÓDULO LEGADO FORA DO ESCOPO DO MAIN (13/11/2026).** Este documento conserva uma especificação/testes do CT-e que existem parcialmente no backend. A versão funcional atual de NexFiscal é **NF-e com apenas duas operações**; CT-e não é oferecido no Main, nem existe Worker CT-e desta entrega. Não tomar as instruções de CT-e abaixo como fluxo atual do usuário. Ver [escopo vigente](ESPECIFICACAO_FUNCIONAL_ATUAL.md) e [arquitetura](ARQUITETURA_E_APIS.md).

---

## Registro técnico anterior de CT-e (preservado)
# CT-e — mesmo fluxo de captura individual da NF-e

## Referência

- [Nota Técnica CT-e 2015/002 v1.00a — distribuição de DF-e](https://www.cte.fazenda.gov.br/portal/exibirArquivo.aspx?conteudo=5c1PwLTdrCA%3D).
- [Relação oficial de serviços CT-e — Ambiente Nacional](https://www.cte.fazenda.gov.br/portal/webServices.aspx?tipoConteudo=wpdBtfbTMrw%3D).
- [MOC CT-e 4.00 / documentos oficiais](https://dfe-portal.svrs.rs.gov.br/CTe/Documentos).
- [NFePHP SPED-CTE: implementação de referência sefazDistDFe](https://github.com/nfephp-org/sped-cte/blob/master/src/Tools.php).

## Fluxo positivo

1. A mesma empresa e certificado A1 cadastrados para NF-e podem ser usados para CT-e. O sistema lê o CNPJ e UF do A1 e valida a cadeia ICP-Brasil antes da chamada.
2. O operador escolhe uma única pasta X. Ela precisa existir **no computador/servidor que executa o NexFiscal**, com permissão de gravação.
3. A operação CT-e consulta `CTeDistribuicaoDFe`, método `cteDistDFeInteresse` e `distNSU/ultNSU`. **Não reutiliza o endpoint, namespace, NSU nem o cursor da NF-e.**
4. Se houver documentos (`cStat 138`), o sistema preserva o lote bruto para recuperação e só copia documentos completos com `schema=procCTe_v*.xsd`, raiz `cteProc`, elementos `CTe/infCte` e `protCTe/infProt`.
5. Os documentos completos são gravados na mesma pasta X como `<chave-de-acesso-de-44-digitos>.xml` (o NSU específico do CT-e). Arquivo com mesmo conteúdo é ignorado; conteúdo diferente nunca é sobrescrito.
6. Avança o NSU do CT-e somente após a cópia para a pasta X. O checkpoint está em `.nexfiscal-cte/<CompanyId>.json` e é independente do HANA. Os XMLs são armazenados individualmente no diretório escolhido, não nas tabelas SAP HANA.

## Fluxos negativos

- Pasta inexistente ou ausência do A1: não consulta SEFAZ.
- UF do certificado ausente: não inventa o cUFAutor, impede envio SOAP.
- `137`: sem documentos; nenhum arquivo criado; aguarda o intervalo.
- `656`: bloqueio por consumo indevido; nenhum arquivo criado; aguarda o intervalo.
- Outros `cStat`: mostra a rejeição, não avança NSU.
- Eventos `procEventoCTe` ou outros DF-e: preserva cópia bruta de recuperação, mas não os mistura com CT-e completos.
- Falha parcial de gravação: preserva ZIP de recuperação e mantém NSU; não repetir a consulta sem reconciliar o resultado.
- Timeout: a reserva local evita que o mesmo NSU seja consultado novamente imediatamente.

## Limitações

Este é um primeiro fluxo controlado de **uma consulta manual por clique**, não um serviço recorrente. Não há comprovação de uma operação real em SEFAZ neste commit: os testes são de contrato e descompactação simulada. O checkout atual ainda usa o HANA para listar empresas e a configuração do A1, mas **o motor e checkpoint CT-e não fazem gravação em HANA**.

A pasta `.nexfiscal-cte` contém estado necessário para continuidade. Preservá-la ao atualizar a aplicação, assim como os arquivos de chaves, certificados e conexão.

O serviço pode entregar DF-e de interesse conforme os papéis fiscais do ator. Não há promessa de baixar CT-e que não esteja disponível ou de retornar todos os documentos históricos anteriores à janela permitida.

## Convenção de nomes atualizada

Cada XML completo salvo na pasta de destino é identificado exclusivamente pela **chave de acesso de 44 dígitos** extraída de `CTe/infCte/@Id` e conferida com `protCTe/infProt/chCTe`. A identificação por NSU fica apenas no checkpoint e no ZIP bruto de recuperação, para rastreabilidade. Arquivos já gravados com nome antigo não são renomeados automaticamente.
