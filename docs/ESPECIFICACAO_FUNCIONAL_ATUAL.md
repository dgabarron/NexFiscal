# NexFiscal — especificação funcional vigente

**Status:** aprovada para implementação/QA na branch `qa/frontend-enxuto-20261113`. **Atualização:** 13/11/2026. Esta especificação prevalece sobre roadmaps, prompts de redesign e notas históricas anteriores.

## Objetivo

Permitir que uma empresa cadastrada consulte e armazene XMLs de NF-e **que estejam efetivamente disponíveis para o seu CNPJ no serviço nacional de distribuição da SEFAZ**, sem exigir que o usuário conheça o controle de NSU.

## As duas funções do Main

### 1. Monitoramento automático

**Entrada do usuário:** selecionar empresa, definir a pasta de XMLs no servidor, configurar intervalo de X minutos, ativar e salvar.

**Comportamento:**
- O `NfeMonitoringWorker` roda no backend, avaliando periodicamente as empresas. O laço interno de avaliação ocorre aproximadamente a cada 30 segundos; isso **não** significa uma consulta fiscal a cada 30 segundos.
- Só tenta consultar quando monitoramento está habilitado, há pasta configurada, certificado A1 armazenado e o horário fiscal permite. O intervalo configurado é subordinado à janela de espera da SEFAZ.
- Retoma automaticamente o checkpoint interno associado à empresa e grava os XMLs completos efetivamente recebidos, sem sobrescrever arquivos divergentes.
- Desativar o monitoramento para a empresa não apaga a configuração da pasta, não redefine checkpoint e **não bloqueia a função manual**.

**Resultado visível:** estado de configuração, última tentativa, contagem registrada no HANA e próxima janela de consulta permitida. O produto não deve prometer uma próxima execução exata quando o backend só informa o fim do bloqueio fiscal.

### 2. Consultar tudo disponível

**Entrada do usuário:** selecionar a empresa e clicar em **Consultar todos os XMLs disponíveis** (botão da Operação).

**Comportamento:**
- O frontend envia `POST /api/sefaz/nfe/catchup` com o `companyId` da empresa selecionada. O backend consulta lotes de distribuição `distNSU` a partir do checkpoint interno, sem NSU fornecido pelo operador.
- A execução ocorre em job no backend; o frontend acompanha seu estado com `GET /api/sefaz/nfe/catchup/{companyId}`.
- A ação manual exige certificado A1 e pasta existente, mas **não** exige o toggle de monitoramento ligado.
- O job tenta percorrer a disponibilidade informada pela SEFAZ; pode **pausar** se houver bloqueio de uma hora, `cStat 656`, falha ou necessidade de reconciliar persistência. Não prometer que uma execução necessariamente baixará todo o histórico ou esgotará todos os lotes.
- Não altera manualmente nem retrocede o checkpoint. Worker e consulta manual compartilham controle de NSU e não devem disparar consultas concorrentes.

**Resultado visível:** status de execução, lotes processados, XMLs completos novos, já existentes e mensagem quando há pausa ou erro. Números técnicos como NSU só aparecem em detalhes de diagnóstico, nunca como campos editáveis.

### Semântica de documentos

A SEFAZ pode retornar `procNFe` (NF-e completa), `resNFe` (resumo) ou eventos. **Somente `procNFe` válido é salvo como XML completo na pasta operacional**; os outros tipos não devem ser apresentados como notas completas capturadas. Duplicatas de mesmo conteúdo não geram arquivos novos. O backend também mantém lote bruto interno para diagnóstico e redução de risco de perda em falhas de gravação; isso **não** cria uma função de restauração de ZIP para usuário.

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
- CT-e e NFS-e no Main, Worker CT-e/NFS-e, importação direta de documentos no SAP Business One.
- Disponibilização na rede, autenticação AD/LDAPS e homologação de produção.

Código/endpoint legado para outros métodos poderá existir enquanto não houver uma limpeza técnica específica, mas **não define o produto**.

## Critérios de aceite de QA

- Exibir **somente monitoramento automático e botão de consulta de tudo disponível** na página Operação.
- A empresa selecionada determina os dados e ações; seleção não deve misturar CNPJs.
- Salvar pasta mesmo com monitoramento desligado e conseguir usá-la no botão manual.
- Não exibir campos de NSU, data retroativa, restauração ZIP, CT-e ou NFS-e como operações principais.
- Não iniciar consulta fiscal no teste HTTPS, na simples navegação ou ao salvar agendamento.
- Consultas concorrentes e bloqueios fiscais retornam erro/pausa visível, não sucesso fictício.
- Informar `137` (sem mais documentos), `138` (documentos), `656` (consumo indevido/bloqueado) sem prometer cobertura histórica total.
- Confirmar gravação de `procNFe` na pasta, contagem correta e controle de persistência HANA.
- Testes reais com HANA, certificado A1 e SEFAZ devem ser concluídos em QA antes de qualquer implantação.
