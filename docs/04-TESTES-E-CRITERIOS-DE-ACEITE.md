# 04 — Matriz de teste e critérios de aceite

## F1 HANA — HOMOLOGAR PRIMEIRO
| ID | Cenário | Resultado esperado |
|---|---|---|
| H01 | Iniciar pasta nova | Abre somente configuração HANA |
| H02 | Host e porta inválidos | Mensagem objetiva, não salva credenciais |
| H03 | Senha incorreta | Erro controlado, sem expor senha |
| H04 | Credenciais corretas, schema inexistente | **Testar** confirma acesso, indica estrutura pendente; não cria objetos |
| H05 | Salvar com privilégios DDL | Cria apenas schema/tabelas que faltam, valida 5 tabelas e salva |
| H06 | Salvar com tabelas já existentes | Não modifica os dados, valida estrutura e salva |
| H07 | Tabela com colunas incompatíveis | Bloqueia e informa necessidade de correção; não destrói tabela |
| H08 | Falha de provisionamento | Não salva credenciais como sucesso; estado claro |
| H09 | Reiniciar backend | Reconhece conexão HANA salva e conecta novamente |
| H10 | Mesmo projeto rodado por terminal em outras pastas | Localiza frontend e mesma configuração |
| H11 | Arquivo de config cifrado sem chaves | Mostra configuração inacessível, não sobrescreve nem pede login silenciosamente |
| H12 | Reiniciar com outra conta Windows | Falha DPAPI explicada sem apagar dados |
| H13 | Requisições de máquina remota | Acesso bloqueado; porta apenas loopback |
| H14 | GitHub CI Windows e Linux | Backend, frontend e smoke passam; **isso não substitui H04–H12 no HANA real** |

## F2 A1
| ID | Cenário | Resultado esperado |
|---|---|---|
| C01 | PFX/P12 válido com senha | CNPJ/razão social/UF/validade extraídos e exibidos |
| C02 | Senha errada ou arquivo inválido | Erro legível, sem cadastro parcial |
| C03 | CNPJ duplicado | Recusa cadastro duplicado; permite atualizar A1 corretamente |
| C04 | Reinício servidor | Empresa e certificado continuam acessíveis |
| C05 | Arquivo existe, não descriptografa | `unreadable`, diferente de `missing`; nenhuma sobrescrita automática |
| C06 | Certificado vencido | Mostra vencido; bloqueia captura se necessário |
| C07 | Atualizar A1 de mesmo CNPJ | Substituição controlada, preserve NSU e histórico |
| C08 | Atualizar A1 de outro CNPJ | Rejeita |
| C09 | Remover sem histórico | Exige confirmação, remove cadastro e material privado autorizado |
| C10 | Remover com histórico | Arquiva logicamente, preserva documentos/checkpoint |

## F3 Manual NF-e
| ID | Cenário | Resultado esperado |
|---|---|---|
| N01 | CNPJ com documentos novos disponíveis | Percorre `distNSU` dentro das regras, salva `procNFe` único por chave |
| N02 | XML completo já na pasta | Não duplica nem sobrescreve válido |
| N03 | Resumo `resNFe` sem completo | Não conta como XML, registra pendência e possibilidade técnica |
| N04 | NSU conhecido com XML ausente | Tenta recuperação **se elegível**, via mecanismo oficialmente permitido |
| N05 | NSU arbitrário desconhecido | Nunca executa varredura/brute force |
| N06 | Arquivo com nome correto mas XML corrompido | Não conta como presente; preserva evidência e permite recuperação segura |
| N07 | Requisição duplicada enquanto job ativo | Não executa duas consultas concorrentes |
| N08 | `cStat=137` | Para, registra nada mais disponível e intervalo obrigatório |
| N09 | `cStat=138` | Processa lote e avança cursor somente com persistência segura |
| N10 | `cStat=656` | Pausa, não repete consulta imediata; estado explícito |
| N11 | Falha disco/HANA entre gravação e checkpoint | Reiniciar sem perda ou duplicação; reconciliação consistente |
| N12 | Job pendente e backend reiniciado | Solicitação durável é retomada com segurança |
| N13 | XML já capturado anteriormente e ausente agora | Inventário detecta falta; tentativa de recuperação se possível, senão marca indisponível |
| N14 | Nenhum XML disponível | Resultado honesto sem alegar inexistência de notas históricas |

## F4 Monitor
| ID | Cenário | Resultado esperado |
|---|---|---|
| A01 | Automático desabilitado | Nenhum disparo espontâneo |
| A02 | Ativar automático com intervalo livre | Salva configuração por CNPJ |
| A03 | Intervalo 1 min mas SEFAZ exige 1 h | Não consulta antecipadamente; agenda horário elegível |
| A04 | Frontend fechado, backend ativo | Captura continua em serviço |
| A05 | Backend encerrado | Não há captura; ao reiniciar recompõe jobs/schedule |
| A06 | Manual durante automático | Compartilha lock e evita concorrência |
| A07 | Trocar A1 ou pasta | Não perde checkpoint nem histórico |
| A08 | Falha transitória | Registra erro e espera backoff apropriado |

## Evidências exigidas
- Para testes com HANA real: saída visual/API sem senha ou dados sensíveis, esquema e estado antes/depois.
- Para SEFAZ: logs sanitizados com status, NSUs, número de envelopes, `procNFe` persistidos e arquivo por chave; não publicar PFX, senhas, XMLs privados nem CNPJ real sem consentimento.
- Cada PR deve indicar o que foi **realmente** testado e o que não foi.
