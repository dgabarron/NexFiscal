# NexFiscal

Motor nacional de captura de documentos fiscais eletrônicos para empresas que utilizam SAP Business One.

**Estado:** desenvolvimento inicial. Ainda não homologado em SEFAZ ou SAP HANA.

**Escopo da primeira aplicação:** NF-e modelo 55, distribuição nacional por NSU, com certificado A1, persistência no SAP HANA e execução em serviço .NET 8. A captura de CT-e e NFS-e virá depois.

**Identidade:** interfaces exibem somente NexFiscal. Prefixo interno reservado: `BRAI_NEX`. Interfaces futuras seguem Microsoft Fluent UI React v9.

O código da primeira fase será desenvolvido na branch `feat/nfe-capture-core`. Não utilizar credenciais reais, arquivos PFX ou XMLs com dados de clientes no Git.
