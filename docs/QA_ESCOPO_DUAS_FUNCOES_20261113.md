# NexFiscal — QA do escopo de duas operações

**Documento vigente:** 13/11/2026, branch `qa/frontend-enxuto-20261113`. Referência funcional: [ESPECIFICACAO_FUNCIONAL_ATUAL.md](ESPECIFICACAO_FUNCIONAL_ATUAL.md). Referência técnica: [ARQUITETURA_E_APIS.md](ARQUITETURA_E_APIS.md).

## Operação

- [ ] Menu exibe **Operação** e **Empresas**; Configuração técnica é acessível pelo cabeçalho.
- [ ] Main exibe somente **Monitoramento automático NF-e** e **Consultar todos os XMLs disponíveis**.
- [ ] Configurar X minutos, pasta do servidor e salvar; verificar persistência após atualizar página.
- [ ] Com Worker ativado, verificar que consulta segue intervalo e janela SEFAZ, sem múltiplas chamadas concorrentes.
- [ ] Com Worker desativado, manter pasta salva e confirmar que o botão de consulta manual funciona.
- [ ] Botão consulta por `companyId` e acompanha `GET /api/sefaz/nfe/catchup/{companyId}`; atualização da página recupera estado enquanto o backend estiver vivo.
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

Fora do Main e das funções de captura atuais: `consNSU` por NSU específico, recuperação retroativa, ZIP local, busca por período, CT-e, NFS-e. O backend contém rotas técnicas legadas de CT-e e captura unitária, que não ampliam o escopo. Os endpoints de recuperação `/api/recovery/nfe/*` foram removidos na branch.

## Resultado de validação

- Build da API .NET, build Vite e testes automatizados devem continuar passando.
- **Homologação real com SAP HANA + A1 + SEFAZ: pendente.**
- **Produção: não alterada; não realizar merge/implantação até concluir QA funcional.**
