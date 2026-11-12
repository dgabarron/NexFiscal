# NexFiscal — portal local de configuração

## Requisitos
- Windows com Visual Studio 2022 e workload ASP.NET Core ou .NET SDK 8.
- Node.js 20 ou superior (frontend React + shadcn/ui + Tailwind CSS).
- O portal usa o pacote SAP oficial `Sap.Data.Hana.Net.v8.0` para .NET 8. Validar compatibilidade do driver e a conexão com o HANA utilizado.
- Por segurança, a primeira versão de configuração responde **somente em loopback** (127.0.0.1). Não publique na rede.

## Rodar no Visual Studio
1. Clone `https://github.com/dgabarron/NexFiscal.git` e selecione `feat/nfe-capture-core`.
2. Abra `NexFiscal.sln` no Visual Studio.
3. No terminal do Visual Studio, rode `powershell -ExecutionPolicy Bypass -File scripts/Run-NexFiscalLocal.ps1`.
4. Acesse `http://127.0.0.1:5187`.
5. Para depurar, defina `NexFiscal.Web` como projeto de inicialização e pressione **F5**. O MSBuild executa `npm install` somente na primeira vez ou quando `frontend/package.json` mudar e executa `npm run build` automaticamente antes do backend. Não é necessário abrir PowerShell.

## Capacidades atuais
- Assistente na ordem: **HANA → Empresa/Certificado A1 → Operação**. O HANA é validado e configurado antes do cadastro.
- Valida arquivo A1 e senha e exibe CNPJ, razão social, UF quando presente no Subject ST, emissão/validade e emissor.
- O endpoint `POST /api/certificates/inspect` não salva o A1 nem a senha.
- O endpoint `POST /api/companies/register` recebe somente certificado e senha; cria a empresa **pausada** no HANA após configurar provider e connection string. Não aceita NSU nem ambiente enviado pelo usuário. O ambiente padrão da versão inicial é produção.
- O XML, o certificado e a senha não devem ser comitados em Git.

## Configuração HANA
1. Abrir o NexFiscal em http://127.0.0.1:5187.
2. Informar servidor, porta SQL, usuário técnico e senha no primeiro passo do assistente.
3. Selecionar **Testar conexão**; o servidor abre uma conexão real e confere as cinco tabelas do schema `BRAI_NEX`.
4. Selecionar **Salvar e continuar**; a configuração é protegida pelo ASP.NET Core Data Protection e armazenada no servidor, fora do repositório.
5. O arquivo protegido `.nexfiscal-hana.dat` e as chaves `.nexfiscal-keys/` são dados locais de instalação: **não copiar para Git**, não excluir ao atualizar o ZIP e manter com acesso restrito. No Windows, as chaves são protegidas por DPAPI, vinculadas ao contexto da instalação.
6. O cadastro da empresa só fica disponível depois da configuração HANA.

Pode-se manter a variável de ambiente `NexFiscal__HanaConnectionString` como fallback de administração avançada. A conexão usa o driver oficial e não exige mais configurar um factory manualmente.

**Não foi testada uma conexão no HANA da sua empresa**; os testes de CI verificam a compilação, não substituem o teste real. O portal permanece somente em loopback. Não há validação completa ICP-Brasil, CRL/OCSP nem comunicação real com a SEFAZ nesta etapa.

## Próximas etapas
- Persistência segura de A1 e rotação do certificado sem perder NSU.
- Cliente SOAP de NFeDistribuicaoDFe.
- Autenticação e HTTPS antes de habilitar acesso pela rede.
- Homologação da DDL e dos comandos com HANA real.

## Controle interno de NSU
O cadastro **não cria um checkpoint fictício**, e não consulta a SEFAZ. A ausência de linha em `BRAI_NEX_CaptureCursor` significa captura não inicializada. Uma empresa já cadastrada não tem seu NSU substituído; a tentativa de cadastro duplicado retorna erro sem alteração. `Enabled=0` impede o orquestrador de iniciar captura.

A próxima implementação deve identificar checkpoint anterior de fonte confiável ou executar inicialização controlada pela SEFAZ, após disponibilizar cliente SOAP A1 e persistência protegida. Se não houver prova de exclusividade do consumidor NSU, manter o estado aguardando e notificar administrador em diagnóstico técnico (sem expor NSU no cadastro normal). Não é possível descobrir com garantia absoluta o NSU vigente apenas lendo o A1.

## Instalação automática e cofre de certificados A1

- **Etapa 1:** após informar HANA e clicar em **Testar conexão**, se faltarem tabelas o assistente oferece **Preparar estrutura automaticamente**. Essa ação explícita executa a migração SQL embarcada, cria o schema/tabelas ausentes, sem remover nem sobrescrever dados existentes. Requer usuário com privilégios DDL na primeira instalação; use uma credencial operacional restrita após o provisionamento.
- A migração de instalação é versionada por `BRAI_NEX_SchemaVersion`. A versão inicial cria cinco tabelas, idempotentemente em nível de existência dos objetos. Ainda não substitui migrações de alteração de colunas em versões futuras: alterações de estrutura exigirão migrations incrementais.
- **Etapa 2:** o A1 é validado e, após cadastrar a empresa pausada, os bytes PKCS#12 e a senha são cifrados em um arquivo `.a1enc` por `CompanyId` dentro da pasta `.nexfiscal-certificates`, fora de `wwwroot`. A chave de proteção do ASP.NET Data Protection é protegida com **Windows DPAPI escopo do usuário que executa o serviço**. A chave privada não é instalada no repositório de certificados Windows nem persiste desprotegida em disco.
- O HANA guarda metadados da empresa; o PFX fica cifrado somente no servidor do NexFiscal. O operador não faz novas consultas manuais no banco para criar tabelas.
- Pastas `.nexfiscal-certificates` e `.nexfiscal-keys` e arquivo `.nexfiscal-hana.dat` são **críticos**: não remover na atualização da aplicação, aplicar ACLs restritas ao usuário de serviço e manter backup seguro de todo o conjunto. Sem a chave DPAPI do mesmo perfil Windows, os dados cifrados **não são necessariamente recuperáveis em outro host/usuário**. Para migração de servidor, será necessário um procedimento próprio de reimportação A1 e configuração HANA; o backup sozinho não garante recuperação.
- **Ainda não habilitar captura real automaticamente**: faltam cliente SOAP SEFAZ, verificação cadeia/revogação do certificado, renovação de A1, recuperação de falhas e isolamento de usuários na aplicação em rede.
- O processo atual é somente local `127.0.0.1`, sem autenticação multiusuário. Não expor as rotas de setup a outros computadores.

## Motor de comunicação NF-e — implementação inicial

- `src/NexFiscal.Capture/NfeDistributionClient.cs` contém transporte SOAP 1.2 mTLS para o endpoint oficial do AN, montagem do `distNSU` e leitura de `retDistDFeInt` (137/138/656), com `docZip` descompactado e SHA-256.
- O cliente **não é invocado por nenhum endpoint nem agendamento**. Não liga a captura automaticamente, não define NSU inicial nem grava XML/NSU no HANA. Somente ativar depois de: validar cadeia/revogação ICP-Brasil, implementar persistência de lote + checkpoint de modo atômico, garantir lease por CNPJ e impedir dois consumidores de NSU.
- O serviço existente de captura de outros fornecedores pode compartilhar o mesmo CNPJ. Não disparar solicitações reais de produção até planejar migração/coexistência.
- Testes `NfeDistributionClientTests` cobrem a montagem SOAP, parsing de retornos 137, 138, 656 e a descompactação de XML; sem consulta SEFAZ em CI.

### Persistência do primeiro lote no HANA

A classe `HanaNfeDistributionStore` recebe a resposta já validada e executa uma única transação: bloqueia `BRAI_NEX_CaptureCursor` com `FOR UPDATE`, compara o checkpoint com o NSU solicitado, grava os XMLs em `BRAI_NEX_Envelope` por NSU e SHA-256, e só então atualiza `LastNsu`. Uma falha provoca rollback. Status 137/656 nunca avançam o NSU; aplicam cooldown. Na falta de cursor preexistente, a gravação é recusada. **Ainda não há job, endpoint ou execução real habilitada.**

## Central de operação e teste de captura NF-e

- `GET /api/capture/status`: consulta o HANA e informa empresas, A1 armazenado, checkpoint NF-e, quantidade de envelopes, última tentativa, cooldown e motivo de bloqueio, sem acessar a SEFAZ. A interface abre a Central de operação diretamente quando encontra empresas registradas.
- `POST /api/capture/test-once`: recebe `{ "companyId": "<GUID>" }`, executa no máximo uma consulta e retorna código SEFAZ e quantidade de documentos novos. A captura não é automática e não cria NSU.
- **Bloqueio padrão obrigatório:** requer simultaneamente empresa `Enabled=1`, cursor `BRAI_NEX_CaptureCursor` inicializado e ainda disponível, A1 no cofre, cadeia de certificados verificável e configuração de servidor `NexFiscal:Capture:AllowControlledQuery=true` + `NexFiscal:Capture:ExclusiveConsumerConfirmed=true` após validação administrativa de handoff. Enquanto não houver migração segura do consumidor atual, não ativar as flags nem habilitar empresas. A interface informa o motivo e mantém o botão desabilitado.
- Para status 138, arquivos XML são armazenados por NSU e hash na transação que avança o cursor. Status 137/656 não avançam NSU, aplicam espera. Status de consulta são persistidos em `BRAI_NEX_CaptureLog` na mesma transação.
- Limitação deliberada: o controle de concorrência de execução é local ao processo; para agendamento multi-instância, ainda será necessário lease distribuído. Não executar várias instâncias para o mesmo CNPJ.
- Não existe processo administrativo completo de transferência de NSU entre fornecedores: esse procedimento e a validação de ambiente/credenciais devem ser concluídos antes do primeiro teste real.

### Teste imediato na Central de operação (sem consumir NSU)

- Após atualizar o frontend via PowerShell com `npm run build` e executar `NexFiscal.Web` pelo Visual Studio, a Central de operação recupera empresas do HANA.
- **Testar conexão SEFAZ** chama `POST /api/capture/test-connectivity`, usa o A1 cifrado, valida sua cadeia e consulta o WSDL por HTTPS. **Não envia uma consulta distNSU e não consome NSU.** Um HTTP 200 não garante autorização da SEFAZ para distribuir documentos.
- **Testar consulta real** só fica habilitado depois de um handoff formal com confirmação de exclusividade, empresa habilitada e checkpoint previamente autorizado. Não habilitar apenas para forçar o teste: o NexFiscal não conhece o NSU do consumidor anterior.

### Diagnóstico corrigido — Schannel Windows `0x8009030E`

Em 11/11/2026, a comunicação HTTPS com A1 retornou `HttpRequestException → IOException → AuthenticationException → Win32Exception 0x8009030E` (SEC_E_NO_CREDENTIALS). A Microsoft documenta falha conhecida do Schannel com `X509KeyStorageFlags.EphemeralKeySet` na autenticação TLS cliente: https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting#handshake-failed-with-ephemeral-keys . O `NfeCaptureCoordinator` foi atualizado para importar o A1 com `UserKeySet` no Windows, sem `PersistKeySet`, e mantê-lo disponível durante a negociação mTLS; Linux continua usando `EphemeralKeySet`. O cofre protegido original não foi alterado; a chave privada pode ter materialização temporária no perfil do usuário Windows durante a chamada, removida na liberação do certificado em .NET 8, por isso a conta que executa o serviço precisa ter perfil e permissões adequados. Não ativar consulta `distNSU` antes da migração segura do checkpoint.

### Preparação do checkpoint NF-e pela Central de operação

- Após validar HTTPS e confirmar que o fornecedor anterior deixou de consultar o mesmo CNPJ, o operador pode abrir **Preparar transferência do NSU**.
- O formulário solicita origem do último NSU, valor de 15 dígitos conferido no sistema anterior, data/hora da última consulta, CNPJ e duas confirmações explícitas. Esses dados **não são inferidos** a partir do HTTP 200. Não inserir NSU zero, aleatório ou de outra empresa.
- O endpoint local `POST /api/capture/prepare-handoff` cria uma única vez `BRAI_NEX_CaptureCursor` e grava `HANDOFF_PREPARED` na tabela `BRAI_NEX_CaptureLog` na mesma transação. Se já existe checkpoint, recusa sobrescrever. Se a empresa não está pausada ou o CNPJ não confere, recusa.
- O cadastro continua com `Enabled=0`, as flags de consulta real permanecem desligadas e **não há requisição à SEFAZ**. A confirmação feita no formulário é uma declaração, não uma verificação automática da parada do fornecedor.
- Uma autorização operacional futura ainda deve verificar a posse exclusiva do consumidor, a autenticidade do checkpoint e o intervalo desde a última consulta antes da primeira tentativa `distNSU`.

### Primeiro download manual de NF-e (11/11/2026)

- Com o consumidor anterior **desligado**, a Central de operação agora oferece **Baixar NF-e agora**. O navegador solicita confirmação explícita antes de enviar a requisição real; o usuário não informa NSU.
- `POST /api/capture/download` executa **uma chamada SOAP distNSU** por clique. Se não houver checkpoint para a empresa, grava `LastNsu=0` automaticamente; caso contrário, retoma o último NSU persistido. A operação só roda no portal local (loopback) com A1 válido.
- Para impedir consulta repetida após erro ou timeout, antes da chamada reserva `NextAllowedAtUtc=agora+1h` e registra uma tentativa em `BRAI_NEX_CaptureLog`. Se ocorrer falha de rede, **não tente novamente imediatamente**: aguarde o intervalo.
- Em `138`, grava os XMLs em `BRAI_NEX_Envelope` e avança o NSU na mesma transação; em `137`/ `656`, respeita o intervalo de 1h. O status é mostrado ao operador. A aplicação não solicita captura automática nem recupera o histórico completo além do que a SEFAZ disponibilizar pelo serviço.
- Esta liberação pressupõe que o sistema anterior foi desligado. Confirme novamente antes do clique. Não use se outro consumidor ainda estiver ativo para o mesmo CNPJ.
- Não existe interface de exportação ZIP dos documentos ainda: **download** aqui significa buscar XMLs da SEFAZ e armazenar no HANA.
