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
- Intervalos configuráveis em **campo numérico livre de minutos**, aceitando qualquer inteiro positivo de 1 até o limite representável (Int64) sem teto de negócio; 60 minutos como valor inicial de proposta. O intervalo efetivo é o maior entre a configuração e `NextAllowedAtUtc`/limites da respectiva fonte; permitir continuar lotes enquanto a regra fiscal permitir.
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

## Ajuste de interface acordado em 12/11/2026

- Campo de intervalo **livre** em minutos, mínimo de 1 minuto, sem opções predefinidas e sem máximo arbitrário de negócio. O backend usa `Int64`, que impõe apenas um limite técnico representável.
- Checkbox **Captura ativa** separado para NF-e, CT-e e NFS-e.
- **Pasta de destino** começa desabilitada quando Captura ativa está desmarcada. Ao marcar, habilita o campo de caminho e exige pasta existente no servidor com permissões de acesso. Desmarcar desabilita o campo, sem apagar o caminho já preenchido.
- Persistência da configuração em `.nexfiscal-schedules/<CompanyId>.json`, via GET/POST `/api/capture/schedules/{companyId}`.
- Salvar a configuração **não inicia consultas** enquanto o Worker de captura automática ainda não tiver sido implementado e homologado.
- O agendamento efetivo respeitará `NextAllowedAtUtc` e as regras das três fontes, mesmo quando o operador configurar 1 minuto.

## Unificação das pastas manual e automática

- O antigo campo avulso "Pasta de destino dos XMLs" abaixo do painel foi removido por duplicidade.
- A configuração por tipo (NF-e, CT-e, NFS-e) é a única fonte de destino tanto para as consultas manuais quanto para o futuro serviço automático.
- Para consultar manualmente, marcar `Captura ativa`, configurar a pasta e clicar em `Salvar configurações de captura`. Isto **não** ativa por si só o Worker, ainda não implementado.
- As rotas `POST /api/capture/download` e `POST /api/cte/download` recebem apenas `companyId` e `previousConsumerStopped`, e resolvem a pasta exclusivamente na configuração persistida; o cliente não pode indicar pasta alternativa.
- O backend mantém a validação de pasta existente e acessível antes de iniciar uma consulta fiscal.

## Próximo teste definido — Reconsulta pontual por data-limite e NSU antigo (12/11/2026)

**Objetivo:** recuperar um documento com NSU já conhecido, relativo ao período retroativo de até 90 dias, sem retroceder o `ultNSU` da captura contínua. A data-limite é um **filtro local**, e não um parâmetro de pesquisa por data suportado por `NFeDistribuicaoDFe`.

1. O operador seleciona tipo de documento (primeiro NF-e), data-limite e (quando conhecido) NSU/chave de acesso de um documento faltante.
2. O sistema pesquisa primeiramente o XML ou ZIP bruto já armazenado, os registros/NSUs conhecidos e os documentos sem XML completo.
3. Com NSU individual conhecido, usa `consNSU` da NF-e, se permitido e ainda dentro da disponibilidade de até 90 dias. Se houver chave, avaliar `consChNFe`. **Não** consultar `distNSU` com NSU antigo nem alterar checkpoint de produção.
4. Validar `procNFe`, chave + protocolo e hash antes de salvar o XML na pasta de NF-e configurada. Relatar `recuperado`, `já existe`, `fora de prazo`, `sem permissão`, `resumo/evento`, `bloqueado` e `não encontrado`.
5. Respeitar máximo publicado de **20 consultas pontuais por hora por CNPJ** e cooldown de `656`; não prometer recuperação universal.
6. CT-e e NFS-e: confirmar os métodos pontuais efetivamente documentados e seus limites específicos antes de ativar a mesma experiência; não assumir que `consNSU` e 90 dias funcionam identicamente nos três órgãos.

**Referência NF-e:** [NT 2014.002, portal oficial](https://www.nfe.fazenda.gov.br/Portal/exibirArquivo.aspx?conteudo=VgPuhtPApQo%3D). A NT distingue `distNSU` sequencial de `consNSU` pontual. Somente a consulta pontual pode referenciar um NSU antigo sem modificar a sequência principal, sujeito às regras do serviço.

**Situação:** roteiro do teste registrado. Ainda não foi desenvolvido endpoint `consNSU`, o botão de data-limite nem executada chamada real de recuperação.

## Esclarecimento: recuperação sem NSU informado pelo operador

A interface **não exige digitar NSU**. O usuário escolhe a data-limite (90 dias por padrão), empresa e tipo fiscal.

**Fontes locais de descoberta (ordem):** lotes brutos `docZip` preservados no ZIP de recuperação; `BRAI_NEX_Envelope` e logs HANA de NF-e; índices de NSU/chaves no sistema; arquivos XML individuais existentes. O lote que retornou 48 resumos/eventos é um candidato importante para identificar `resNFe` e seus NSUs e buscar possíveis XMLs completos após manifestação autorizada.

**NF-e:** `distNSU` é sequencial e limitado à janela da distribuição; não possui filtro por emissão/data. `consNSU` exige conhecer o NSU, recuperado do índice local, e `consChNFe` exige uma chave conhecida. Cada retorno deve respeitar o prazo de disponibilização, os limites de consultas pontuais e os bloqueios `656`. Nunca colocar `ultNSU=0` ou retroceder o cursor da captura contínua como estratégia para varrer histórico: quem já consultou o serviço pode receber bloqueio por sequência.

**Limite intransponível sem outra fonte:** se não há NSU, chave, lote bruto, registro fiscal ou outra referência do documento, não existe método oficial de pesquisar arbitrariamente todo o histórico informando somente a data. Em casos assim mostrar `não rastreável pela fonte atual` e orientar fontes fiscais/fornecedores autorizados. Novos consumidores podem ter disponibilidade inicial distinta, mas não é um reset seguro para consumidor ativo.

**CT-e e NFS-e:** descoberta local análoga, mas verificar endpoints, retenção e limites oficiais específicos de cada ambiente; não presumir os mesmos métodos da NF-e.

**Observação de implementação:** o botão primeiro gera uma prévia de documentos rastreáveis e data de referência; só depois executa consultas fiscais. Resultado inclui recuperados, já existentes, resumos pendentes, não disponíveis e sem cobertura comprovada. O checkpoint normal permanece intacto.

## Versão de teste implementada — recuperação local NF-e e `consNSU`

**Entregue na branch `feat/nfe-capture-core`:**

- Painel: **Teste de recuperação de NF-e antigas**, campo **Data-limite**, botão **Identificar NSUs antigos** (somente leitura local) e botão **Reconsultar 1 NSU na SEFAZ**.
- `GET /api/recovery/nfe/preview/{companyId}?from=<ISO8601>`: examina `manifest.json` + XML nos ZIPs existentes de `.nexfiscal-downloads/<CompanyId>`, valida SHA-256 e seleciona candidatos `resNFe` com `dhEmi` dentro do intervalo. Exclui CT-e, eventos sem data e XMLs completos já gravados. **Não envia requisição fiscal**.
- `POST /api/recovery/nfe/one`: seleciona automaticamente um candidato ainda não tentado nos últimos 60 minutos, usa `consNSU` com certificado A1 e ambiente de produção; preserva a resposta bruta e só grava `procNFe` na pasta NF-e configurada. Nunca atualiza `BRAI_NEX_CaptureCursor`.
- A cota local é limitada a **19 tentativas pontuais por hora**, abaixo do teto nominal de 20 para reduzir risco, e após `656` impede novas consultas por 1 hora. As tentativas e bloqueios ficam em `.nexfiscal-recovery/<CompanyId>.json`.
- Os documentos sem `dhEmi` não podem ser classificados pela data e são relatados à parte. Arquivos sem ZIP bruto correspondente não são descobertos.
- A recuperação é **experimental e manual**: não equivale a obter tudo por data ou prometer XML completo após reconsultar resumo. Se a SEFAZ só disponibilizar resumo por ausência de manifestação autorizada, o resultado será indicado e não haverá confirmação automática de operação.
- O teste CT-e e a captura NFS-e/ADN retroativa não estão disponíveis nesta versão; cada serviço exige método próprio.

**Como testar:** atualizar o projeto sem excluir `.nexfiscal-downloads`, `.nexfiscal-certificates`, `.nexfiscal-keys`, `.nexfiscal-cte`, `.nexfiscal-schedules` e o checkpoint HANA. No painel, marcar e salvar **Captura ativa — NF-e** e a pasta existente. Primeiro clicar **Identificar NSUs antigos**, verificar os candidatos. Depois, quando permitido, clicar **Reconsultar 1 NSU na SEFAZ**. Conferir status e arquivo. Não reexecutar manualmente após erro de conexão sem averiguar o estado.
