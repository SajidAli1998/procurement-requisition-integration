export function fmtNumber(value: number | null | undefined, opts?: Intl.NumberFormatOptions, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "-";
  return new Intl.NumberFormat(locale, opts).format(value);
}

export function fmtAmount(value: number | null | undefined, currency?: string | null, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "-";
  if (currency && /^[A-Z]{3}$/.test(currency)) {
    try {
      return new Intl.NumberFormat(locale, { style: "currency", currency, maximumFractionDigits: 2 }).format(value);
    } catch {
      // Unknown currency code: fall back to a plain number.
    }
  }
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(value);
}

export function fmtMs(ms: number | null | undefined, locale?: string): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "-";
  if (ms >= 1000) return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(ms / 1000)} s`;
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(ms)} ms`;
}

export function fmtPct(value: number | null | undefined, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "-";
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value)}%`;
}

function parseDate(value: string | null | undefined): Date | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) || d.getUTCFullYear() < 1900 ? null : d;
}

export function fmtDateTime(value: string | null | undefined, locale?: string, timeZone?: string): string {
  const d = parseDate(value);
  if (!d) return value ? String(value) : "-";
  return new Intl.DateTimeFormat(locale, {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    timeZoneName: "short",
    timeZone,
  }).format(d);
}

export function fmtTime(value: string | null | undefined, locale?: string, timeZone?: string): string {
  const d = parseDate(value);
  if (!d) return "-";
  return new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit", second: "2-digit", timeZone }).format(d);
}

// Calendar dates have no timezone. Formatting them as UTC keeps them from shifting a day.
export function fmtDateOnly(value: string | null | undefined, locale?: string): string {
  if (!value) return "-";
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!m) return value;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return new Intl.DateTimeFormat(locale, { year: "numeric", month: "short", day: "2-digit", timeZone: "UTC" }).format(d);
}

