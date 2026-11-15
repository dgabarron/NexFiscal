# 08 — Etapa 02 (Empresas e certificado A1): decisões, testes e homologação

Função 1 aprovada pelo usuário em 15/11/2026. Função 2 implementada na branch `v1/etapa-02-empresas-a1`, criada a partir de `v1/etapa-01-hana`. Nenhum código da branch de QA antiga foi importado.

## 1. O que foi entregue

- Aba **Empresas**: adicionar, validar certificado, prévia, cadastrar, lista com estado do A1, atualizar A1, remover/arquivar, reativar, selecionar.
- Leitura do PFX/P12 no backend: senha, chave privada, CNPJ (ICP-Brasil, OID 2.16.76.1.3.3 no *Subject Alternative Name*, com o nome do titular como reserva), razão social, UF quando presente, emissor e validade.
- Cofre local cifrado `.nexfiscal-certs\<id>.a1.dat` (mesmas chaves DPAPI da Função 1).
- Migração 002, **aditiva**: tabela `BRAI_NEX_CompanyCertificate` (UF, emissor, impressão digital, validade, arquivamento). `BRAI_NEX_Company` não foi alterada.
- Botão **Atualizar estrutura** para quem vem da etapa 1.

## 2. Decisões tomadas (revisáveis)

| # | Decisão | Motivo |
|---|---|---|
| 1 | Ambiente fixo **Produção** (`Environment = 1`) | A especificação não prevê escolha de ambiente e a V1 captura documentos reais. Se você quiser homologação SEFAZ na etapa 3, avise |
| 2 | Certificado **vencido não pode ser cadastrado nem usado em atualização** | Não serve para captura. O estado "vencido" aparece quando um A1 já guardado expira |
| 3 | Com menos de 30 dias de validade, o selo vira "A1 vence em N dia(s)" | Aviso antecipado; não bloqueia nada |
| 4 | **Cadeia e revogação (LCR/OCSP) não são verificadas** | A especificação as coloca "em operação separada quando aplicável"; exigiria acesso externo. A SEFAZ recusará um A1 revogado na etapa 3 |
| 5 | CNPJ já cadastrado **arquivado** não vira empresa nova: usa-se **Reativar com A1** no mesmo cadastro | Preserva identidade, NSU e histórico |
| 6 | A1 guardado **ilegível** só é substituído com confirmação; o arquivo antigo vira backup | Mesma regra da configuração HANA |
| 7 | A cada troca de A1 o arquivo anterior fica como `.anterior-<data>.bak` | Reversão manual possível; só é apagado junto com a empresa sem histórico |
| 8 | Remoção de empresa sem histórico apaga o cadastro (2 `DELETE` por `CompanyId`) e os arquivos do cofre daquela empresa | Previsto na especificação F2. Histórico = existir cursor, envelope ou log de captura da empresa |
| 9 | Arquivo enviado em Base64 dentro de JSON, limite de 256 KB | Mantém a API apenas JSON; alterações vindas de outro site no navegador são recusadas (cabeçalho `Origin`) |
| 10 | "Selecionar" é guardado no navegador | Só há uso real na etapa 3; não é dado fiscal |
| 11 | Aceita CNPJ alfanumérico (12 posições A–Z/0–9 + 2 dígitos) | Formato novo da Receita Federal |
| 12 | Empresas que já existam em `BRAI_NEX_Company` (versão antiga) aparecem na lista como "A1 não encontrado" | Não são validadas nem alteradas; basta enviar o A1 em "Atualizar A1" se quiser usá-las |

### Interface refeita em Microsoft Fluent 2 (pedido do usuário em 15/11/2026)

Toda a interface foi reescrita com **Fluent UI React v9**, a implementação oficial do Fluent 2, sem mudar nenhuma função nem a API:

- Navegação lateral com as seções **Conexão SAP HANA** (com indicador de estado) e **Empresas**.
- Estados e resultados em faixas de mensagem do Fluent (sucesso, aviso, erro), anunciadas por leitores de tela.
- Conexão já salva aparece como resumo, com **Alterar conexão**; o formulário só abre quando necessário.
- Envio do A1 e confirmação de remoção em diálogos modais, operáveis por teclado (Enter confirma, Esc fecha).
- Dica permanente do que falta para habilitar **Salvar configuração**; validação de campo no próprio campo.
- Tema claro/escuro conforme o Windows; fonte Segoe UI no Windows.
- `lucide-react` e o CSS próprio foram removidos; os estilos usam os *tokens* do Fluent.

Produto exclusivo para Windows e para o SAP HANA do SAP Business One: os textos da tela foram ajustados a isso (porta padrão 30015 do SAP B1).

Ordem de gravação: cofre primeiro, HANA depois. Se o HANA falhar no cadastro, o arquivo recém-criado é removido; se falhar na atualização, o certificado anterior volta ao lugar. Depois de gravar, o A1 é relido do cofre e comparado.

## 3. Testes executados (resultados reais)

| Verificação | Resultado |
|---|---|
| `dotnet build` | OK, 0 avisos |
| `dotnet test` — 110 testes (58 da função 1 + 52 novos) | 110 aprovados |
| `npm ci`, `npm run build`, `npm test` — 9 testes | OK |
| Navegador (Chromium) na interface Fluent 2, contra o programa real com HANA simulado: arquivo inválido, senha errada, prévia, cadastro, duplicidade, fechar com Esc, segunda empresa, seleção após recarregar, A1 de outro CNPJ, renovação com backup, remoção, arquivamento, reativação, alterar conexão HANA, configuração ilegível, atualização de estrutura, janela estreita e tema escuro | OK, sem erros nem avisos de console — imagens em `docs/evidencias-etapa-02/` |
| Arquivo do cofre | Não contém senha nem PFX em claro |
| Processo real sem HANA: `/api/companies` | 409 "Configure e valide a conexão SAP HANA…" |

### Matriz C01–C10

| ID | Situação |
|---|---|
| C01, C02, C03, C06, C07, C08, C09, C10 | Verificados com certificados de teste e repositório em memória |
| C04 | Verificado recriando o serviço sobre a mesma pasta; **reinício real no Windows depende de você** |
| C05 | Verificado simulando perda das chaves; **DPAPI com outra conta Windows não testado** |

## 4. O que depende da sua homologação

Não tive acesso a SAP HANA real, a um certificado A1 real nem a Windows. Especificamente:

1. **Leitura do seu A1 real**: CNPJ, razão social, UF, emissor e validade corretos na prévia. Os certificados de teste seguem o padrão ICP-Brasil, mas só um e-CNPJ real confirma.
2. **SQL de empresas no HANA real** (`INSERT`/`UPDATE`/`DELETE`/`SELECT` com parâmetros e transação). Aqui só foi conferido estaticamente contra as migrações; nunca executou em HANA.
3. **Atualizar estrutura** criando `BRAI_NEX_CompanyCertificate` no seu banco.
4. Reinício no Windows com a mesma conta (A1 continua "guardado e válido").
5. Carregamento do PFX no Windows sem instalar nada no repositório de certificados.
6. Aparência da interface Fluent 2 no seu Windows (Edge/Chrome), em tema claro e escuro. Aqui foi verificada em Chromium no Linux, sem a fonte Segoe UI.

### Roteiro

| Passo | Ação | Esperado |
|---|---|---|
| 1 | Baixe a branch `v1/etapa-02-empresas-a1` em pasta nova e rode o script | Abre na seção Conexão SAP HANA com "estrutura a revalidar" |
| 2 | **Atualizar estrutura** | "Estrutura atualizada: 1 tabela(s) criada(s)"; faixa verde |
| 3 | Seção **Empresas** → Adicionar empresa → A1 com **senha errada** → Validar | "Senha do certificado incorreta" |
| 4 | Senha correta → Validar | Prévia com CNPJ, razão social, UF, emissor e validade do seu certificado |
| 5 | **Cadastrar empresa** | Empresa na lista com selo "A1 guardado e válido" |
| 6 | Adicionar a mesma empresa de novo | Aviso de CNPJ já cadastrado; sem botão de cadastrar |
| 7 | Ctrl+C, rode o script de novo, abra Empresas | Empresa continua "A1 guardado e válido" |
| 8 | **Atualizar A1** com o mesmo certificado | "Certificado A1 atualizado… preservados" |
| 9 | (Se tiver A1 de outra empresa) Atualizar A1 com ele | Bloqueado: "outro CNPJ" |
| 10 | (Opcional) Cadastre uma segunda empresa e use **Remover empresa** | Aviso de exclusão definitiva; após confirmar, some da lista |

Confira os passos 4, 5 e 7 com atenção. **Não me envie o arquivo PFX nem a senha**; se algo sair errado, basta o texto da mensagem ou um print da tela.

## 5. Critérios pendentes

- Homologação com A1 e HANA reais (acima).
- Verificação de cadeia/revogação: fora desta entrega (decisão 4).
- Função 3 **não iniciada**; aguarda o seu "APROVADO" da etapa 2. Antes de codificá-la será validada a Nota Técnica vigente do NFeDistribuicaoDFe, como exige o doc 05.
