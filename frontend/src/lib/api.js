export class ApiError extends Error {
  constructor(message, { status, detail, stage, diagnostic } = {}) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.detail = detail;
    this.stage = stage;
    this.diagnostic = diagnostic;
  }
}

async function request(path, { method = "GET", body } = {}) {
  const init = { method };
  if (body !== undefined) {
    if (body instanceof FormData) init.body = body;
    else {
      init.headers = { "Content-Type": "application/json" };
      init.body = JSON.stringify(body);
    }
  }
  const resp = await fetch(path, init);
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    const suffix = data.diagnostico
      ? ` (Diagnóstico: ${data.diagnostico}; etapa: ${data.etapa})` : "";
    throw new ApiError(
      (data.error || data.detail || "A operação não foi concluída.") + suffix,
      { status: resp.status, detail: data.detail, stage: data.etapa, diagnostic: data.diagnostico }
    );
  }
  return data;
}

export const api = {
  get: (path) => request(path),
  post: (path, body) => request(path, { method: "POST", body }),
};
