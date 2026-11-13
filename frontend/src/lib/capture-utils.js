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
