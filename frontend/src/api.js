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
