# NexFiscal — Escopo funcional aprovado para QA
Data: 13/11/2026
Branch: qa/frontend-enxuto-20261113

## Apenas duas operações de captura NF-e
1. **Monitoramento automático NF-e**: serviço permanente no servidor, `NfeMonitoringWorker`, respeita agendamento e janela SEFAZ; pode estar desativado sem impedir consultas manuais.
2. **Consultar todos os XMLs disponíveis**: ação manual `POST /api/sefaz/nfe/catchup`, usa `distNSU` sequencial a partir do checkpoint armazenado, percorre os lotes disponíveis. É independente de `Enabled` do monitoramento e exige pasta válida/credencial A1. Não reinicia NSU.

O portal continua com duas áreas: **Operação** (ambas as funções) e **Empresas** (cadastro A1); HANA e conectividade ficam na configuração técnica do cabeçalho.

## Explicitamente fora do produto
- Recuperação retroativa `consNSU`, tentativa de preencher lacunas, ajuste manual de NSU.
- Restauração local de ZIP, varredura por data, prévia e recuperação de XMLs de diretórios antigos.
- CT-e e NFS-e não são opções do Main nesta etapa. Os serviços CT-e de backend são legados preservados separadamente nesta branch para evitar remoção destrutiva de módulos independentes.

Removidos da API: `POST /api/recovery/nfe/sefaz/{companyId}`, `GET /api/recovery/nfe/preview/{companyId}`, `POST /api/recovery/nfe/restore`.
Mantido `GET /api/capture/archive/{companyId}/{archiveId}` exclusivamente como diagnóstico técnico de um lote que o próprio NexFiscal recebeu; `NfeXmlArchive` continua como cópia de segurança, não como recurso de restauração do usuário.

## Regras que devem continuar verdadeiras
- O manual consulta somente os lotes disponibilizados pela SEFAZ e **não garante XML desde a fundação** nem `procNFe` para cada evento/resumo.
- XMLs individuais são gravados somente quando a SEFAZ retorna `procNFe` completo. O backend mantém arquivo bruto interno para evitar perda caso falhe a persistência.
- `LastNsu` e janela fiscal permanecem compartilhados entre manual e Worker. Desligar Worker não apaga nem recua NSU.
- HANA multiempresa e certificado A1 continuam obrigatórios; nenhuma exposição na rede ou deploy de produção.
- `/api/capture/schedules/{companyId}` preserva contrato atual (NFE, CTE, NFSE) mas as configurações extras não aparecem na UI.

## Checklist QA
- [ ] Monitoramento desligado: configurar diretório, salvar e executar consulta manual com sucesso.
- [ ] Monitoramento ligado: consultar em intervalo; manual compartilha `LastNsu` e cooldown.
- [ ] Recarregar página durante sincronização: restaurar o status do job em execução.
- [ ] Ausência de ações ZIP, consNSU, CT-e, NFS-e na interface principal.
- [ ] `POST /api/recovery/nfe/sefaz/{companyId}`, preview e restore não registrados na API.
- [ ] Persistência de arquivos HANA/pasta, certificados e comportamento de `137`, `138`, `656`.
- [ ] Testar SAP HANA, certificado A1 e SEFAZ **em ambiente de QA** antes de liberar instalação.
