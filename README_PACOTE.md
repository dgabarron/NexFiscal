# Pacote de redesign — NexFiscal / DeepSeek
Origem: https://github.com/dgabarron/NexFiscal/tree/feat/nfe-capture-core
Commit: 81f8a0d2b2723cb2b8c394a2dc2de04921f9e6db
Data: 12/11/2026

Este ZIP contém:
- `frontend/`: **todos os 11 arquivos de frontend existentes** na branch de desenvolvimento, com código React, Vite, CSS, componentes, utilitários e testes.
- `CONTEXTO_FUNCIONAL_E_UX.md`: descrição do produto, perfis, jornadas, fluxos e critérios de aceite.
- `CONTRATOS_API.md`: rotas reais utilizadas pelo frontend.
- `PROMPT_PARA_DEEPSEEK.md`: instruções objetivas para executar o redesign.

Não foram incluídos backend, certificados, senhas, XMLs fiscais ou diretórios locais.

Para rodar o frontend: `cd frontend && npm install && npm run build`.
A aplicação depende de um backend ASP.NET Core em `/api`; abrir o frontend isoladamente não executará as capturas.
Leia primeiro o prompt e o contexto antes de alterar o código.
