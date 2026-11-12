# INSTRUÇÕES PARA O DEEPSEEK — REDESIGN NEXFISCAL

Você receberá este ZIP com **todo o frontend real** de um produto chamado NexFiscal, além do briefing funcional e da lista de contratos HTTP. Quero que **refatore o frontend existente e melhore profundamente a usabilidade**, entregando o código final completo e compilável.

Leia PRIMEIRO:
1. `CONTEXTO_FUNCIONAL_E_UX.md` — objetivos, usuários, fluxos e restrições fiscais;
2. `CONTRATOS_API.md` — integrações reais;
3. `frontend/src/main.jsx` — comportamento implementado a preservar;
4. demais componentes, CSS, package.json e teste de formatação.

### Resultado esperado
Refatore em React + Vite + Tailwind (sem reescrever o backend), com layout de sistema empresarial moderno e UX de alta clareza para analistas fiscais e TI. Quero navegação por seções (Visão geral, Empresas/Certificados, Captura NF-e/CT-e, Recuperação, Configurações), dashboard informativo e wizard de instalação separado da operação cotidiana. Crie componentes reutilizáveis, hooks de carregamento, estados de erro/loading/vazio, acessibilidade e responsividade.

**A exigência principal:** Monitoramento NF-e automático, Sincronização NF-e por distNSU, Recuperação retroativa por consNSU e Restauração de ZIPs locais são **quatro operações diferentes**. Cada uma deve ter sua própria área, propósito, mensagens, bloqueios e chamadas HTTP. Não inventar retroação universal ou alterar checkpoints.

### Entrega
- Árvore completa de arquivos modificados/novos;
- Código integral de todos os arquivos necessários (não somente trechos);
- Manter chamadas reais do backend, formatos e nomes de propriedade;
- Scripts de instalação/compilação e instruções simples para substituir o `frontend/` existente;
- Testes de componentes/fluxos que puder realizar, com resultado real e ressalvas;
- Uma lista curta de melhorias que dependem de NOVOS endpoints, separadas daquilo que foi implementado.

Evite dashboards fictícios, endpoints inventados, senhas armazenadas no navegador, placeholders que se passam por funcionalidade real e alterações na semântica fiscal.

**Importante:** trabalhe sobre o código fornecido; não devolva só imagens ou um protótipo isolado.
