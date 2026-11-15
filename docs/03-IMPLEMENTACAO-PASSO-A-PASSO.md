# 03 — Plano incremental de implementação para Claude Code

## Preparação sem alterar a máquina do usuário
1. Clonar `https://github.com/dgabarron/NexFiscal`.
2. Fazer checkout da branch `v1/etapa-01-hana`, confirmar commit e árvore atuais (podem ter avançado desde esta documentação).
3. Criar branch de trabalho *a partir da V1*, jamais juntar tudo de `qa/refactor-estabilizacao-20261113`.
4. Executar `dotnet build` e `npm install && npm run build`.
5. Executar testes e smoke HTTP; listar falhas antes de mudar código.
6. Verificar se diretório de dados privado não foi confundido com instalação legada; não migrar silenciosamente.

## Sprint A — F1 SAP HANA (FAZER AGORA)
- [ ] Inspecionar `HanaSetup`, `HanaSchemaInstaller`, `HanaSchemaVerifier`, SQL e frontend.
- [ ] Testar entrada inválida, credenciais incorretas, host indisponível, schema ausente e existente.
- [ ] Garantir que **Testar conexão** não cria/edita tabelas ou escreve credenciais.
- [ ] Garantir que **Salvar** prepara apenas objetos ausentes (sem `DROP`, `TRUNCATE`, reinicialização de dados), valida colunas e escreve configuração protegida atomicamente.
- [ ] Revisar idempotência de provisioning e tratamento do SQL de migração.
- [ ] Ao reiniciar com o mesmo usuário Windows, verificar que o status fica "configurado e conectado"; testar também erro de DPAPI sem sobrescrever arquivo cifrado anterior.
- [ ] Garantir `GET /`, assets JS/CSS, `/api/health`, `/api/setup/hana/status` respondendo em Windows.
- [ ] Validar que servidor HTTP escuta **somente** loopback.
- [ ] Exibir no frontend mensagens específicas: conexão recusada/credenciais, schema incompleto, permissão insuficiente, salvo com sucesso, criptografia ilegível; sem vazar detalhes internos ou segredos.
- [ ] Escrever testes unitários/integrados com HANA de teste quando disponíveis, e evidência dos fluxos.
- [ ] Entregar ZIP/branch/PR isolados e **aguardar aprovação do usuário**; não implementar F2 sem isso.

## Sprint B — F2 Empresas e A1 (SÓ APÓS ACEITE F1)
- [ ] Inspecionar código antigo apenas como referência: `A1CompanyCertificate`, `CompanyCertificateVault`, `CompanyCertificateRecovery`, `CompanyRegistrationRemoval` na branch de QA.
- [ ] Criar tabela/migration para metadados da empresa/certificado, caso os campos originais sejam insuficientes; preservar companyId e histórico.
- [ ] Implementar upload seguro PFX/P12, limite de tamanho, inspeção de certificado, extração CNPJ/razão social/UF/validade e confirmação.
- [ ] Verificar chave privada, senha, CPF/CNPJ conforme padrão ICP-Brasil aplicável; a aplicação é **para CNPJ**, não cadastrar certificado sem CNPJ validado.
- [ ] Implementar cofre protegido em caminho fixo, leitura/decriptação, rotação A1, status faltando/ilegível/vencido/legível e testes após restart.
- [ ] Adicionar frontend: Empresas, Adicionar, Validar, Atualizar A1, Remover/Arquivar, Selecionar.
- [ ] Criar testes de mesmo CNPJ, duplicidade, rejeição de A1 de outro CNPJ, remoção com e sem histórico, DPAPI/identidade Windows.
- [ ] Entregar etapa 2 para teste real e aguardar aceite.

## Sprint C — F3 Captura manual (SÓ APÓS ACEITE F2)
- [ ] Estudar manual/NT vigente do NFeDistribuicaoDFe: `distNSU`, `consNSU`, `retDistDFeInt`, `docZip`, `cStat`, regras de intervalo e manifestação do destinatário.
- [ ] Inspecionar código legível anterior: `NfeDistributionClient`, `NfeCompleteDocument`, `NfeFirstDownload`, `NfeCaptureOrchestrator`, `NfeIndividualXmlStore`, `HanaNfeDistributionStore` etc. Reaproveitar somente o que for testado e correto.
- [ ] Construir `NfeCaptureEngine` compartilhável com F4 e armazenamento de jobs duráveis.
- [ ] Antes de consultar, inventariar XMLs completos e pendências conhecidas (sem exigir digitação de chaves).
- [ ] Planejar sequenciamento de `distNSU` e recuperação seletiva por `consNSU` de NSUs elegíveis conhecidos; impedir brute force, consultas sem autorização ou saturação do serviço.
- [ ] Persistir arquivos `procNFe` completos com nome `<chave>.xml`, checar validade e não duplicar; resNFe/eventos apenas como evidência.
- [ ] Tratar transação/checkpoint idempotente e reinício inesperado; garantir que arquivo salvo e cursor não se desencontrem.
- [ ] Respeitar 137, 138, 656 e outros `cStat` relevantes segundo normas vigentes; implementar `nextAllowedUtc` durável.
- [ ] Mostrar status real, quantidades e pendências; **não** marcar "tudo recuperado" se houver documentos indisponíveis.
- [ ] Testar mocks de SEFAZ, resumo-only, NSU ausente, XML já existente, XML inválido, lote repetido, cStat de cooldown, falha no HANA, falha em disco, reinício e concorrência.
- [ ] Testar com SEFAZ real apenas se houver autorização/credenciais e possibilidade legal; não exigir que o usuário forneça senha ao Claude Code.
- [ ] Entregar versão e aguardar aceite.

## Sprint D — F4 Monitoramento (SÓ APÓS ACEITE F3)
- [ ] Criar configuração por empresa: `Enabled`, `IntervalMinutes` ≥1, `DestinationDirectory`, `NextRunAtUtc`.
- [ ] Implementar worker hospedado com `BackgroundService` e persistência de estado, recuperando agendamento após restart.
- [ ] Reutilizar motor F3; respeitar mesma reserva/lock e intervalo da SEFAZ. Nenhuma consulta concorrente para mesmo CNPJ.
- [ ] Exibir habilitado/desabilitado, última execução, próxima tentativa e erro categorizado.
- [ ] Testar frontend fechado vs backend ativo, serviço reiniciado, pausa SEFAZ, A1 vencido, HANA inacessível, disco sem permissão, execução manual concorrente.
- [ ] Entregar etapa 4 e aguardar aceite.

## Release V1 (APÓS TODAS AS APROVAÇÕES)
- [ ] Validação de ponta a ponta Windows + HANA + A1 + SEFAZ + pasta XML.
- [ ] Build reproduzível e script de instalação/execução único.
- [ ] Snapshot/backup e plano de rollback.
- [ ] Documentação de operação e limites oficiais.
- [ ] CI verde e homologação do usuário; só então propor merge.

## Relatório obrigatório em cada entrega
```text
ETAPA: F1/F2/F3/F4
Branch e commit:
Arquivos alterados:
O que funciona:
Como reproduzir:
Testes executados (resultado real):
Não testado / ambiente necessário:
Riscos / dados preservados:
Critérios ainda pendentes:
PEDIDO: "Valide esta etapa e me diga APROVADO ou o ajuste desejado."
```
