# Refatoração de estabilização — NexFiscal (13/11/2026)

Esta revisão foi isolada na branch `qa/refactor-estabilizacao-20261113`, derivada da branch de QA atual. Não altera a `main`, não migra arquivos automaticamente e não remove documentos fiscais.

## 1. Persistência de dados privados

Antes, certificados A1, chaves ASP.NET Data Protection, conexão SAP HANA, agendamentos, checkpoint CT-e e lotes de evidência eram armazenados com base em `IWebHostEnvironment.ContentRootPath`. Essa raiz varia conforme a forma de iniciar a aplicação, o que podia fazer o certificado parecer ausente ou deixar a conexão HANA ilegível.

A classe `NexFiscalDataDirectory` determina uma raiz independente do diretório atual: no desenvolvimento, ela localiza `src/NexFiscal.Web` a partir do binário e verifica tanto essa pasta quanto a raiz da solução em busca de arquivos privados já existentes. Se apenas uma contém dados, utiliza essa. Se ambas contêm arquivos privados, **interrompe a inicialização com diagnóstico claro** para não misturar chaves, certificados e agendas. Em instalação publicada, usa a pasta dos binários. Nenhum dado é copiado, sobrescrito ou apagado durante a detecção.

É possível definir explicitamente um caminho absoluto e durável com a variável `NEXFISCAL_DATA_DIR` ou configuração `NexFiscal:DataDirectory`. **Antes de apontá-lo, verifique que os arquivos necessários pertencem à mesma instalação e preserve uma cópia de segurança.** Usar a pasta errada pode tornar configurações e A1 inacessíveis.

A mensagem de inicialização informa a raiz escolhida no log. Preservar o conjunto de estado:
- `.nexfiscal-hana.dat`
- `.nexfiscal-keys/`
- `.nexfiscal-certificates/`
- `.nexfiscal-schedules/`
- `.nexfiscal-cte/`
- `.nexfiscal-downloads/`

As pastas de XML configuradas separadamente também devem ser preservadas, assim como os checkpoints fiscais armazenados no HANA.

## 2. Diagnóstico do certificado

A API distingue `certificateStatus=missing` (arquivo não localizado), `stored` (cofre descriptografável) e `unreadable` (arquivo existe, mas as chaves locais não permitem abri-lo). O frontend não oferece reenvio do A1 quando o estado é `unreadable`: é necessário conferir as chaves originais e a conta do Windows que executa o processo.

`stored` **não garante validade temporal, revogação, cadeia ICP-Brasil ou autorização SEFAZ**; essas verificações continuam no diagnóstico de conectividade e na captura real.

## 3. Janela fiscal consistente

`FiscalWindow` centraliza o processamento de `nextAllowedUtc` com e sem sufixo de fuso e com offset explícito. NF-e e CT-e usam a mesma regra de comparação UTC no worker automático e no agendamento por solicitação manual. O estado `scheduled` não recebe horário de término enquanto estiver aguardando.

## 4. Testes

- Testes unitários de seleção de raiz de dados: pasta de desenvolvimento, instalação legada na solução, conflito entre raízes, caminho explícito e publicação.
- Testes unitários de interpretação da janela fiscal UTC.
- Build ASP.NET Core, Vite e testes do frontend.
- Smoke test: inicialização do backend, resposta `GET /api/health` e entrega do frontend pela raiz `/`.

Esses testes **não** substituem a homologação Windows + SAP HANA + certificado A1 + SEFAZ. Antes da liberação final, confirmar também a manutenção do certificado entre reinícios, cadastro/remoção de empresas e a captura NF-e/CT-e manual e automática respeitando os checkpoints.

## 5. Rodar localmente sem sobrepor versões

Não sobrescreva a instalação antiga. Extraia a branch refatorada em outra pasta, mas **não copie apenas um arquivo de certificado ou apenas chaves**. Defina `NEXFISCAL_DATA_DIR` somente depois de verificar a raiz de estado original e fazer backup. A instalação pode usar o mesmo HANA; por isso não habilite dois consumidores simultaneamente para o mesmo CNPJ. Se os dados estiverem espalhados por duas raízes, interrompa a operação até reconciliá-los de forma controlada.

**Estado:** versão candidata para QA, não homologada em produção.
