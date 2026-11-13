# QA — NexFiscal frontend enxuto

Data: 13/11/2026
Base: `qa/deepseek-frontend-20261113`.
Branch de teste: `qa/frontend-enxuto-20261113`.

## Alterações de interface
- Menu principal reduzido de seis para quatro áreas: **Painel, Empresas, Capturas, Recuperação**.
- **Capturas** tem abas NF-e e CT-e com um único seletor de empresa. Cada tipo continua usando seu endpoint e cursor próprios.
- **Configuração técnica** fica acessível pelo botão na barra superior e não integra o menu principal. Esta navegação **não implementa controle de acesso**; autenticação e autorização ainda dependem de backend.
- Diagnóstico de conexão SEFAZ removido do painel e movido para Configuração técnica, com escolha da empresa. O teste de conectividade não faz consulta SOAP de distribuição.
- Removido da interface o botão de consulta manual de um lote NF-e. Mantidos no backend `POST /api/capture/download` e os demais endpoints fiscais, para compatibilidade/diagnóstico.
- CT-e continua com habilitação, pasta de XML e consulta manual. O campo de intervalo CT-e foi ocultado porque ainda não existe Worker CT-e; seu valor continua no payload de agenda para preservar o contrato `NFE/CTE/NFSE`.
- Nenhuma tela NFS-e é apresentada como funcional. A configuração interna NFS-e permanece para manter a API existente.
- ZIPs locais aparecem somente em **Opções avançadas** dentro da Recuperação, distintos da consulta retroativa `consNSU`.
- Indicadores do Painel resumidos; detalhes de NSU, cStat, resumos/eventos ficam em áreas expansíveis quando necessários.
- Textos técnicos reduzidos sem apagar avisos de indisponibilidade SEFAZ, checkpoint ou erro de captura.

## Serviços e contratos preservados
- `NfeMonitoringWorker`: monitoramento automático NF-e no servidor.
- `POST /api/sefaz/nfe/catchup`: sincronização completa dos lotes disponíveis a partir do cursor.
- `POST /api/recovery/nfe/sefaz/{companyId}`: uma lacuna histórica conhecida por consNSU.
- `GET /api/recovery/nfe/preview/{companyId}` e `POST /api/recovery/nfe/restore`: restauração local de ZIPs, sem SEFAZ.
- `POST /api/cte/download`: consulta CT-e manual, cursor independente.
- `POST /api/capture/schedules/{companyId}`: envia os três tipos exigidos pelo backend; nenhum contrato removido.

## Checklist de QA obrigatório
- [ ] Instalação HANA e cadastro A1 de primeira empresa.
- [ ] Navegação desktop e mobile com quatro áreas e Configuração técnica na barra superior.
- [ ] Trocar de CNPJ e confirmar que o seletor compartilhado atualiza ambas as abas corretamente.
- [ ] Salvar NF-e ativa, pasta no servidor e intervalo; confirmar dados persistidos ao recarregar.
- [ ] Verificar que sincronização por distNSU avança checkpoint conforme backend, sem repetir durante cooldown.
- [ ] CT-e: ativar, informar pasta, salvar e consultar manualmente, sem exibir Worker CT-e ou campo de intervalo.
- [ ] Retroação: consultar lacuna elegível; checkpoint do monitoramento não pode ser alterado.
- [ ] Reabrir ZIP nas opções avançadas; preview precisa ser refeita após mudar empresa/data.
- [ ] Validar diagnóstico SEFAZ na Configuração técnica por empresa; não executar SOAP por esse botão.
- [ ] Validar estados 137, 138 e 656, erro de certificado, HANA indisponível e diretório inválido.
- [ ] Confirmar ausência de UI NFS-e ativa, sem quebrar a configuração de agenda existente.
- [ ] Testar responsividade, teclado, foco e visibilidade de feedback.
- [ ] Não implantar na produção antes de validar HANA/A1/SEFAZ em QA.

## Validação técnica automatizada
- `dotnet test tests/NexFiscal.Capture.Tests/NexFiscal.Capture.Tests.csproj -c Release`
- `dotnet build src/NexFiscal.Web/NexFiscal.Web.csproj -c Release`
- `cd frontend && npm install && npm run build && npm test`

Não foram alterados serviços .NET, banco de dados ou credenciais. Apenas frontend e documentação de QA.
