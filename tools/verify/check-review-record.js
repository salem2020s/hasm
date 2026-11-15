#!/usr/bin/env node
// Validates an independent review record against the PR head: it must name the exact head SHA, a reviewer context,
// findings, the list of open Blocker/High findings and a verdict. It cannot create independence; it only refuses
// records that are incomplete or that belong to another commit.
// Usage: node tools/verify/check-review-record.js <record.md> <headSha>
"use strict";
const fs = require("fs");

function validate(text, headSha) {
  const p = [];
  const sha = (text.match(/Head SHA reviewed[^:]*:\*\*\s*([0-9a-f]{40})/i) || [])[1];
  if (!sha) p.push("the full 40-character head SHA is missing");
  else if (!(sha === headSha || (headSha.length < 40 && sha.startsWith(headSha)))) p.push(`the record reviewed ${sha.slice(0, 7)}, not the PR head ${headSha.slice(0, 7)}`);
  for (const k of ["Reviewer context", "Findings", "Blocker or High findings open", "NOT VERIFIED", "Verdict"]) {
    const re = new RegExp("\\*\\*" + k + "[^*]*\\*\\*[ \\t]+\\S", "i");
    if (!re.test(text)) p.push(`section "${k}" is missing or empty`);
  }
  if (/<(sha|number|separate|only|files|list)[^>\n]*>/i.test(text)) p.push("template placeholders were not replaced");
  if (!/Verdict:\*\*[ \t]*(approve|request changes)[ \t]*$/im.test(text)) p.push("the verdict must be exactly 'approve' or 'request changes'");
  if (/Verdict:\*\*[ \t]*approve[ \t]*$/im.test(text) && !/Blocker or High findings open:\*\*\s*none/i.test(text)) p.push("an approval cannot carry open Blocker/High findings");
  return p;
}
module.exports = { validate };

if (require.main === module) {
  const [f, sha] = process.argv.slice(2);
  if (!f || !sha) { console.error("usage: check-review-record.js <record.md> <headSha>"); process.exit(2); }
  const problems = validate(fs.readFileSync(f, "utf8"), sha);
  if (problems.length) { console.error("REVIEW RECORD INVALID:\n - " + problems.join("\n - ")); process.exit(1); }
  console.log("Review record is complete and bound to " + sha.slice(0, 7));
}
