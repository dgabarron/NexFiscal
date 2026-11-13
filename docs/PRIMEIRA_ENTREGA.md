> **REGISTRO HISTÓRICO — NÃO VIGENTE (13/11/2026).** Este texto retrata a primeira etapa do código. Afirmações abaixo como “sem cliente SOAP real”, “sem HANA” ou “sem Worker” não descrevem o estado da branch de QA atual. Para instalar/operar, use [README](../README.md), [especificação funcional](ESPECIFICACAO_FUNCIONAL_ATUAL.md) e [arquitetura e API](ARQUITETURA_E_APIS.md). O escopo atual possui **somente monitoramento automático NF-e e consulta manual de tudo disponível**. Não implementar itens antigos sem novo aceite.

---

## Texto original de registro da primeira entrega (preservado)
# NexFiscal — primeira entrega (em desenvolvimento)

## Implementado nesta branch
- Leitura defensiva da resposta de distribuição NF-e `retDistDFeInt`.
- Descompactação de `docZip` (GZip/Base64), com limite de tamanho, validação XML, SHA-256 e NSU.
- Regras de checkpoint que recusam retrocesso e respostas rejeitadas.
- Contratos para cliente fiscal e persistência transacional; orquestrador com reserva antes da chamada de rede.
- Testes unitários sintéticos e workflow GitHub Actions.

## Não implementado nesta entrega
- Cliente SEFAZ real com certificado A1 e autorização do CNPJ.
- Implementação concreta HANA para `INfeCaptureStore`.
- Serviço Windows, configuração de empresas, autenticação e portal Fluent UI.
- Homologação contra SEFAZ e HANA reais.

## Regras fundamentais para a próxima etapa
1. A persistência HANA precisa gravar envelopes e cursor na mesma transação, com concorrência otimista.
2. A reserva de consulta deve persistir antes de chamar a SEFAZ; falhas precisam manter uma espera conservadora.
3. Usar somente um consumidor de NSU por CNPJ e ambiente.
4. Não reiniciar NSU para recuperar histórico arbitrário.
5. Não armazenar senha de certificado, PFX ou XML real no Git.

## Produto
Nome visual: NexFiscal. Prefixo técnico reservado: `BRAI_NEX`. Portal futuro: Microsoft Fluent Design System / Fluent UI React v9.

## Etapa seguinte — persistência intermediária HANA (11/11/2026)

Implementados na branch:
- `sql/migrations/001_BRAI_NEX_initial.sql`: schema exclusivo `BRAI_NEX`, empresas, cursores, envelopes XML, log e versão de schema.
- `HanaNfeCaptureStore`: implementação ADO.NET de `INfeCaptureStore`, com reserva persistida, concorrência otimista, confirmação de NSU e XML na mesma transação e detecção de NSU com hash divergente.
- O modelo `NfeEnvelope` passa a preservar os bytes do XML descompactado, além de sua representação textual e hash.

**Validação:** compilação e testes sintéticos via GitHub Actions. A DDL e as operações SQL **não foram executadas em uma instância SAP HANA real**; compatibilidade do provider e do servidor deve ser testada antes de instalar. O provider HANA é fornecido pela aplicação host através de `DbProviderFactory`.

**Próximos bloqueios:** validar a migração inicial no HANA da empresa, criar rotina de migrações seguras para upgrades, cadastrar empresa e cursor de forma transacional, cliente SEFAZ usando A1, e testar a operação real ponta a ponta. Nenhuma tabela de schema nativo SAP B1 é acessada pelo motor.
