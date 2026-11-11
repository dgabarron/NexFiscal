# NexFiscal — portal local de configuração

## Requisitos
- Windows com Visual Studio 2022 e workload ASP.NET Core ou .NET SDK 8.
- Node.js 20 ou superior (frontend React + Fluent UI v9).
- Para cadastro real, SAP HANA Client e provider .NET compatíveis, registrado e disponível ao processo.
- Por segurança, a primeira versão de configuração responde **somente em loopback** (127.0.0.1). Não publique na rede.

## Rodar no Visual Studio
1. Clone `https://github.com/dgabarron/NexFiscal.git` e selecione `feat/nfe-capture-core`.
2. Abra `NexFiscal.sln` no Visual Studio.
3. No terminal do Visual Studio, rode `powershell -ExecutionPolicy Bypass -File scripts/Run-NexFiscalLocal.ps1`.
4. Acesse `http://127.0.0.1:5187`.
5. Se preferir depurar o C#, execute `npm install` e `npm run build` dentro de `frontend/` e defina `NexFiscal.Web` como projeto de inicialização.

## Capacidades atuais
- Valida arquivo A1 e senha e exibe CNPJ, razão social, emissão/validade e emissor.
- O endpoint `POST /api/certificates/inspect` não salva o A1 nem a senha.
- O endpoint `POST /api/companies/register` recebe somente certificado e senha; cria a empresa **pausada** no HANA após configurar provider e connection string. Não aceita NSU nem ambiente enviado pelo usuário. O ambiente padrão da versão inicial é produção.
- O XML, o certificado e a senha não devem ser comitados em Git.

## Configuração HANA
Configurar no ambiente local do servidor, sem comitar segredos:
- `NexFiscal__HanaConnectionString`: string de conexão SAP HANA da conta com DML em BRAI_NEX.
- `NexFiscal__HanaFactoryType`: nome completo do tipo do factory provider e assembly, por exemplo via uma implementação instalada de `DbProviderFactory` com campo estático `Instance`.

**Atenção:** o projeto não distribui nem instala automaticamente driver SAP HANA. Sem um provider compatível, o cadastro permanece indisponível. O script SQL v1 deve existir no HANA. A1 não é armazenado nesta fase, portanto não é possível executar captura contínua após o cadastro. Não há verificação completa ICP-Brasil, CRL/OCSP ou comunicação real SEFAZ.

## Próximas etapas
- Persistência segura de A1 e rotação do certificado sem perder NSU.
- Cliente SOAP de NFeDistribuicaoDFe.
- Autenticação e HTTPS antes de habilitar acesso pela rede.
- Homologação da DDL e dos comandos com HANA real.

## Controle interno de NSU
O cadastro **não cria um checkpoint fictício**, e não consulta a SEFAZ. A ausência de linha em `BRAI_NEX_CaptureCursor` significa captura não inicializada. Uma empresa já cadastrada não tem seu NSU substituído; a tentativa de cadastro duplicado retorna erro sem alteração. `Enabled=0` impede o orquestrador de iniciar captura.

A próxima implementação deve identificar checkpoint anterior de fonte confiável ou executar inicialização controlada pela SEFAZ, após disponibilizar cliente SOAP A1 e persistência protegida. Se não houver prova de exclusividade do consumidor NSU, manter o estado aguardando e notificar administrador em diagnóstico técnico (sem expor NSU no cadastro normal). Não é possível descobrir com garantia absoluta o NSU vigente apenas lendo o A1.
