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
