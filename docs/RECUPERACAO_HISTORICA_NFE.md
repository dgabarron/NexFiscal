# NexFiscal — recuperação histórica de XMLs completos NF-e

## Objetivo operacional

Recuperar **todos os XMLs completos `procNFe` disponíveis nas fontes autorizadas** da empresa, desde a data inicial escolhida em **DD/MM/AAAA** até a data atual, independentemente de terem ou não sido capturados antes pelo NexFiscal. Cada XML individual é identificado pela **chave de acesso de 44 dígitos**, com nome `CHAVE.xml`, sem sobrescrever documento existente.

## Implementação desta versão

- Nova seção **Recuperação histórica de XMLs — NF-e** no painel de operação.
- Data inicial editável, com padrão 01/01/1900 (para incluir histórico de empresas antigas; o operador pode escolher a data de abertura).
- Campo de **até 12 pastas históricas adicionais**, uma por linha, cujos caminhos devem existir **no servidor NexFiscal** e ser acessíveis à conta de execução. Aceita pastas de exportação do ERP, backups locais ou pastas compartilhadas montadas na rede. O programa não coleta arquivos diretamente do banco SAP Business One.
- Fontes sempre incluídas: pasta de destino NF-e configurada e ZIPs internos do NexFiscal; fontes adicionais: XML e ZIP com XMLs completos, incluindo subpastas.
- Importação automática em operação separada da consulta SEFAZ, com progresso e totais de XMLs novos, já existentes, conflitantes, inválidos, pertencentes a outro CNPJ ou fora da data.
- Só aceita XML com estrutura `nfeProc`, elementos `NFe/infNFe` e `protNFe/infProt`, chave de 44 dígitos coincidente entre documento e protocolo e CNPJ da empresa (emitente ou destinatária) correspondente ao certificado A1 cadastrado. Mantém o hash íntegro e não regrava XML existente, mesmo diante de conteúdo diferente.
- Proteção contra entidades XML externas, tamanho descompactado acima de 10 MB por documento, symlinks e explosões de arquivos ZIP. O processo interrompe a varredura ao superar 200 mil arquivos ou 150 mil entradas XML em ZIP e informa **cobertura incompleta**.
- `POST /api/history/nfe/start` recebe `{companyId,fromDate:"01/01/1900",sourceDirectories:["D:\\\\Historico"]}`; `GET /api/history/nfe/status/{companyId}` informa progresso. A tarefa continua no processo do servidor após o término da requisição HTTP, mas para quando o serviço é desligado. Resultados finais são salvos em `.nexfiscal-historical/<companyId>.json`.
- A restauração anterior de XMLs já disponíveis em ZIP foi preservada na API interna; a interface agora oferece a ação geral de recuperação histórica.

## Limite fiscal e status real

**Isto não é uma busca irrestrita por data na SEFAZ.** A distribuição nacional NF-e não fornece uma API que liste todo o histórico da empresa desde sua constituição. A disponibilidade retroativa no serviço é limitada, geralmente a 90 dias, e depende dos requisitos fiscais. A consulta normal `distNSU` do NexFiscal continua separada e usa o cursor vigente, sem reset. Não afirmar que “todas as notas da empresa foram encontradas” quando as fontes acessíveis não permitem comprovar isso.

Para alcançar documentos de muitos anos atrás, é necessário disponibilizar arquivos antigos de fornecedores, exportações do SAP Business One, GED ou backups fiscais em uma pasta acessível ao NexFiscal. A integração direta com repositórios externos / SAP e a descoberta de documentos que nunca foram guardados em fonte alguma **não estão implementadas**.

## Teste

1. Preserve `.nexfiscal-certificates`, `.nexfiscal-keys`, `.nexfiscal-downloads`, `.nexfiscal-schedules` e os checkpoints HANA. Os XMLs existentes nunca são apagados.
2. Em operação, confirme que a captura NF-e está ativada e possui pasta de destino salva.
3. Informe a data de abertura da empresa em **Desde (DD/MM/AAAA)**.
4. Para arquivos fora da pasta principal, informe os caminhos absolutos das pastas históricas no **servidor**, um por linha.
5. Clique **Recuperar todos os XMLs possíveis** e acompanhe os totais. Os XMLs válidos e inéditos serão gravados no destino como `CHAVE.xml`.
6. Repetir a operação sobre as mesmas fontes deve manter os arquivos íntegros e não gerar duplicatas.

**Importante:** “Concluído” significa que o scanner terminou as fontes configuradas. Não significa que foram recuperadas todas as NF-e emitidas desde a abertura da empresa.
