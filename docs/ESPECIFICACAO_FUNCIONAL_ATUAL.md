# NexFiscal — especificação funcional vigente

**Status:** aprovada para implementação/QA na branch `qa/frontend-enxuto-20261113`. **Atualização:** 13/11/2026. Esta especificação prevalece sobre roadmaps, prompts de redesign e notas históricas anteriores.

## Objetivo

Permitir que uma empresa cadastrada consulte e armazene XMLs de NF-e **que estejam efetivamente disponíveis para o seu CNPJ no serviço nacional de distribuição da SEFAZ**, sem exigir que o usuário conheça o controle de NSU.

## NF-e: duas funções do Main

### 1. Monitoramento automático

**Entrada do usuário:** selecionar empresa, definir a pasta de XMLs no servidor, configurar intervalo de X minutos, ativar e salvar.

**Comportamento:**
- O `NfeMonitoringWorker` roda no backend, avaliando periodicamente as empresas. O laço interno de avaliação ocorre aproximadamente a cada 30 segundos; isso **não** significa uma consulta fiscal a cada 30 segundos.
- Só tenta consultar quando monitoramento está habilitado, há pasta configurada, certificado A1 armazenado e o horário fiscal permite. O intervalo configurado é subordinado à janela de espera da SEFAZ.
- Retoma automaticamente o checkpoint interno associado à empresa e grava os XMLs completos efetivamente recebidos, sem sobrescrever arquivos divergentes.
- Desativar o monitoramento para a empresa não apaga a configuração da pasta, não redefine checkpoint e **não bloqueia a função manual**.

**Resultado visível:** estado de configuração, última tentativa, contagem registrada no HANA e próxima janela de consulta permitida. O produto não deve prometer uma próxima execução exata quando o backend só informa o fim do bloqueio fiscal.

### 2. Consultar tudo disponível

**Entrada do usuário:** selecionar a empresa e clicar em **Consultar todos os XMLs disponíveis** (aba NF-e da Operação). O botão permanece clicável mesmo durante a espera fiscal, para informar o horário permitido.

**Comportamento:**
- O frontend envia `POST /api/sefaz/nfe/catchup` com o `companyId` da empresa selecionada. O backend consulta lotes de distribuição `distNSU` a partir do checkpoint interno, sem NSU fornecido pelo operador.
- A execução ocorre em job no backend; o frontend acompanha seu estado com `GET /api/sefaz/nfe/catchup/{companyId}`.
- A ação manual exige certificado A1 e pasta existente, mas **não** exige o toggle de monitoramento ligado. Se a janela fiscal ainda estiver bloqueada, clicar no botão informa a espera sem disparar uma consulta antecipada.
- O job tenta percorrer a disponibilidade informada pela SEFAZ; pode **pausar** se houver bloqueio de uma hora, `cStat 656`, falha ou necessidade de reconciliar persistência. Não prometer que uma execução necessariamente baixará todo o histórico ou esgotará todos os lotes.
- Não altera manualmente nem retrocede o checkpoint. Worker e consulta manual compartilham controle de NSU e não devem disparar consultas concorrentes.

**Resultado visível:** status de execução, lotes processados, XMLs completos novos, já existentes e mensagem quando há pausa ou erro. Números técnicos como NSU só aparecem em detalhes de diagnóstico, nunca como campos editáveis.

### Semântica de documentos

A SEFAZ pode retornar `procNFe` (NF-e completa), `resNFe` (resumo) ou eventos. **Somente `procNFe` válido é salvo como XML completo na pasta operacional**; os outros tipos não devem ser apresentados como notas completas capturadas. Duplicatas de mesmo conteúdo não geram arquivos novos. O backend também mantém lote bruto interno para diagnóstico e redução de risco de perda em falhas de gravação; isso **não** cria uma função de restauração de ZIP para usuário.

## CT-e — consulta manual preservada

- A aba **CT-e** foi restaurada no Main, sem modificar o escopo de duas funções NF-e.
- O usuário pode habilitar a consulta manual CT-e, informar sua pasta de XMLs no servidor e salvar a configuração.
- O botão **Consultar CT-e** usa `POST /api/cte/download`; o estado é consultado por `GET /api/cte/status/{companyId}`.
- O CT-e possui checkpoint e janela fiscal independentes do NF-e. Ainda **não existe monitoramento automático CT-e**.
- A consulta em cooldown informa o próximo horário sem enviar requisição antecipada.
- O operador deve confirmar que não há outro consumidor CT-e ativo para o CNPJ.
- O CT-e não tem consulta de NSU específico, busca histórica por data ou restauração de ZIP.

## Pré-requisitos de uso, não novas funções de captura

1. Configurar e testar o SAP HANA; criar/verificar o schema `BRAI_NEX` e as cinco tabelas esperadas.
2. Registrar uma ou mais empresas usando certificado A1 (`.pfx`/`.p12`) válido, extraindo razão social e CNPJ do certificado.
3. Configurar uma pasta de destino válida e acessível **no servidor**. A configuração de pasta é compartilhada pelas duas operações.
4. Acessar apenas no host local durante esta fase: não há login multiusuário/AD/LDAPS implementado.

A área **Empresas** serve para cadastrar e conferir A1; **Configuração técnica** serve para HANA e teste HTTPS de conectividade, sem consultar XMLs. Essas áreas dão suporte às duas operações do Main, mas não constituem métodos extras de captura.

## Fora do escopo atual

- Consulta por NSU específico (`consNSU`), busca de lacunas e recuperação retroativa.
- Consulta por data de emissão, filtro de 90 dias, varredura histórica e reinicialização do cursor.
- Importação ou restauração de ZIPs locais, interface de gerenciamento de lotes ZIP.
- Uma terceira operação de captura NF-e "consultar um único lote" para o usuário.
- NFS-e no Main, Worker CT-e/NFS-e, importação direta de documentos no SAP Business One.
- Disponibilização na rede, autenticação AD/LDAPS e homologação de produção.

Código/endpoint legado para outros métodos poderá existir enquanto não houver uma limpeza técnica específica, mas **não define o produto**.

## Critérios de aceite de QA

- Exibir **somente monitoramento automático e botão de consulta de tudo disponível** na página Operação.
- A empresa selecionada determina os dados e ações; seleção não deve misturar CNPJs.
- Salvar pasta mesmo com monitoramento desligado e conseguir usá-la no botão manual.
- Não exibir campos de NSU, data retroativa, restauração ZIP ou NFS-e. Preservar a aba CT-e de consulta manual.
- Não iniciar consulta fiscal no teste HTTPS, na simples navegação ou ao salvar agendamento.
- Consultas concorrentes e bloqueios fiscais retornam erro/pausa visível, não sucesso fictício.
- Informar `137` (sem mais documentos), `138` (documentos), `656` (consumo indevido/bloqueado) sem prometer cobertura histórica total.
- Confirmar gravação de `procNFe` na pasta, contagem correta e controle de persistência HANA.
- Testes reais com HANA, certificado A1 e SEFAZ devem ser concluídos em QA antes de qualquer implantação.

## Ajustes da experiência de operação — 13/11/2026

A tela principal apresenta a empresa selecionada e duas abas: **Notas fiscais (NF-e)** e **Conhecimentos (CT-e)**. A NF-e possui botão de captura manual, status do pedido, indicadores de registros recebidos e configuração do monitoramento por intervalo. CT-e preserva captura manual e configuração própria, sem Worker automático.

**Solicitar captura durante a espera fiscal:** o clique registra um job de NF-e no backend e a interface mostra **Agendada**; o backend aguarda a próxima janela permitida e então tenta a captura automaticamente. O usuário não precisa clicar novamente. O Worker NF-e não concorre com esse job. **Limitação de QA:** a solicitação agendada fica somente em memória e é interrompida quando o processo da API reinicia; nesse caso, é necessário registrar uma nova solicitação. A contagem de registros HANA inclui resumos/eventos e não deve ser apresentada como quantidade de XMLs completos.

**Notas com XML ausente:** o sistema ainda não tem serviço de consulta individual por chave; a análise exige chave de 44 dígitos para cruzar HANA, arquivo gravado e situação de disponibilização. Não prometer que o botão recupera todas as notas emitidas.

## Escopo de CT-e alinhado à NF-e — 13/11/2026

A partir desta revisão, **CT-e e NF-e apresentam a mesma experiência operacional**: monitoramento automático com intervalo configurável e pasta de XMLs, botão de captura manual de todos os lotes atualmente disponíveis, acompanhamento da execução e agendamento automático da solicitação durante a espera fiscal. A consulta CT-e usa `POST /api/sefaz/cte/catchup` e o progresso `GET /api/sefaz/cte/catchup/{companyId}`, com serviço `CteSefazCatchup` e Worker `CteMonitoringWorker`.

O motor CT-e mantém distribuição fiscal, XML `procCTe`, checkpoint e janela SEFAZ próprios, independentes de NF-e. A configuração `CTE` do agendamento controla o monitoramento automático; a captura manual exige certificado A1 e pasta válida, não o monitoramento ativado. O CT-e tem armazenamento de checkpoint local em `.nexfiscal-cte/`, ao contrário do checkpoint NF-e no SAP HANA. **Não confundir equivalência de interface e fluxo com identidade de persistência.**

O histórico anterior deste documento pode mencionar CT-e apenas manual: essa informação foi substituída nesta revisão. Não há restauração por ZIP, busca por data, consulta de NSU específico ou garantia de recuperar documentos fora da disponibilidade fiscal. Jobs manuais agendados continuam apenas em memória e não sobrevivem ao reinício da API. Homologação real com SEFAZ/CT-e ainda pendente.

## Diagnóstico de NF-e ausentes por chave — QA

Na aba NF-e, o bloco **Verificar XMLs não encontrados** permite colar de 1 a 50 chaves de 44 dígitos. Ele envia `POST /api/nfe/diagnostics/keys` com `{"companyId":"<GUID>","keys":["<chave>"]}` e faz verificação somente de leitura: procura o arquivo `<chave>.xml` na pasta de NF-e configurada, valida se é `nfeProc`, confere envelopes completos `procNFe`, resumos `resNFe` e eventos em `BRAI_NEX.BRAI_NEX_Envelope` para a empresa selecionada. O relatório distingue completo, XML ausente da pasta, somente resumo, somente evento, arquivo divergente, apenas na pasta, não localizado e inconclusivo. O cruzamento tem limite de 100.000 envelopes, sinalizando quando a análise é parcial. A operação **não executa consultas SEFAZ, não altera NSU nem restaura XMLs**. Os resultados só podem ser confirmados quando a API puder acessar o HANA e a pasta reais.

Cinco notas informadas como ausentes na homologação, ainda sem resultado de consulta real: 738 (NRTECNAGEM), 10963 (JVC MATERIAIS), 32072 (MASTER TECNOLOGIA), 87228 (DIGIMEC), 1396817 (CASA MIMOSA), todas de novembro/2026. Suas chaves fornecidas possuem estrutura e dígito verificador corretos, mas isso não comprova disponibilização ou captura do XML.
