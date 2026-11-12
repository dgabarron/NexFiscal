# NexFiscal — Próximas entregas: NFS-e ADN, agendador e recuperação 90 dias

Data: 2026-11-12. Status: **especificação funcional/técnica**, não implementação em produção.

## Premissa

A captura manual NF-e (`procNFe`) e CT-e (`procCTe`) com XMLs individuais já foi comprovada pelo usuário. Preservar a pasta X indicada no servidor e a separação dos NSUs por tipo/CNPJ. Não modificar os motores que funcionam antes de testes de regressão.

## Entrega 1 — NFS-e padrão nacional (ADN)

- Consumir a **API de contribuintes do Ambiente de Dados Nacional (ADN)**; não confundir com o emissor público de NFS-e nem a API municipal.
- Referência oficial: https://www.gov.br/nfse/pt-br/biblioteca/documentacao-tecnica/documentacao-atual/documentacao-atual
- Manual oficial contribuintes ADN: https://www.gov.br/nfse/pt-br/biblioteca/documentacao-tecnica/documentacao-atual/manual-contribuintes-apis-adn-sistema-nacional-nfse.pdf
- Swagger produção restrita: https://adn.producaorestrita.nfse.gov.br/contribuintes/docs/index.html
- Confirmar endpoints concretos, estrutura HTTP/JSON/XML, autenticação por mTLS e consulta por CNPJ vinculada ao CNPJ raiz antes de integrar. O manual cita `GET /DFe/{NSU}` e `GET /NFSe/{ChaveAcesso}/Eventos`.
- O CNPJ consultado deve ser parte interessada (prestador, tomador ou intermediário) e ter autorização adequada. A API só devolve o que estiver efetivamente disponível no ADN.
- Criar `NfseDistributionClient`, parser, validação de XML/assinatura quando aplicável e `NfseCursor` independente dos de NF-e e CT-e. Aumentar proteção contra payloads grandes/arquivos maliciosos e preservar códigos/mensagens retornados pela API.
- Gravar NFS-e XML completo individual, não PDF, na mesma pasta X por tipo, com nome seguro (`NFSe_<chave>.xml` ou identificador oficial se disponível) e deduplicação por chave + SHA-256.
- Separar NFS-e dos eventos/retornos não utilizáveis. Não afirmar que o ADN cobre todos os municípios/documentos históricos.
- Primeiro teste real manual e controlado com um CNPJ autorizado antes de habilitar tarefa automática.

## Entrega 2 — Serviço de captura automática

Arquitetura sugerida:
- .NET 8 `BackgroundService`/Worker hospedado no servidor, como processo durável; para on-prem Windows, Windows Service; para Linux, systemd. **Não usar timer do navegador**.
- Um registro de execução por `CompanyId + DocumentType`, com `Enabled`, `IntervalMinutes`, `NextRunAtUtc`, `LastRunAtUtc`, `LastSuccessAtUtc`, `LastStatus`, `LastErrorCode`, `LastErrorMessage` e lease transacional. Manter configuração e estado persistentes, não em memória do processo.
- Intervalos configuráveis por empresa e por tipo (NF-e, CT-e, NFS-e), com 60 minutos como sugestão inicial. O intervalo efetivo é o maior entre a configuração e `NextAllowedAtUtc`/limites da respectiva fonte; permitir continuar lotes enquanto a regra fiscal permitir.
- Lock distribuído/lease resistente a reinício, no máximo um consumidor do mesmo tipo/CNPJ; backoff em 429, 656, timeout e indisponibilidade, sem reset de NSU.
- Pausar/retomar no painel e executar manualmente sem corrida com o serviço automático.
- Manter os XMLs na pasta escolhida, com escrita atômica, idempotência, checagem de hash e checkpoint somente após preservar a resposta.
- Expor logs sem revelar certificado, senha, CNPJ completo ou XML.
- Não implementar manifesto de ciência/confirmação fiscal sem uma autorização explícita apropriada.

## Entrega 3 — Recuperar documentos dos últimos 90 dias

Na Central de operação, três botões independentes: `Recuperar NF-e — 90 dias`, `Recuperar CT-e — 90 dias`, `Recuperar NFS-e — 90 dias`.

**Semântica do botão:** iniciar um *job de recuperação*, não promessa de que o órgão fiscal oferece consulta arbitrária `dataInicio/dataFim`.

- NF-e e CT-e: distribuição por NSU, não por data de emissão; a janela de documentos disponibilizados e as restrições fiscais limitam o histórico. O NSU é ordenado pela recepção no ambiente de distribuição, não necessariamente pela emissão. Por isso filtrar data dos XMLs depois de recebê-los não garante completude.
- **Nunca zerar o NSU corrente nem recuar o checkpoint de produção para buscar 90 dias:** isso pode causar 656, duplicidade ou perda de continuidade. Checar checkpoint, arquivos existentes, eventuais buracos conhecidos, cache bruto e documentação do serviço. Consultas pontuais por NSU/chave apenas quando suportadas e com limites específicos.
- Para NFS-e, seguir o contrato efetivamente disponível no ADN; confirmar condições de histórico/NSU e disponibilidade por contribuinte. Não assumir que as regras ou limites são idênticos à NF-e.
- Job específico de recuperação com estado próprio: `JobId`, `CompanyId`, `DocumentType`, `RequestedFromDate`, `RequestedToDate`, `CreatedAtUtc`, `State`, `Progress`, `FilesSaved`, `AlreadyPresent`, `NotAvailable`, `Errors`. O cursor principal não deve ser reescrito por um job.
- Mostrar limitações antes de começar e, ao terminar, relatório distinguindo **recuperado**, **já existia**, **não disponível**, **pendente de manifestação (NF-e)**, **falha** e **período sem cobertura garantida**.
- Para completar documentos fora da janela fiscal ou que nunca foram distribuídos ao CNPJ, prever obtenção do fornecedor/prestador ou fonte municipal conforme autorização; nunca prometer disponibilidade universal.

## Sequência de implementação e critérios de aceite

1. Primeiro **NFSe manual**: uma consulta legítima de ADN produz XML válido na pasta X; cursor NFS-e gravado e retomável; não usa o motor SOAP da NF-e.
2. Depois **Worker automático**: modo manual continua funcionando; agendamento persiste após reinício; não há chamadas duplicadas/656 por concorrência; exibir última e próxima execução por tipo e empresa.
3. Depois **recuperação 90 dias**: três botões com jobs, progresso e relatórios; nenhum deles altera retroativamente o cursor corrente.

Critérios de segurança: trilhas de auditoria, proteção do certificado A1, acesso autenticado ao painel antes de rede externa, bloqueio de caminhos arbitrários se usuários remotos puderem editar a pasta X, integridade dos arquivos e respeito a limites dos serviços.

## Situação atual

Nenhuma API de ADN foi acionada nem agendamento/recuperação de 90 dias foi iniciado por este documento. Esta especificação orienta as próximas alterações.
