# 12 — Etapa 06 (Ciência da Operação): norma, decisões, testes e homologação

Em 16/11/2026 o usuário delegou a escolha do próximo passo ("Faça o que acredita ser o melhor caminho"). A etapa escolhida foi a **manifestação do destinatário**, limitada ao evento **Ciência da Operação**, na branch `v1/etapa-06-manifestacao`, criada a partir de `v1/etapa-05-nfse`. É uma ampliação do escopo original e está registrada no `CLAUDE.md`.

**Por que esta etapa:** para quem é destinatário, a SEFAZ entrega apenas o resumo (`resNFe`) até haver manifestação. Sem ela, o objetivo do produto — o XML completo — não é alcançado para as notas de compra, e o operador precisava sair do NexFiscal para resolver.

## 1. Fonte normativa (conferida na fonte oficial, Portal Nacional da NF-e)

| Documento | O que foi usado |
|---|---|
| **NT 2020.001 v1.60** (23/04/2026, vigente) | Regras do evento, prazos, códigos de retorno e rejeições |
| **NT 2012.002** | Leiaute original do evento e do lote |
| Pacote de schemas **Evento_ManifestaDest_PL_v1.01** | `envEvento` 1.00, `evento`/`infEvento`, `detEvento` do tipo 210210, `retEnvEvento`, assinatura XML-DSig. Os arquivos estão em `tests/NexFiscal.Tests/Schemas/` e os testes validam contra eles |
| Página **Relação de Serviços Web** (Ambiente Nacional) | `https://www.nfe.fazenda.gov.br/NFeRecepcaoEvento4/NFeRecepcaoEvento4.asmx` |

Pontos aplicados: `cOrgao` 91 (Ambiente Nacional), `tpAmb` 1 (produção), `tpEvento` 210210, `nSeqEvento` 1, `Id = "ID" + 210210 + chave + "01"`, `descEvento` "Ciencia da Operacao", lote de até 20 eventos, assinatura RSA-SHA1 com C14N (algoritmos fixados pelo schema oficial da assinatura).

### NÃO conferido na fonte oficial

O WSDL do serviço exige certificado digital até para ser lido (HTTP 403 daqui). **A ação SOAP (`.../NFeRecepcaoEvento4/nfeRecepcaoEventoNF`) e o formato do corpo (`nfeDadosMsg` direto no `Body`) não vieram do WSDL**: foram conferidos nos nomes gravados em uma biblioteca fiscal pública amplamente usada (ZeusFiscal), sem usar o código dela. É o principal ponto a observar no primeiro envio real: se a SEFAZ devolver erro de serviço, o motivo aparece na tela e nada é dado como registrado.

## 2. Reuso de bibliotecas

O usuário pediu que se priorizasse o reuso de motores fiscais existentes. Avaliação:

- **Bibliotecas fiscais de terceiros** (ZeusFiscal/Hercules, Unimake.DFe): tecnicamente adequadas, mas os pacotes **não declaram licença verificável**. Não foram incorporadas.
- **APIs hospedadas de terceiros**: descartadas — o A1 e os XMLs sairiam do computador da empresa.
- **Reusado:** a assinatura XML-DSig do próprio .NET (`System.Security.Cryptography.Xml` 8.0.5, da Microsoft, licença MIT) e o transporte HTTPS com certificado já homologado na captura.

A manifestação fica atrás da interface `IManifestGateway`. Se uma biblioteca vier a ser aprovada (licença confirmada), ela entra como outra implementação dessa interface, sem alterar tela, regras nem banco. A distribuição de NF-e, já homologada, não foi reescrita.

## 3. Decisões

1. **Só a Ciência da Operação.** Ela libera o XML e não é conclusiva. Confirmação, desconhecimento e operação não realizada têm efeito fiscal definitivo e ficam fora do NexFiscal até decisão expressa.
2. **Nunca automática.** O envio só acontece quando o operador marca as notas, clica em **Registrar ciência da operação** e confirma no diálogo. O monitoramento automático não manifesta.
3. **Sem digitar chave.** O servidor só aceita notas que ele mesmo conhece como pendentes (resumo recebido da SEFAZ, autorizada, sem XML completo, sem ciência registrada). Qualquer outra chave enviada é ignorada.
4. **Até 100 notas por envio**, em lotes de 20. Se um lote falhar, os anteriores ficam registrados e a tela informa quantas ficaram sem resposta.
5. **Resultado por nota**, gravado em `BRAI_NEX_NfeManifest` (migração 006, aditiva):
   - 135/136 → ciência registrada;
   - 573 (duplicidade) → já estava registrada, tratada como registrada;
   - 655 (já há manifestação conclusiva) → tratada como registrada, pois o XML já está liberado;
   - 596 (fora do prazo), 650 (cancelada/denegada) e demais → recusada, com o motivo na lista; pode ser reenviada.
6. **Um registro aceito nunca é rebaixado** por uma tentativa posterior (garantido no SQL e em teste).
7. **Nada da captura é tocado**: checkpoint (NSU), inventário, envelopes e arquivos ficam como estavam. O XML completo chega pela captura normal, respeitando a espera de 1 hora da SEFAZ.
8. **CNPJ ou chave alfanuméricos**: o leiaute oficial do evento (v1.01) só aceita numéricos. Nesses casos a tela informa que a manifestação deve ser feita no portal da SEFAZ.
9. **Sigilo:** o evento leva apenas a parte pública do certificado. Senha, PFX e XML não vão para log nem para respostas da API.

## 4. Prazos (NT 2020.001, item 4)

- Ciência: **10 dias** da autorização da nota. Fora do prazo a SEFAZ responde 596 e o NexFiscal mostra "prazo para manifestação encerrado". O NexFiscal não bloqueia por data: quem decide é a SEFAZ.
- Depois da ciência, a manifestação conclusiva continua obrigatória nos casos previstos no Ajuste SINIEF 07/05, em até **90 dias** da autorização. **Isso é responsabilidade da empresa e é feito fora do NexFiscal.**

## 5. Testes automatizados

228 testes de backend e 16 de frontend. Os desta etapa (`NfeManifestTests`, `HttpApiTests`, `api.test.js`) cobrem: lote de 20 eventos válido no schema oficial; assinatura conferida com o certificado e detecção de adulteração; leitura das respostas; envio só das notas marcadas; tradução de 573/596/650/655; reenvio só das recusadas; recusa de chaves desconhecidas; lotes e falha no meio; lote não processado; empresa arquivada; logs sem dados sensíveis; SQL restrito à tabela própria, sem `DELETE`/`DROP`/`ALTER`.

Verificação no navegador com SEFAZ simulada (imagens em `docs/evidencias-etapa-06/`): botão desabilitado sem marcar; cancelar o diálogo não envia nada; falha de conexão explicada; uma nota registrada e outra recusada por prazo; situação mantida após recarregar; sem erros de console; sem rolagem horizontal em janela estreita.

**Nada disso é homologação.** Não houve envio à SEFAZ real, nem execução em SAP HANA real.

## 6. Como homologar (Windows, ambiente real)

Pré-requisito: uma empresa com notas pendentes (bloco "aguardando manifestação" na tela Captura).

1. Execute `scripts\Run-NexFiscalLocal.ps1` e, em **Conexão SAP HANA**, clique em **Atualizar estrutura** (cria só `BRAI_NEX_NfeManifest`).
2. Em **Captura → NF-e**, clique em **Ver notas pendentes**.
3. **Comece com uma única nota**, de preferência recente (menos de 10 dias) e que a empresa reconhece. Marque-a, clique em **Registrar ciência da operação** e confirme.
4. Confira a coluna **Ciência**: "Ciência registrada. Aguardando o XML completo."
5. Opcional: confirme no portal da NF-e (consulta pela chave) que o evento "Ciência da Operação" aparece na nota.
6. Quando a tela indicar que a consulta à SEFAZ está liberada, clique em **Capturar todos os XMLs disponíveis**. A nota deve sair da lista de pendentes e o arquivo `<chave>.xml` aparecer na pasta.
7. Depois repita com várias notas, incluindo uma antiga, para ver a mensagem de prazo encerrado.

### Critérios de aceite

- [ ] "Atualizar estrutura" cria a tabela nova sem alterar dados.
- [ ] Nenhuma ciência é enviada sem marcar notas e confirmar.
- [ ] A SEFAZ real aceita o evento (ciência registrada na tela e no portal).
- [ ] O XML completo da nota chega na captura seguinte e é gravado na pasta.
- [ ] Recusas da SEFAZ aparecem com motivo compreensível.
- [ ] Mensagens em português, sem dados sensíveis.

Se o primeiro envio falhar com erro de serviço, anote a mensagem exibida (ela não contém dados sensíveis): é o indício de que a ação SOAP ou o formato do corpo precisam de ajuste (item 1).

## 7. Volta para a versão anterior

A tabela nova não interfere nas versões anteriores, que simplesmente a ignoram. As ciências já registradas continuam valendo na SEFAZ — o evento não pode ser desfeito.
