"use strict";
/** Nearest-rank percentile (p in 0..100) of a numeric array; null for an empty sample. Never interpolates or estimates. */
function percentile(values, p) {
  if (!values.length) return null;
  const s = [...values].sort((a, b) => a - b);
  const rank = Math.max(1, Math.ceil((p / 100) * s.length));
  return s[Math.min(rank, s.length) - 1];
}
function summarize(values) {
  return {
    samples: values.length,
    min: values.length ? Math.min(...values) : null,
    p50: percentile(values, 50), p95: percentile(values, 95), p99: percentile(values, 99),
    max: values.length ? Math.max(...values) : null,
  };
}
module.exports = { percentile, summarize };
