# NexFiscal V1 — Etapa 01: SAP HANA

**Escopo desta branch: apenas configuração e validação SAP HANA.** Sem cadastro de empresa/A1, captura manual ou automática, CT-e, NFS-e ou outras telas.

## Como testar (Windows)

1. Extraia este ZIP **em uma pasta nova**. Não misture com a instalação anterior.
2. Instale .NET SDK 8 e Node.js.
3. Execute na raiz da pasta extraída:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Run-NexFiscalLocal.ps1
```

4. Acesse http://127.0.0.1:5187.
5. Informe servidor, porta, usuário e senha SAP HANA.
6. Clique em **Testar conexão**. A mensagem distingue conexão válida e estrutura pendente.
7. Clique em **Salvar configuração**. A aplicação cria somente as tabelas ausentes de `BRAI_NEX`, valida estrutura e salva a conexão cifrada. Caso a criação falhe, nada é salvo.
8. Feche a aplicação, inicie novamente, confirme que a conexão segue salva e validada.

## Preservação de dados

Dados privados desta V1: `%LOCALAPPDATA%\NexFiscal\V1`. Esta localização **não depende da pasta de execução**. Não apague a pasta ou suas chaves `.nexfiscal-keys`. No Windows, as chaves são protegidas pelo DPAPI do usuário executando o serviço. Para servidor, mantenha a **mesma conta Windows**.

Para escolher raiz explícita, configure `NEXFISCAL_DATA_DIR` como caminho absoluto antes da primeira execução. Não aponte para uma instalação antiga sem revisar as chaves e arquivos.

O serviço só escuta `127.0.0.1:5187`, não há login multiusuário. Ainda não é solução para rede/produção.

## Endpoints

- `GET /api/health`
- `GET /api/setup/hana/status` (nunca retorna senha)
- `POST /api/setup/hana/test` (não salva nem cria tabelas)
- `POST /api/setup/hana/save` (cria só tabelas ausentes, valida, salva credenciais cifradas)

A validação das tabelas reaproveita o schema `BRAI_NEX` com cinco tabelas do projeto fiscal. Instalação não apaga dados e rejeita tabelas incompatíveis sem sobrescrevê-las.

**Teste no GitHub Actions não substitui sua homologação contra um HANA real.** Nesta etapa a única validação funcional necessária é testar conexão, estrutura e persistência após reiniciar.
