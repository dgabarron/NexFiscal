# 05 — Regras fiscais e técnicas críticas: NSUs e recuperação

## Intenção de negócio aprovada
O operador quer clicar **"Capturar todos os XMLs disponíveis"** e receber TODOS OS XMLs COMPLETOS `procNFe` que o serviço oficial ainda disponibilizar ao CNPJ e que não estão armazenados corretamente. A captura deve incluir documentos ainda não consultados **e** documentos já conhecidos no banco, mas sem arquivo completo salvo. Não solicitar chave ou NSU ao operador.

## O que a API realmente permite
O serviço NFeDistribuicaoDFe opera com distribuição por NSU. A requisição `distNSU` avança sequencialmente a partir de `ultNSU` e retorna metadados de limite como `ultNSU`/`maxNSU` e documentos da janela disponível. `consNSU` consulta um NSU conhecido quando suportado e elegível, **não é um endpoint para descobrir cada NSU histórico**. O comportamento de geração, retenção e disponibilidade pode depender de regras de habilitação, tipo de ator fiscal, manifestação e tempo. Confira a **Nota Técnica vigente** antes de codificar quotas/cStat/janelas; não hardcodar suposições não verificadas.

A janela de disponibilidade frequentemente citada é da ordem de **90 dias** na distribuição, mas a norma vigente e a natureza do documento/consulta devem determinar a regra real. Não garantir "desde o nascimento da empresa". A API não devolve uma listagem universal de NSUs de todos os documentos históricos.

## Estratégia obrigatória
1. Persistir checkpoint oficial por CNPJ/tipo/ambiente fiscal.
2. Inventariar o que já está salvo (envelopes NSU, chaves identificadas, XMLs completos, integridade SHA-256, arquivos existentes).
3. Criar fila **somente de NSUs conhecidos com evidência real** e elegíveis para `consNSU`, com limites de tentativas e motivo de recuperação.
4. Percorrer `distNSU` do checkpoint persistido para obter novos envelopes oficialmente distribuídos; respeitar retorno de cada lote, NSU monotônico e cStat.
5. Classificar `docZip` por tipo de esquema; `procNFe` só é XML completo, `resNFe` não é; eventos também não.
6. Para `procNFe`, validar conteúdo, nome por chave 44 dígitos, persistência atômica, deduplicação e checkpoint consistente.
7. Registrar resumos/eventos como evidência sem contar como arquivo completo baixado; avaliar necessidade técnica de manifestação do destinatário quando aplicável (não incluir fluxo de manifestação sem detalhamento/consentimento explícito).
8. Usar `consNSU` apenas em situações elegíveis, com checkpoint/limite de consulta e sem gerar tráfego abusivo. Caso o serviço não entregue o completo, informar `indisponível` ou `pendente`, sem mascarar como sucesso.
9. Em `137` ou `656`, respeitar bloqueios e não martelar a SEFAZ. Fazer controle de próximas janelas em UTC e gravar fila persistente.
10. Relatório: **novos completos**, **já existentes**, **recuperados**, **resumos ignorados**, **falhas**, **pendências não disponíveis**. Nunca somar `resNFe` ao número de XMLs completos.

## Situações concretas do histórico do projeto
- O sistema antigo mostrava "ZIP de lote SEFAZ (somente no ZIP)" e não entregava arquivos completos separados na pasta. **Isso não atende o objetivo.**
- Consultas anteriores recuperavam somente documentos recentes ou do dia; essa observação **não prova** que todo histórico estava disponível pela SEFAZ. A V1 deve distinguir erro de lógica, checkpoint avançado e ausência oficial.
- Já foram citadas cinco NF-e esperadas como ausentes em uma instalação anterior. Essas chaves são **casos de homologação privada**, não hardcodar em código/testes públicos nem presumir que os XMLs existem no serviço.
- Pedido explícito: **não validar empresas anteriores nem exigir NSU manualmente**. Capturar apenas documentos ainda disponíveis para a empresa cadastrada.

## Riscos de desenho
- Avançar o NSU antes de salvar XML perde oportunidades de download.
- Reconsultar o início/zerar NSU indiscriminadamente pode causar uso indevido; NSU antigo não é garantia de recuperação.
- Duas instâncias para o mesmo CNPJ podem consumir cota e gerar dados inconsistentes; implementar lock persistente.
- Arquivo com chave correta mas XML inválido não deve ser tratado como completo.
- Jobs apenas em RAM desaparecem após restart, invalidando promessa de retomada automática.

## Fonte normativa
Antes de implementar F3, validar manual e nota técnica **NFeDistribuicaoDFe (Distribuição DF-e de Interesse dos Atores da NF-e)** vigentes no Portal Nacional da NF-e / ENCAT, especialmente operação `distNSU`, `consNSU`, `docZip`, status 137/138/656, janelas e regras de manifestação. Não tomar notas do projeto antigo como substitutas da documentação oficial.
