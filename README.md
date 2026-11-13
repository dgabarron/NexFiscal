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
