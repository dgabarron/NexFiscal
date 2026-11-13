# NexFiscal — QA do escopo de duas operações

**Documento vigente:** 13/11/2026, branch `qa/frontend-enxuto-20261113`. Referência funcional: [ESPECIFICACAO_FUNCIONAL_ATUAL.md](ESPECIFICACAO_FUNCIONAL_ATUAL.md). Referência técnica: [ARQUITETURA_E_APIS.md](ARQUITETURA_E_APIS.md).

## Operação

- [ ] Menu exibe **Operação** e **Empresas**; Configuração técnica é acessível pelo cabeçalho.
- [ ] Aba NF-e exibe **Monitoramento automático NF-e** e **Consultar todos os XMLs disponíveis**; aba CT-e exibe a consulta manual.
- [ ] Configurar X minutos, pasta do servidor e salvar; verificar persistência após atualizar página.
- [ ] Com Worker ativado, verificar que consulta segue intervalo e janela SEFAZ, sem múltiplas chamadas concorrentes.
- [ ] Com Worker desativado, manter pasta salva e confirmar que o botão de consulta manual funciona.
- [ ] Botão consulta por `companyId` e acompanha `GET /api/sefaz/nfe/catchup/{companyId}`; atualização da página recupera estado enquanto o backend estiver vivo.
- [ ] Durante cooldown NF-e, botão permanece clicável e informa a espera, sem requisitar SEFAZ.
- [ ] CT-e: habilitar, salvar pasta, consultar e verificar XMLs, erros e cooldown sem afetar cursor NF-e.
- [ ] Trocar empresa: status, configuração e histórico do job continuam vinculados ao CNPJ correto.
- [ ] Validar mensagens para `137`, `138`, `656`, checkpoint indisponível, pasta sem acesso, certificado ausente e falha HANA.
- [ ] Resultado distingue `procNFe` completo, duplicata, resumo/evento; nenhum resultado de erro aparece como sucesso.
- [ ] Confirmar que não existem campos de NSU, data retroativa, restauração de ZIP ou consulta de um único lote para usuário.

## Instalação e segurança

- [ ] HANA: testar, provisionar cinco tabelas `BRAI_NEX`, salvar; rejeitar payload incompleto e validar erro de conexão.
- [ ] A1: validar certificado, razão social, CNPJ, cadastrar empresa, sem persistir senha no navegador.
- [ ] Diagnóstico HTTPS não dispara consulta SOAP nem altera checkpoint.
- [ ] A aplicação rejeita conexão não loopback; não há autenticação multiusuário pronta.
- [ ] Não remover arquivos protegidos (`.nexfiscal-keys`, `.nexfiscal-certificates` e `.nexfiscal-hana.dat`) nas atualizações.

## Regra de escopo

Fora do Main: `consNSU` específico, recuperação retroativa, ZIP local, busca por período e NFS-e. **CT-e manual está disponível na aba CT-e**. CT-e automático ainda não foi implementado. Rotas técnicas de consulta unitária NF-e permanecem no backend sem constituir função do Main. Os endpoints de recuperação `/api/recovery/nfe/*` foram removidos na branch.

## Resultado de validação

- Build da API .NET, build Vite e testes automatizados devem continuar passando.
- **Homologação real com SAP HANA + A1 + SEFAZ: pendente.**
- **Produção: não alterada; não realizar merge/implantação até concluir QA funcional.**

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
