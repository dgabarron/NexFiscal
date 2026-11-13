# NexFiscal

Captura de XMLs de NF-e disponíveis na SEFAZ para empresas que utilizam SAP Business One. Execução local (on-premises), com frontend React/Vite, backend ASP.NET Core .NET 8 e controle operacional no SAP HANA. A integração de importação de documentos no SAP Business One **não faz parte desta entrega**.

**Versão documentada:** branch `qa/frontend-enxuto-20261113`, 13/11/2026. **Estado:** QA; compilação e testes automatizados executados, sem homologação operacional com SAP HANA e SEFAZ reais e sem implantação em produção.

## NF-e: duas operações; CT-e: consulta manual

1. **Monitoramento automático de NF-e:** usuário configura um intervalo de X minutos, pasta de XMLs e habilitação; o serviço `NfeMonitoringWorker` consulta quando autorizado pela SEFAZ.
2. **Consultar tudo disponível:** botão manual que inicia `POST /api/sefaz/nfe/catchup`, percorrendo os lotes que a SEFAZ disponibiliza, a partir do controle interno já armazenado. Pode ser usado com monitoramento automático desativado.

**O botão de consulta manual NF-e fica clicável durante a espera fiscal:** nesse caso informa o horário de liberação, sem fazer chamada antecipada à SEFAZ. **Consultar tudo disponível não significa recuperar toda a vida fiscal da empresa.** A disponibilidade de documentos completos (`procNFe`), os limites de consulta e a janela de distribuição são definidos pela SEFAZ. Consultas podem terminar, pausar ou exigir nova tentativa após o período permitido.

O usuário **não informa NSU, não escolhe NSU específico, não informa data nem recupera XMLs de ZIPs locais**. O NSU é utilizado exclusivamente pelo motor como checkpoint técnico da distribuição. Não existem ações de `consNSU` retroativo, restauração de ZIP ou busca por período no produto atual.

## Interface

- **Operação:** abas NF-e e CT-e. NF-e oferece monitoramento automático e consulta manual de tudo disponível; CT-e oferece consulta manual com configuração de pasta.
- **Empresas:** cadastro pelo certificado digital A1 e identificação de CNPJ/razão social.
- **Configuração técnica** (no cabeçalho): conexão SAP HANA e diagnóstico de conectividade.

**CT-e está disponível na aba CT-e do Main, em modo manual**, com endpoint e checkpoint independentes de NF-e. Não existe Worker automático CT-e nesta versão. NFS-e não foi disponibilizada.

## Documentação vigente

- [Escopo funcional e critérios de aceite](docs/ESPECIFICACAO_FUNCIONAL_ATUAL.md)
- [Arquitetura, banco e contratos HTTP](docs/ARQUITETURA_E_APIS.md)
- [Instalação e execução local](docs/PORTAL_LOCAL.md)
- [Checklist de QA desta versão](docs/QA_ESCOPO_DUAS_FUNCOES_20261113.md)

Os documentos `docs/PRIMEIRA_ENTREGA.md`, `docs/NFE_PROTOCOL_AUDIT.md` e `docs/ROADMAP_NFSE_AGENDADOR_90_DIAS.md` são registros históricos. `docs/CTE_DISTRIBUICAO.md` descreve o mecanismo CT-e manual, com correções do escopo vigente no cabeçalho.

## Executar em desenvolvimento / QA

Requisitos: Windows com .NET SDK 8, Node.js 20+ e acesso a SAP HANA para testar o fluxo configurado. A partir da raiz do repositório:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Run-NexFiscalLocal.ps1
```

O script executa `npm install`, `npm run build` e inicia `NexFiscal.Web`. Acesse `http://127.0.0.1:5187`. O servidor restringe acesso a loopback; **não expor diretamente à rede** sem implementar autenticação/autorização e revisão de segurança.

Para verificar:

```powershell
dotnet test tests/NexFiscal.Capture.Tests/NexFiscal.Capture.Tests.csproj -c Release
dotnet build src/NexFiscal.Web/NexFiscal.Web.csproj -c Release
cd frontend
npm install --no-audit --no-fund
npm run build
npm test
```

Não versionar PFX, senhas, XMLs de clientes, arquivos `.nexfiscal-hana.dat`, `.nexfiscal-certificates/` ou `.nexfiscal-keys/`.

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
