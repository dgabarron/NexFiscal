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
