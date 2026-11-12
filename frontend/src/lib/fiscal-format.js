/**
 * Formatação exclusivamente visual. APIs, armazenamento e validações
 * continuam trabalhando com os identificadores originais sem máscara.
 */
export function onlyDigits(value) {
  return String(value ?? "").replace(/\D/g, "");
}

export function formatCnpj(value) {
  const digits = onlyDigits(value);
  if (!/^\d{14}$/.test(digits)) return value == null ? "Não informado" : String(value);
  return digits.replace(/^(\d{2})(\d{3})(\d{3})(\d{4})(\d{2})$/, "$1.$2.$3/$4-$5");
}

export function formatCpf(value) {
  const digits = onlyDigits(value);
  if (!/^\d{11}$/.test(digits)) return value == null ? "Não informado" : String(value);
  return digits.replace(/^(\d{3})(\d{3})(\d{3})(\d{2})$/, "$1.$2.$3-$4");
}

/**
 * Os certificados X.509 expõem Issuer como Distinguished Name (DN).
 * Exibimos apenas o CN legível na tela, preservando o DN completo
 * para consulta técnica. Suporta vírgulas escapadas no DN.
 */
export function issuerDisplayName(issuerDn) {
  if (!issuerDn || typeof issuerDn !== "string") return "Não identificado";
  const parts = issuerDn.match(/(?:\\.|[^,;])+/g) ?? [];
  const cn = parts.find((part) => /^\s*CN\s*=/i.test(part));
  if (!cn) return issuerDn.trim();
  return cn.replace(/^\s*CN\s*=\s*/i, "").trim().replace(/\\([,;=+])/g, "$1") || issuerDn.trim();
}
