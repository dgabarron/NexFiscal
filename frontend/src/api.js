export async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { ...(options.body ? { "Content-Type": "application/json" } : {}), ...options.headers }
  });
  const json = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(json.error || json.detail || "Operação não concluída.");
  return json;
}

export function credentials(form) {
  return {
    server: form.server.trim(), port: Number(form.port),
    username: form.username.trim(), password: form.password
  };
}
