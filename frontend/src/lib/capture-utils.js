const NINETY_DAYS_MS = 90 * 86400000;
const LONG_MAX = 9223372036854775807n;
const DEFAULT_TYPES = ["NFE", "CTE", "NFSE"];

export function pendingWindow(entity, now) {
  const raw = entity?.nextAllowedUtc;
  if (!raw) return null;
  const canonical = /[zZ]$|[+-]\d{2}:\d{2}$/.test(raw) ? raw : `${raw}Z`;
  const ms = Date.parse(canonical);
  if (!Number.isFinite(ms) || ms <= now) return null;
  return {
    time: new Date(ms).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" }),
    minutes: Math.ceil((ms - now) / 60000),
    iso: new Date(ms).toISOString(),
  };
}

export function parseUtcTimestamp(raw) {
  if (!raw) return null;
  const canonical = /[zZ]$|[+-]\d{2}:\d{2}$/.test(raw) ? raw : `${raw}Z`;
  const ms = Date.parse(canonical);
  return Number.isFinite(ms) ? new Date(ms) : null;
}

export function formatUtcAsLocal(raw, fallback = "Ainda não realizada") {
  const date = parseUtcTimestamp(raw);
  return date ? date.toLocaleString("pt-BR") : fallback;
}

export function defaultSchedule() {
  return DEFAULT_TYPES.map((documentType) => ({
    documentType, enabled: false, intervalMinutes: "60", destinationDirectory: "",
  }));
}

export function mergeSchedule(raw) {
  if (!Array.isArray(raw)) return defaultSchedule();
  const byType = new Map(raw.map((s) => [s.documentType, s]));
  return DEFAULT_TYPES.map((documentType) => {
    const found = byType.get(documentType) || {};
    return {
      documentType,
      enabled: !!found.enabled,
      intervalMinutes: String(found.intervalMinutes ?? "60"),
      destinationDirectory: found.destinationDirectory ?? "",
    };
  });
}

export function isValidInterval(value) {
  const text = String(value ?? "").trim();
  if (!/^\d+$/.test(text)) return false;
  const n = BigInt(text);
  return n >= 1n && n <= LONG_MAX;
}

export function maskDateInput(raw) {
  const digits = String(raw ?? "").replace(/\D/g, "").slice(0, 8);
  if (digits.length > 4) return `${digits.slice(0, 2)}/${digits.slice(2, 4)}/${digits.slice(4)}`;
  if (digits.length > 2) return `${digits.slice(0, 2)}/${digits.slice(2)}`;
  return digits;
}

export function defaultRecoveryDate() {
  const date = new Date(Date.now() - 89 * 86400000);
  return new Intl.DateTimeFormat("pt-BR", {
    day: "2-digit", month: "2-digit", year: "numeric", timeZone: "UTC",
  }).format(date);
}

export function parseRecoveryDate(raw) {
  const text = String(raw ?? "").trim();
  const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(text);
  if (!match) throw new Error("Informe a data no formato DD/MM/AAAA.");
  const day = Number(match[1]), month = Number(match[2]), year = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (
    date.getUTCFullYear() !== year ||
    date.getUTCMonth() !== month - 1 ||
    date.getUTCDate() !== day
  ) throw new Error("Data inválida. Use DD/MM/AAAA.");
  const now = Date.now(), from = date.getTime();
  if (from > now || from < now - NINETY_DAYS_MS)
    throw new Error("Escolha uma data dentro dos últimos 90 dias.");
  return date.toISOString();
}
