# NexFiscal — instalar e operar o portal local (versão QA)

**Vigente na branch:** `qa/frontend-enxuto-20261113`, 13/11/2026. Esta versão substitui as instruções antigas de handoff obrigatório, teste unitário no Main e ausência de Worker. Leia [escopo funcional](ESPECIFICACAO_FUNCIONAL_ATUAL.md) e [arquitetura/API](ARQUITETURA_E_APIS.md).

## Requisitos

- Windows com .NET SDK 8, Visual Studio 2022 opcional e Node.js 20+.
- Acesso ao SAP HANA (schema `BRAI_NEX`; provider SAP `Sap.Data.Hana.Net.v8.0`).
- Certificado A1 `.pfx` ou `.p12` da empresa com senha e permissão de uso.
- Diretório de destino **no servidor** acessível à conta que executa o NexFiscal.
- Rede local e saídas HTTPS/TLS compatíveis com a validação ICP-Brasil e SEFAZ.

**Atenção:** o projeto é on-premises, mas este portal de bootstrap é **somente loopback**. Ainda não há login AD, LDAPS, permissões por usuário ou publicação segura em rede. Não usar em produção sem QA e revisão de segurança.

## Iniciar no ambiente de desenvolvimento/QA

1. Clonar o repositório e selecionar a branch `qa/frontend-enxuto-20261113`.
2. Na raiz do repositório, executar:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Run-NexFiscalLocal.ps1
```

3. O script instala dependências Node, compila React/Vite para `src/NexFiscal.Web/wwwroot` e inicia a API ASP.NET Core.
4. Abrir `http://127.0.0.1:5187` no próprio servidor. O endereço vem de `NexFiscal:ListenUrl` em `src/NexFiscal.Web/appsettings.json`.

Para desenvolvimento do frontend isolado, executar `cd frontend` e `npm run dev`; `frontend/vite.config.js` encaminha `/api` para `127.0.0.1:5187`. A API precisa estar em execução no backend local.

## Instalação pelo assistente

1. **SAP HANA:** preencher host, porta, usuário e senha. Usar **Testar conexão**. Se as cinco tabelas não existirem, usar **Preparar estrutura** com credenciais que tenham permissão DDL, repetir a validação e salvar. A conexão é protegida no servidor.
2. **Empresa e A1:** escolher arquivo `.pfx`/`.p12`, informar senha, validar dados extraídos, conferir CNPJ/razão social e cadastrar. O cadastro não exige digitar NSU nem informar dados de fornecedor anterior.
3. **Operação:** selecionar empresa; informar pasta de XMLs no servidor e salvar a configuração. O mesmo diretório serve ao monitoramento e à consulta manual.

## Operação: NF-e e CT-e

### Monitoramento automático NF-e

Ativar **Monitoramento automático**, informar o intervalo em minutos e salvar. O `NfeMonitoringWorker` avalia as configurações e consulta periodicamente, **somente** quando o intervalo e a janela fiscal permitem. Desativar a opção interrompe novas tentativas automáticas; não exclui XMLs, configurações de pasta nem o checkpoint interno.

### Consultar todos os XMLs disponíveis

Clicar **Consultar todos os XMLs disponíveis**. A API inicia um job `distNSU` para aquele CNPJ; a tela mostra estado, lotes, XMLs novos e já presentes. O botão independe do monitoramento automático estar ativado, **mas exige pasta salva e A1**. Mesmo durante a espera fiscal, ele permanece clicável para informar a próxima janela, sem consultar a SEFAZ antecipadamente. A SEFAZ pode pausar a operação; nem todo DF-e distribuído é uma NF-e completa `procNFe`, e a consulta não garante documentos anteriores à janela fiscal de disponibilidade.

Na aba **CT-e**, configure a pasta, habilite a consulta manual e clique em **Consultar CT-e**. O CT-e usa seu próprio checkpoint fiscal, não existe Worker CT-e nesta versão e a consulta em espera apenas informa a próxima janela. Não há campos para NSU específico, data, consultas retroativas nem recuperação por ZIP.

## Diagnóstico e limitações

- **Configuração técnica** fica no cabeçalho, fora da navegação principal. Ali é possível rever o HANA ou testar conectividade HTTPS SEFAZ para empresa cadastrada.
- Teste de conectividade HTTPS 200/WSDL **não prova autorização SOAP de distribuição de NF-e**.
- `/api/health` apenas informa estado da configuração: não comprova que todas as tabelas estão acessíveis.
- `cStat 137` informa ausência de novos documentos naquele momento; `138` indica resposta com documentos; `656` exige respeito à janela de segurança. Não insistir em consultas durante cooldown.
- Se falhar a gravação de arquivo ou checkpoint, **não repetir a consulta cegamente**. Conferir logs, persistência e lote bruto interno antes de agir; a API não disponibiliza restauração manual de ZIP.
- Se a página abrir sem empresas por falha de HANA, verificar o erro retornado por `/api/capture/status`. Não cadastrar empresa novamente por engano.

## Dados locais sensíveis e atualização

Conservar protegidos e com backup seguro:
- `.nexfiscal-hana.dat` (configuração HANA cifrada);
- `.nexfiscal-keys/` (chaves Data Protection);
- `.nexfiscal-certificates/` (cofre A1);
- `.nexfiscal-schedules/` (configuração por empresa);
- `.nexfiscal-downloads/` (lotes técnicos já recebidos e arquivados internamente).

Esses itens não devem ser enviados ao Git nem apagados em atualização. No Windows, chaves protegidas com DPAPI são associadas ao contexto do serviço; **copiar apenas os arquivos pode não ser suficiente para migrar de servidor/usuário**.

## Compilação e testes técnicos

```powershell
dotnet test tests/NexFiscal.Capture.Tests/NexFiscal.Capture.Tests.csproj -c Release
dotnet build src/NexFiscal.Web/NexFiscal.Web.csproj -c Release
cd frontend
npm install --no-audit --no-fund
npm run build
npm test
```

Os testes automatizados não substituem verificar com A1 autorizado e SAP HANA de QA a captura real, a integridade dos arquivos e o comportamento ao encerrar/reiniciar o servidor. Não foi realizado deploy em produção nesta atualização documental.

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
