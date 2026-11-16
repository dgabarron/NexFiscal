export class ApiError extends Error {
  constructor(message, kind, issues) {
    super(message);
    this.kind = kind || "unknown";
    this.issues = Array.isArray(issues) ? issues : [];
  }
}

export async function api(path, options = {}) {
  let response;
  try {
    response = await fetch(path, {
      ...options,
      headers: { ...(options.body ? { "Content-Type": "application/json" } : {}), ...options.headers }
    });
  } catch {
    throw new ApiError("Sem resposta do serviço local do NexFiscal. Confirme que ele continua em execução e tente novamente.", "service_offline");
  }
  const json = await response.json().catch(() => ({}));
  if (!response.ok)
    throw new ApiError(json.error || json.detail || "Operação não concluída.", json.kind, json.issues);
  return json;
}

export function credentials(form, replaceUnreadable = false) {
  return {
    server: form.server.trim(), port: Number(form.port),
    username: form.username.trim(), password: form.password,
    ...(replaceUnreadable ? { replaceUnreadable: true } : {})
  };
}

export function isValid(form) {
  return Boolean(form.server.trim()) && /^\d+$/.test(form.port) &&
    +form.port >= 1 && +form.port <= 65535 && Boolean(form.username.trim()) && Boolean(form.password);
}

// Traduz o estado devolvido por /api/setup/hana/status para a faixa de status da tela.
export function describeStatus(status) {
  switch (status?.state) {
    case "ready": return { tone: "ok", title: "Conexão salva e estrutura validada" };
    case "schema_pending": return { tone: "warn", title: "Conexão salva, estrutura a revalidar" };
    case "unavailable": return { tone: "warn", title: "Conexão salva, SAP HANA indisponível" };
    case "unreadable": return { tone: "error", title: "Configuração salva ilegível" };
    case "not_configured": return { tone: "idle", title: "Configuração pendente" };
    default: return { tone: "idle", title: "Status desconhecido" };
  }
}

// ---------- Empresas (função 2) ----------

export function fileToBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new ApiError("Não foi possível ler o arquivo selecionado.", "file_read"));
    reader.onload = () => resolve(String(reader.result).split(",").pop());
    reader.readAsDataURL(file);
  });
}

export function formatDate(iso) {
  if (!iso) return "—";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleDateString("pt-BR");
}

// Selo do certificado na lista. "Guardado" só aparece quando o servidor conseguiu abrir o A1.
export function describeCertificate(company) {
  if (company.archived) return { tone: "warn", label: "Empresa arquivada" };
  switch (company.certificateStatus) {
    case "stored":
      return company.daysRemaining != null && company.daysRemaining <= 30
        ? { tone: "warn", label: `A1 vence em ${company.daysRemaining} dia(s)` }
        : { tone: "ok", label: "A1 guardado e válido" };
    case "expired": return { tone: "error", label: "A1 vencido" };
    case "missing": return { tone: "error", label: "A1 não encontrado" };
    case "unreadable": return { tone: "error", label: "A1 ilegível" };
    default: return { tone: "error", label: "A1 inválido" };
  }
}

export function removalWarning(company) {
  return company.hasFiscalHistory
    ? `${company.legalName} possui histórico fiscal. Ela será apenas arquivada: documentos, NSUs e o certificado guardado ficam preservados, e ela pode ser reativada depois.`
    : `${company.legalName} não possui histórico fiscal. O cadastro e o certificado A1 guardado serão excluídos definitivamente.`;
}

// ---------- Captura de NF-e (função 3) ----------

export function formatDateTime(iso) {
  if (!iso) return "—";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "—"
    : date.toLocaleString("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function formatMoney(value) {
  return typeof value === "number" ? value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" }) : "—";
}

export function isJobActive(job) {
  return ["queued", "running", "waiting_sefaz"].includes(job?.state);
}

// Estado do pedido de captura, nos termos da especificação: Aguardando SEFAZ, Consultando, Concluído, Com pendências, Falha.
export function describeJob(job, source = "a SEFAZ") {
  switch (job?.state) {
    case "queued":
    case "running": return { intent: "info", title: `Consultando ${source} e gravando os XMLs...`, busy: true };
    case "waiting_sefaz": return { intent: "warning", title: `Aguardando ${source} — continua sozinho em ${formatDateTime(job.nextRunAtUtc)}`, busy: false };
    case "completed": return { intent: "success", title: "Captura concluída", busy: false };
    case "completed_with_pending": return { intent: "warning", title: "Captura concluída com pendências", busy: false };
    case "failed": return { intent: "error", title: "A captura falhou", busy: false };
    default: return null;
  }
}

// Contagens exibidas. Resumos e eventos nunca entram na conta de XMLs completos.
export function jobCounters(job) {
  return [
    { key: "newComplete", label: "XMLs completos novos", value: job?.newComplete ?? 0, tone: "ok" },
    { key: "alreadyPresent", label: "Já presentes na pasta", value: job?.alreadyPresent ?? 0, tone: "neutral" },
    { key: "recovered", label: "Recuperados de pendência", value: job?.recovered ?? 0, tone: "ok" },
    { key: "ignored", label: "Resumos e eventos ignorados", value: job?.ignored ?? 0, tone: "neutral" },
    { key: "pending", label: "Ainda sem XML completo", value: job?.pending ?? 0, tone: (job?.pending ?? 0) > 0 ? "warn" : "neutral" },
    { key: "failures", label: "Falhas", value: job?.failures ?? 0, tone: (job?.failures ?? 0) > 0 ? "error" : "neutral" },
  ];
}

// ---------- Monitoramento automático (função 4) ----------

// Situação do monitoramento de uma empresa. Ligado só por opção explícita do operador.
export function describeMonitor(view) {
  if (!view) return null;
  if (view.enabled && !view.canCapture) return { intent: "error", title: "Monitoramento ativo, mas em espera" };
  if (view.enabled) return { intent: "success", title: `Monitoramento ativo — próxima consulta prevista para ${formatDateTime(view.nextRunAtUtc)}` };
  return { intent: "info", title: "Monitoramento automático desligado para esta empresa" };
}

export function isIntervalValid(text) {
  return /^\d+$/.test(String(text)) && +text >= 1 && +text <= 10080;
}

// ---------- Tipos de documento (NF-e pela SEFAZ, NFS-e pelo ADN) ----------

export const DOCUMENT_KINDS = {
  nfe: { id: "nfe", label: "NF-e", source: "a SEFAZ", sourceTo: "à SEFAZ", pending: true,
    lead: "Baixa da SEFAZ todos os XMLs completos de NF-e disponíveis para a empresa e que ainda não estão na pasta, um arquivo por nota. Nenhuma chave ou número precisa ser digitado." },
  nfse: { id: "nfse", label: "NFS-e", source: "o ADN", sourceTo: "ao ADN da NFS-e", pending: false,
    lead: "Baixa do Ambiente de Dados Nacional todas as NFS-e do padrão nacional em que a empresa é prestadora, tomadora ou intermediária e que ainda não estão na pasta, um arquivo por nota." },
};

// Contagens de uma captura; na NFS-e não existe "resumo sem XML completo".
export function countersFor(kind, job) {
  const counters = jobCounters(job);
  if (kind !== "nfse") return counters;
  return counters.filter(c => c.key !== "pending").map(c => c.key === "ignored" ? { ...c, label: "Eventos (não são notas)" }
    : c.key === "newComplete" ? { ...c, label: "NFS-e novas" } : c);
}

// ---------- Ciência da Operação (etapa 6) ----------

export const MANIFEST_LIMIT = 100;

// Situação da ciência de uma nota pendente, como aparece na lista.
export function describeManifest(note) {
  if (note?.manifestState === "registered") return { tone: "ok", text: note.manifestMessage || "Ciência registrada. Aguardando o XML completo." };
  if (note?.manifestState === "rejected") return { tone: "error", text: note.manifestMessage || "A SEFAZ recusou a ciência." };
  if (note && !note.canManifest) return { tone: "neutral", text: "Manifestação disponível só no portal da SEFAZ." };
  return { tone: "neutral", text: "Ciência ainda não registrada" };
}

// Notas que podem ser marcadas, limitadas ao máximo de um envio.
export function selectableKeys(notes) {
  return (notes || []).filter(n => n.canManifest).slice(0, MANIFEST_LIMIT).map(n => n.accessKey);
}
