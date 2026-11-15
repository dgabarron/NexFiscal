# 10 — Etapa 04 (Monitoramento automático de NF-e): decisões, testes e homologação

Função 3 aprovada pelo usuário em 15/11/2026. Função 4 implementada na branch `v1/etapa-04-monitoramento`, criada a partir de `v1/etapa-03-captura-nfe`. NFS-e e manifestação do destinatário **não** fazem parte desta entrega.

## 1. O que foi entregue

- Seção **Monitoramento**: por empresa, interruptor "Monitoramento automático ativo", intervalo em minutos (1 a 10080), pasta de destino e **Salvar configuração**; mostra a próxima consulta prevista, a última execução, as contagens e a mensagem.
- Configuração gravada no HANA (migração 004, aditiva: `BRAI_NEX_NfeMonitor`), nunca no navegador.
- Execução pelo **mesmo motor e pela mesma fila** da captura manual: uma execução automática é um pedido de captura do tipo "automático". Nada de análise, gravação ou checkpoint foi duplicado.

## 2. Decisões tomadas (revisáveis)

| # | Decisão | Motivo |
|---|---|---|
| 1 | Vem **desligado**; só liga por ação explícita, e ligar exige A1 válido e pasta gravável | A01, F4 item 1 |
| 2 | **A regra da SEFAZ prevalece sobre o intervalo**: a execução só é criada quando o intervalo venceu **e** a SEFAZ já liberou a consulta; a tela mostra esse horário | A03 e ponto de aceite da F4 |
| 3 | Manual e automático **nunca competem**: toda a fila roda em série, e havendo um pedido ativo para a empresa o outro lado espera ou devolve o mesmo pedido | A06 |
| 4 | Ao reiniciar, a agenda é lida do banco: nada dispara só por ter reiniciado; execução que venceu com o serviço parado roda ao voltar | A05 |
| 5 | Depois de uma execução automática que falhou, espera mínima de **15 minutos** antes de outra; falha passageira segue a regra da etapa 3 (10 min, 3 tentativas) | A08, sem laço de consultas |
| 6 | Certificado vencido/ausente ou empresa arquivada: o monitoramento fica em espera com o motivo, **sem criar pedidos em série** | Evita milhares de registros de falha |
| 7 | Desligar encerra, sem consultar a SEFAZ, uma execução automática que estivesse aguardando; não apaga nada | F4 item 9 |
| 8 | A fila só grava a **agenda** (próxima/última execução e mensagem); ligado/desligado, intervalo e pasta só mudam pela tela | Uma alteração feita durante uma execução não é desfeita quando ela termina |
| 9 | Trocar a pasta mantém o checkpoint; os XMLs já conhecidos são recriados na pasta nova a partir do HANA | A07 |
| 10 | O serviço continua sendo a janela do PowerShell; **não foi criado serviço do Windows** | Fora do escopo aprovado. Se quiser que inicie com o Windows, é uma etapa de instalação à parte |

### Correção em função já entregue

Com as tabelas da etapa 3, **remover uma empresa** que já tivesse pedido de captura, mas ainda nenhum documento, falharia no HANA por causa das chaves estrangeiras. Agora qualquer vestígio de captura (cursor, envelope, log, inventário, lacuna ou pedido) conta como histórico — a empresa é arquivada — e a configuração de pasta e de monitoramento é removida junto com uma empresa realmente sem histórico.

## 3. Testes executados (resultados reais)

| Verificação | Resultado |
|---|---|
| `dotnet build` | OK, 0 avisos |
| `dotnet test` — 185 testes (19 novos) | 185 aprovados, 4 execuções seguidas |
| `npm ci`, `npm run build`, `npm test` — 14 testes | OK |
| Navegador (Chromium) contra o programa real com SEFAZ e HANA simulados: inicia desligado, intervalo inválido, pasta inválida, ativar, execução automática com 2 XMLs, próxima consulta em ~62 min com intervalo de 1 min, mesma pasta na captura manual, desligar mantendo arquivos, janela estreita | OK, sem erros de console — imagens em `docs/evidencias-etapa-04/` |

### Matriz A01–A08

| ID | Teste automatizado (`MonitorTests`) |
|---|---|
| A01 | Sem ativação nada dispara, mesmo após dias |
| A02 | Ativar grava a configuração por empresa; intervalo e pasta validados |
| A03 | Intervalo de 1 minuto: 59 passadas sem nenhuma consulta; consulta só após a hora da SEFAZ |
| A04 | A fila roda no backend, sem depender da tela (a tela só lê o estado) |
| A05 | Reinício recompõe a agenda; execução vencida com o serviço parado roda ao voltar |
| A06 | Manual em andamento faz o automático esperar; clique manual durante o automático devolve o mesmo pedido |
| A07 | Trocar pasta e A1 mantém NSU e histórico |
| A08 | Falha passageira espera e desiste após o limite; depois, pausa mínima antes de nova execução |

Dois defeitos de concorrência foram encontrados pelos próprios testes e corrigidos antes da entrega: novo pedido imediato após desistência (decisão 5) e configuração regravada ao fim de uma execução (decisão 8).

## 4. O que depende da sua homologação

1. Os comandos do monitoramento no **HANA real** (conferidos só estaticamente).
2. O comportamento ao longo de **horas reais** com a SEFAZ: uma execução por hora quando não há novidades, e XMLs novos aparecendo na pasta sem clique.
3. Reinício real do serviço no Windows com o monitoramento ligado.

### Roteiro

| Passo | Ação | Esperado |
|---|---|---|
| 1 | Baixe a branch `v1/etapa-04-monitoramento` em pasta nova, rode o script e clique em **Atualizar estrutura** | "1 tabela(s) criada(s)" |
| 2 | Seção **Monitoramento** | "Monitoramento automático desligado para esta empresa" |
| 3 | Ligue o interruptor, intervalo `30`, pasta `C:\NexFiscal\XML`, **Salvar configuração** | Faixa verde "Monitoramento ativo — próxima consulta prevista para…" |
| 4 | Aguarde até um minuto e atualize | Cartão "Última execução automática" com as contagens (ou a próxima consulta já no horário liberado pela SEFAZ, se você capturou há menos de 1 hora) |
| 5 | Feche o navegador, deixe o serviço aberto por mais de 1 hora e reabra | Nova "Última execução" registrada sem nenhum clique |
| 6 | Ctrl+C, rode o script de novo | Monitoramento continua ligado, com a agenda mantida |
| 7 | Desligue o interruptor e salve | "Monitoramento automático desligado. Histórico e arquivos foram mantidos." |

## 5. Critérios pendentes

- Homologação acima.
- **Manifestação do destinatário**: continua sem decisão sua; sem ela, compras não manifestadas ficam só como resumo (ver doc 09, item 2.1).
- **NFS-e nacional**: combinada para depois da NF-e; não iniciada. É outro serviço (ADN da NFS-e) e exigirá o mesmo estudo da norma antes de codificar.
- Release V1 (doc 03): validação de ponta a ponta, backup e plano de rollback, e só então proposta de merge na `main` — nada disso foi feito, e a `main` não foi tocada.
