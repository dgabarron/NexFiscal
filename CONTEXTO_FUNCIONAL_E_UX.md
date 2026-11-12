# NexFiscal — contexto funcional e de usabilidade para redesign
**Data:** 12/11/2026
**Fonte:** código da branch `feat/nfe-capture-core`, commit `81f8a0d2b2723cb2b8c394a2dc2de04921f9e6db`.
**Produto:** NexFiscal, solução genérica para empresas que utilizam SAP Business One (SAP B1). Não assumir nome, CNPJ ou configurações de uma empresa específica. Identificador interno: `BRAI_NEX`.

## 1. O que é
NexFiscal é um portal administrativo e operacional que cadastra empresas pelo certificado A1, configura a conexão SAP HANA, consulta serviços oficiais de distribuição de documentos fiscais eletrônicos, salva XMLs individuais e acompanha checkpoints NSU. O sistema é executado em infraestrutura própria (on-premises). A interface atual é um portal React em português brasileiro integrado a um backend ASP.NET Core (.NET 8).

Público: analista fiscal/contábil, analista de TI responsável pela instalação, operador de integração ERP e administrador. A pessoa fiscal precisa saber **qual empresa está sendo acompanhada, se o serviço está operando, quando ocorrerá a próxima consulta, quantos documentos foram recebidos, quais exigem atenção e como recuperar XMLs sem comprometer o monitoramento**. O administrador de TI precisa resolver configuração de HANA, certificado A1 e falhas de infraestrutura sem expor segredos.

Escopo atual: NF-e modelo 55 como fluxo mais completo; CT-e possui consulta manual separada; NFS-e nacional está no planejamento, com configurações de agenda que NÃO significam motor automático entregue. Não criar dados ou ações fictícias.

## 2. Arquitetura real
- Frontend React 18, Vite 5, Tailwind CSS 4, ícones lucide-react, pequenos componentes locais de UI inspirados em shadcn/ui. Arquivo central: `frontend/src/main.jsx`. Navegação atual de três etapas: **Conexão SAP HANA → Empresa/Certificado A1 → Central de operação**.
- Backend ASP.NET Core 8 serve arquivos gerados e expõe APIs sob `/api`.
- SAP HANA persiste empresas, cursor NF-e, envelopes XML e registros de captura no schema `BRAI_NEX`; outras informações de instalação/certificados são protegidas no servidor. A integração futura com SAP B1 não deve ser simulada como se já estivesse entregue.
- Certificado A1 (PFX/P12 e senha) é inspecionado no backend. Não armazenar senha ou PFX no navegador, `localStorage`, logs ou mocks distribuídos.
- O portal de configuração atual **só permite acesso local (loopback)**, sem autenticação multiusuário pronta. A implementação futura exige autenticação e autorização no backend antes de disponibilização em rede; não “resolver” segurança apenas escondendo botões.

## 3. Experiência de uso que desejamos
A tela principal deve abrir com uma **Central de operação**, não com um wizard permanente, após a instalação inicial. Exibir estado geral do sistema, empresas monitoradas, consultas recentes, janela fiscal de próxima tentativa e alertas acionáveis. Separar áreas:
1. **Painel operacional:** visão geral real dos estados disponíveis; carregamento/erro/vazio/atualizado em; filtros por empresa e tipo de documento; nenhuma informação inventada.
2. **Empresas e certificado A1:** cadastro/inspeção; CNPJ e razão social provenientes do A1; validade, certificado ausente/expirado; status operacional; não deixar credenciais persistidas na UI.
3. **Monitoramento automático NF-e:** serviço contínuo do servidor; ativar/desativar e editar intervalo e diretório; exibir checkpoint, próximo horário possível, último resultado e bloqueios. O processo fica ativo, mas consultas respeitam cooldown e restrições da SEFAZ. Não confundir timer visual com scheduler.
4. **Sincronização imediata NF-e (distNSU):** ação pontual para percorrer lotes disponíveis a partir de `LastNsu` atual. Compartilha cursor do monitoramento; não recua nem zera NSU. Pode ter job e estado de progresso.
5. **Recuperação retroativa NF-e (consNSU):** ação **separada** para consultar lacunas detectadas entre NSUs já armazenados. Preserva `LastNsu` do monitoramento. Não é varredura regressiva universal, não recupera todo o histórico desde a fundação e não é rotina permanente.
6. **Recuperação local de ZIPs:** outra função distinta; apenas identifica e restaura documentos previamente arquivados localmente, sem consulta à SEFAZ. A tela atual possui seleção de data para este recurso; essa data **não** é um filtro de consulta da SEFAZ.
7. **CT-e:** consulta manual e cursor independentes de NF-e, com XML `procCTe`. Não mostrar Worker automático CT-e como pronto.
8. **NFS-e nacional:** mostrar como funcionalidade ainda em desenvolvimento, sem simular captura ou sucesso.
9. **Configurações técnicas:** conexão HANA e verificação de estrutura; diretórios por tipo, certificados, diagnósticos e parâmetros, em área administrativa separada.

## 4. Jornada de instalação
1. Operador técnico informa host/porta, usuário e senha do HANA.
2. Testa conexão e valida schema `BRAI_NEX`; se necessário, aciona explicitamente preparação das cinco tabelas.
3. Salva credenciais pelo backend, sem voltar a mostrá-las.
4. Seleciona certificado A1 e informa senha para inspeção.
5. Confere CNPJ, razão social, validade e emissor encontrados no A1. O cadastro usa esses dados, não exige digitação manual de CNPJ, e não inicia captura automaticamente.
6. Configura diretório(s) **existentes no servidor** para XML; configura captura NF-e e intervalo; inicia operação. Explicar que o caminho não é uma pasta local do computador do usuário quando a aplicação roda remotamente.
7. Primeira consulta SEFAZ ocorre somente quando requisitos de checkpoint e janela fiscal são atendidos.
Projetar onboarding claro com passos concluídos e pré-requisitos; depois de concluído, administração não deve bloquear a central operacional.

## 5. Diferenças essenciais — NÃO UNIFICAR
| Função | Disparo | Fonte | Identificação | Checkpoint |
|---|---|---|---|---|
| Monitoramento NF-e | Worker periódico no servidor | SEFAZ NF-e | `distNSU` sequencial | Avança `LastNsu` normalmente |
| Sincronização imediata NF-e | Manual e pontual | SEFAZ NF-e | `distNSU` sequencial até disponibilidade | Mesmo cursor do monitoramento |
| Recuperação retroativa NF-e | Manual, uma lacuna conhecida por acionamento | SEFAZ NF-e | `consNSU` específico | **Não** altera `LastNsu` |
| Restauração local | Manual | ZIPs/arquivos já armazenados | Busca no acervo local | Não consulta a SEFAZ |
| Consulta CT-e | Manual | SEFAZ CT-e | Cursor exclusivo CT-e | Não compartilha NSU NF-e |
| NFS-e | Futuro | ADN nacional | A confirmar pela implementação | Não mostrar como ativo |

**Status do código na data de empacotamento:** `NfeMonitoringWorker` executa a cada 30 segundos para verificar se há empresa elegível, **não** para fazer requisição SEFAZ a cada 30 segundos; usa os intervalos configurados e o horário `NextAllowedAtUtc` do HANA. Estado de tentativa do Worker é parcialmente em memória; exigir estado/log persistido e lease distribuído é melhoria futura. `NfeRetroactiveRecovery` consulta lacunas entre envelopes existentes, não reconstitui NSUs anteriores ao primeiro envelope conhecido. `NfeSefazCatchup` possui estado de execução em memória; não exibir como tarefa durável entre reinícios. Uma reserva conservadora de uma hora pode impedir consultas concorrentes de NF-e. O backend é a fonte da verdade; o frontend não pode prometer algo não implementado.

## 6. Regras fiscais e de dados que UX não pode mudar
- Apenas XMLs completos de NF-e (`procNFe`) são gravados como XML individual de NF-e na pasta de destino; resumos e eventos não devem aparecer como notas completas. CT-e completo é `procCTe`.
- ZIPs/envelopes brutos preservados servem a diagnóstico e recuperação.
- Não sobrescrever XML existente com hash diferente. Mostrar duplicidade como “já existente”, não erro de captura genérico.
- `cStat=138`: documentos retornados; `137`: sem novos documentos; `656`: consumo indevido/bloqueio, respeitar espera; outros status devem ser apresentados como erros/rejeições apropriados.
- `ultNSU` e `maxNSU` são cursores, NÃO quantidades de notas fiscais. Não exibir “maxNSU = 1.000 = 1.000 notas”.
- O limite de disponibilidade é imposto pelo órgão fiscal. O NexFiscal **não garante todo histórico empresarial** ou documentos fora da janela de distribuição.
- A captura só avança checkpoints depois das garantias de armazenamento previstas. Não mover/zerar NSU na interface.
- A UI não deve repetir requisições automaticamente por erro, nem contornar `NextAllowedAtUtc`. Exibir horário local com origem UTC e fuso explicitamente.
- Diferenciar “sem XML completo”, “nenhum documento novo”, “SEFAZ indisponível”, “aguardando cooldown”, “falha ao salvar na pasta”, “certificado inválido”, “checkpoints não preparados”, “captura desativada” e “recuperação sem lacunas conhecidas”.
- Preservar funcionalidades já existentes e validações do backend; nenhum processo de recuperação pode avançar o checkpoint do monitoramento.

## 7. Conteúdo recomendado para a Central de operação
Barra superior: NexFiscal, ambiente, empresa ativa, atualização mais recente, indicação de acesso administrativo. Navegação lateral enxuta: Visão geral, Empresas, Capturas, Recuperação, Histórico/Diagnóstico, Configurações. O conteúdo pode ser adaptado ao que as APIs atuais realmente fornecem.

**Cards de status por empresa:** razão social, CNPJ formatado/mascarado conforme perfil, certificado A1 válido/expirando, monitoramento NF-e ligado/desligado, último NSU, último contato com SEFAZ, próxima consulta elegível, XMLs completos armazenados (se API disponível) e estado do serviço. Não inventar totais de CT-e/NFS-e nem KPIs sem fonte real.

**Fluxos guiados:** configurar pasta, validar certificado, ativar captura, executar sincronização pontual, inspecionar recuperação de ZIP, retroagir uma lacuna. Mostrar pré-requisitos e motivos quando botões estiverem desabilitados. Modal de confirmação apenas para ações fiscais que realmente consomem janela/cota; uma confirmação genérica repetitiva reduz usabilidade.

**Feedback:** estados loading/success/empty/error reais; notificação não intrusiva; detalhes técnicos expansíveis; histórico operacional e cópia de detalhes para suporte; progressivo responsivo. Exibir `JobStatus` apenas se retornado pelo backend; não inventar porcentagens de progresso.

## 8. Prioridades de redesign
**P0 — arquitetura da informação:** sair da tela única longa, manter onboarding isolado e separar Operação / Recuperação / Configurações. Destacar monitoramento x retroagir.
**P0 — segurança/contratos:** não alterar endpoints, payloads, métodos HTTP, semântica de cStat e lógica fiscal. Proteção de PFX, senhas, HANA e caminhos.
**P1 — clareza:** rótulos acessíveis para público fiscal (“Último NSU recebido”, “Próxima consulta permitida”), ajuda contextual com termos técnicos, distinção dos tipos de XML.
**P1 — ação e diagnóstico:** explicar bloqueios e ações disponíveis sem reduzir informação crítica.
**P1 — responsividade e acessibilidade:** teclado, foco visível, contraste WCAG AA, formulários acessíveis, retorno de erros próximo ao campo, desktop-first com tablet utilizável.
**P2 — qualidade de código:** extrair `main.jsx` em páginas, hooks de API, componentes e estados reutilizáveis; evitar duplicação; criar testes de fluxos e formatação.
**P2 — visual:** aparência de sistema corporativo com consistência tipográfica, spacing, hierarquia e componentes. IBM Carbon pode servir como referência de legibilidade e densidade; preservar tecnologia atual, sem dependências novas pesadas ou reescrita de backend.

## 9. Critérios de aceite
1. Operador consegue descobrir em segundos se NF-e está ativa, quando poderá consultar e qual erro impede a operação.
2. É impossível confundir **monitorar** com **retroagir** ou com **restaurar ZIP**.
3. Onboarding HANA/A1 não ocupa o espaço do operador depois da configuração.
4. Todas as ações reais continuam enviando exatamente os contratos existentes, sem mocks no fluxo de produção.
5. Campos sensíveis nunca ficam gravados no browser, em URL ou código.
6. Estados de vazio/erro/bloqueio/progresso são legíveis, úteis e compatíveis com as respostas.
7. Interface em português do Brasil e adaptável à empresa (produto genérico).
8. Frontend compila com `npm install && npm run build` dentro de `frontend/`. Registrar eventuais novos pacotes e testes.
9. Fornecer código completo executável, não só mockups ou screenshots. **Não alterar lógica fiscal no backend**.
10. Identificar lacunas de backend como propostas, não fingir que APIs de autenticação, relatórios ou histórico já existem.

## 10. Condições de execução
- O NexFiscal encontra-se em desenvolvimento/QA, não homologado operacionalmente nesta entrega.
- UI atual acessível apenas por `127.0.0.1` nas configurações de segurança do backend. A intenção futura de acesso em rede requer implementação de autenticação (AD/LDAPS) e TLS/autorização pelo backend.
- Diretórios de destino e certificados pertencem ao servidor. Nunca levar segredos para o design.
- Não confundir camada intermediária SAP HANA com integração contábil/fiscal definitiva no SAP Business One.
