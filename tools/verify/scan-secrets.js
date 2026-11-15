#!/usr/bin/env node
// Secret scan (Phase 4.0): scans tracked files for private keys, access tokens and literal passwords in configuration files.
// Findings that are deliberate (documented throwaway CI values) must be listed with a reason in .github/secret-scan-allow.json
// ({"allow":[{"file":"path","rule":"rule-id","reason":"why"}]}); the file is a protected verification file. An allow entry that no longer matches anything is itself reported.
// Known limits: tracked files only (git history is not scanned); files over 2 MB are listed as skipped; source files (.cs/.ts) are not scanned for password literals because test fixtures use them.
// Usage: node tools/verify/scan-secrets.js [root]
"use strict";
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const CONFIG = /(^|\/)\.env[^/]*$|\.(ya?ml|json|config|ps1|sh|toml|ini|properties|xml|sql|conf)$/i;
const RULES = [
  ["private-key", /-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----/],
  ["github-token", /\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b/],
  ["aws-access-key", /\bAKIA[0-9A-Z]{16}\b/],
  ["slack-token", /\bxox[abprs]-[A-Za-z0-9-]{10,}\b/],
  ["jwt", /\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/],
  ["stripe-key", /\bsk_live_[0-9a-zA-Z]{20,}\b/],
  ["google-api-key", /\bAIza[0-9A-Za-z_-]{35}\b/],
  ["azure-storage-key", /AccountKey\s*=\s*[A-Za-z0-9+/=]{40,}/],
  ["bearer-literal", /Authorization\s*[:=]\s*["']?Bearer\s+[A-Za-z0-9._-]{20,}/i],
  ["connection-password", /(?:Password|Pwd|PGPASSWORD|POSTGRES_PASSWORD)"?\s*[=:]\s*["']?(?!\$|\{|<|%|;|\s|@)[^;\s"',]{6,}/i, CONFIG],
  ["generic-secret", /(?:api[_-]?key|client[_-]?secret|access[_-]?token|secret[_-]?key)"?\s*[=:]\s*["'][A-Za-z0-9/+_=-]{20,}["']/i, CONFIG],
];
const SKIP = /(^|\/)(node_modules|package-lock\.json)(\/|$)|\.(png|jpg|jpeg|ico|woff2?|ttf|pdf|xlsx|zip|snap)$/i;

function scan(root, files, skippedLarge = []) {
  const hits = [];
  for (const f of files) {
    if (SKIP.test(f)) continue;
    const p = path.join(root, f); let text;
    try { if (fs.statSync(p).size > 2_000_000) { skippedLarge.push(f); continue; } text = fs.readFileSync(p, "utf8"); } catch { continue; }
    text.split(/\r?\n/).forEach((line, i) => { for (const [id, re, ext] of RULES) if ((!ext || ext.test(f)) && re.test(line)) hits.push({ file: f, line: i + 1, rule: id }); });
  }
  return hits;
}
function filterAllowed(hits, allow) {
  const used = new Set(); const left = [];
  for (const h of hits) { const k = allow.findIndex(a => a.file === h.file && a.rule === h.rule && a.reason); if (k >= 0) used.add(k); else left.push(h); }
  const stale = allow.map((a, k) => ({ a, k })).filter(x => !used.has(x.k)).map(x => x.a);
  return { left, stale };
}
module.exports = { scan, filterAllowed, RULES };

if (require.main === module) {
  const root = path.resolve(process.argv[2] || ".");
  const files = execFileSync("git", ["-C", root, "ls-files"], { encoding: "utf8", maxBuffer: 1 << 28 }).split("\n").filter(Boolean);
  const allowFile = path.join(root, ".github/secret-scan-allow.json");
  const allow = fs.existsSync(allowFile) ? JSON.parse(fs.readFileSync(allowFile, "utf8")).allow : [];
  const large = [];
  const { left, stale } = filterAllowed(scan(root, files, large), allow);
  large.forEach(f => console.log(`notice: ${f} skipped (over 2 MB)`));
  if (left.length || stale.length) {
    console.error("SECRET SCAN FAILED");
    left.forEach(h => console.error(` - ${h.file}:${h.line} [${h.rule}] (values are never printed)`));
    stale.forEach(a => console.error(` - stale allow entry: ${a.file} [${a.rule}]`));
    process.exit(1);
  }
  console.log(`Secret scan passed (${files.length} tracked files, ${allow.length} documented allowances)`);
}
