# 01 — Especificação funcional detalhada

## F1 — Configuração do SAP HANA

### UI
Tela inicial única quando não configurado: **Servidor/host**, **Porta SQL** (sugestão visual 30015, editável), **Usuário**, **Senha** (com mostrar/ocultar), **Testar conexão**, **Salvar configuração** e status. Exibir estado e falhas sem expor segredos. A área de configuração deve continuar acessível caso usuário precise ajustar dados depois.

### Fluxo
1. Iniciar serviço local e abrir http://127.0.0.1:5187.
2. Consultar status protegido existente; distinguir **não configurado**, **configurado e disponível**, **configurado mas indisponível**, **configuração ilegível**.
3. Preencher credenciais. Validar entrada (porta 1–65535, servidor e usuário válidos). Proibir senha em URL e logs.
4. **Testar conexão** abre conexão SAP HANA e consulta schema; NÃO cria tabelas nem salva credenciais. Se inexistente, exibir "Conexão válida; estrutura pendente" sem tratar como falha de login.
5. **Salvar configuração** testa conexão, provisiona **apenas tabelas inexistentes** e valida as colunas. Se estrutura inconsistente, bloquear e mostrar diagnóstico seguro; não executar DROP/alterar tabela existente.
6. Salvar credenciais cifradas com escrita atômica apenas após sucesso; reabrir na próxima execução sob a mesma identidade Windows.
7. Não abrir automaticamente o cadastro A1 na V1 etapa 1: apenas sinalizar conclusão.

### Regras de persistência
Schema de referência `BRAI_NEX`. Tabelas legadas incluídas no migration atual: `BRAI_NEX_SchemaVersion`, `BRAI_NEX_Company`, `BRAI_NEX_CaptureCursor`, `BRAI_NEX_Envelope`, `BRAI_NEX_CaptureLog`. Para V1 futura, revisar modelo de lacunas em migration **aditiva**, não eliminar histórico.

### Aceite
Conexão válida/inválida, estrutura ausente/criada, proteção de senha e reconexão depois de fechar processo, sem necessidade de reconfigurar.

## F2 — Cadastro de empresas e A1

### UI
**Empresas** com botão "Adicionar empresa". Formulário de arquivo `.pfx/.p12`, senha e "Validar certificado". Prévia dos campos extraídos: CNPJ, razão social, UF se encontrada, emissor, validade inicial/final. Confirmação "Cadastrar empresa". Lista exibe status do A1 e permite "Atualizar A1", "Remover empresa", "Selecionar".

### Fluxo de cadastro
1. Exigir HANA configurado e funcional.
2. Ler PFX/P12 no backend, verificar senha, presença de chave privada, formato, identificação de CNPJ, data de validade e cadeia/revogação em operação separada quando aplicável.
3. Extrair CNPJ/razão social/UF do certificado, sem entrada manual de CNPJ. Não inventar UF se ausente; aceitar campo "não identificado" e resolver requisitos técnicos conforme validação.
4. Rejeitar divergência de CNPJ e cadastro duplicado por ambiente; **não sobrescrever** certificado já existente.
5. Registrar empresa no HANA e salvar A1 protegido em local estável, com compensação/reconciliação segura se alguma gravação falhar.
6. Confirmar relendo certificado e cadastro; nunca considerar `File.Exists` como certificado válido.
7. Diferenciar status de arquivo: `missing` (não localizado), `unreadable` (presente, não descriptografável), `stored` (descriptografável), `expired` (fora do prazo), `invalid` (problema estrutural). Expor motivo humano e ação segura.

### Atualização
- A1 novo deve corresponder ao CNPJ cadastrado. Não alterar cadastro fiscal, NSU ou histórico. Processo de atualização controlada com backup/reversão, não sobrescrita cega.
- Não permitir reanexo automático se arquivo cifrado anterior existe mas chaves se perderam: oferecer diagnóstico de restauração de chaves, com orientação e opção de substituição somente por fluxo administrativo explícito.

### Remoção
- **Sem histórico fiscal**: excluir cadastro e arquivo privado após confirmação e checagens.
- **Com histórico**: desativar/arquivar logicamente; preservar HANA, envelopes, logs, NSUs, XMLs, arquivos e possibilidade de auditoria. Não repetir CNPJ como entidade inteiramente nova sem resolver identidade/checkpoint.
- Captura ativa ou job agendado: bloquear exclusão/desativação destrutiva até parada segura.

### Segurança
PFX/senha só transitam ao backend local em requisição autorizada; segredo cifrado e fora de `wwwroot`. Nunca retornar PFX/senha no JSON, nunca gravar em logs. Se rodar como serviço Windows, testar DPAPI com a identidade real.

## F3 — Captura manual: todos os XMLs completos disponíveis e ainda ausentes

### UI
Em "Captura de NF-e", selecionar empresa e pasta de destino, clicar **Capturar todos os XMLs disponíveis**. NÃO mostrar chave/NSU/campo de data como requisitos. Mostrar progresso `Aguardando SEFAZ`, `Consultando`, `Gravando`, `Concluído`, `Com pendências`, `Falha`, bem como contagens:
- XMLs completos novos gravados;
- Arquivos completos já presentes;
- XMLs completos recuperados de pendência conhecida;
- Resumos/eventos ignorados;
- NSUs/documentos ainda pendentes ou indisponíveis;
- Última consulta e próximo horário permitido.

### Fluxo essencial
1. Confirmar empresa ativa, A1 acessível e válido, HANA acessível e pasta gravável; impedir duas capturas para o mesmo CNPJ.
2. **Inventário local:** correlacionar envelopes/NSUs conhecidos e arquivos completos gravados; identificar lacunas e documentos conhecidos com apenas resumo, arquivo faltando ou inválido; sem inferir que todo resumo permite resgatar XML completo.
3. Antes de consultar SEFAZ, examinar checkpoint fiscal, tentativas anteriores e bloqueio de consumo; não reiniciar cursor indiscriminadamente.
4. Consultar `distNSU` a partir do checkpoint persistente para documentos oficialmente ainda distribuídos; processar lotes sequencialmente, incluindo tipos diferentes de payload, até `ultNSU == maxNSU`, cStat 137 ou condição de intervalo/bloqueio. Interpretar cada status conforme NT vigente.
5. Para **NSUs identificados como faltantes e elegíveis**, permitir recuperação por `consNSU` respeitando a especificação e os limites; nunca varrer NSUs arbitrários nem usar `consNSU` como "descoberta" global.
6. Para XML completo `procNFe`, validar estrutura, chave de 44 dígitos e correspondência, persistir arquivo **individual**, nome `<chave>.xml`, em modo seguro (arquivo temporário + rename atômico), sem substituir XML válido.
7. Persistir envelope (ou evidência/fingerprint conforme retenção) e estado da captura por NSU, além de checkpoint, **somente após persistência durável**. A transição de estado deve ser recuperável após crash; não avançar checkpoint deixando XML perdido.
8. Caso encontre arquivo com chave correta mas XML incompleto/corrompido, não contar como "já baixado"; registrar conflito e executar recuperação segura, preservando original para investigação.
9. Ao receber somente `resNFe`, não contar como XML; registrar como documento ainda sem XML completo e avaliar se existe caminho oficial de obtenção/manifestação aplicável, sem prometer recuperação onde não há.
10. Caso SEFAZ imponha cooldown, salvar pedido durável com `nextRunAtUtc` e retomar após permissão, **desde que serviço backend permaneça ativo**; se reiniciar, recompor fila persistente. Não mandar o operador clicar de novo.
11. Finalizar quando esgotar a distribuição oficialmente disponível ou registrar limites/inacessibilidade. Mensagem deve dizer "Todos os documentos disponibilizados nesta janela foram processados" e não "Toda a história da empresa recuperada".

### Observação fundamental
A intenção de negócio é recuperar tudo que falta; a API oficial não oferece um inventário universal e irrestrito de NSUs antigos nem permite enumerar toda a vida da empresa. A implementação deve ser **melhor esforço verificável** com relatório de pendências, nunca tentativas de força-bruta.

## F4 — Monitoramento automático de NF-e

### UI
Para a empresa selecionada: checkbox **Monitoramento automático ativo**, campo livre intervalo em minutos (mínimo 1), pasta destino e **Salvar configuração**. Exibir estado do worker, última consulta, próxima prevista, arquivos novos e falhas. A existência de uma pasta configurada não implica monitoramento ligado.

### Fluxo
1. Ativar somente por opção explícita do usuário; validar empresa, A1, pasta e HANA.
2. Registrar configuração persistente por empresa, não em memória/estado do navegador.
3. Serviço de background no backend percorre empresas ativas com monitoramento ligado; agenda conforme intervalo configurado e autorização fiscal real (a regra da SEFAZ prevalece sobre intervalos curtos).
4. Cada execução chama **o mesmo motor**, sem duplicar implementação de parse, persistência ou checkpoint.
5. Captura manual e worker compartilham exclusão mútua por empresa/documento e rate limit; nunca competem pelo cursor.
6. Após falha transitória, aplicar espera/backoff seguro. Não repetir pedidos SEFAZ em loop.
7. Reinício do backend recompõe agendamentos e estados a partir do banco; monitoramento permanece desligado onde não foi ativado explicitamente.
8. Frontend pode fechar sem encerrar o backend; **se o backend parar**, nenhuma captura ocorre até reiniciar/serviço estar ativo. Não prometer funcionamento offline.
9. Desabilitar automático não apaga fila manual, histórico fiscal ou documentos; permitir solicitação manual enquanto automático está desligado.

### Ponto para aceite na F4
"Intervalo em minutos" é periodicidade desejada, não garantia de consulta junto à SEFAZ naquele instante. Se o intervalo for 1 minuto e a SEFAZ liberar apenas depois de 1 hora, agendar para a hora permitida sem consultas antecipadas.
