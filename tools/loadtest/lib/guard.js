"use strict";
// The harness writes a lot of business data. It refuses to run unless the operator states, explicitly, that the target installation is disposable,
// and it refuses the development database port and any company that already holds data that is not load-test data. The tool cannot see the
// PostgreSQL cluster behind the API: the acknowledgement is the operator's statement and the checks below are best-effort protections, not proof.
const ACK_VALUE = "this-installation-is-disposable";
const LOOPBACK = new Set(["localhost", "127.0.0.1", "::1", "[::1]"]);

function assertDisposableTarget({ apiUrl, env = process.env, databasePort, environmentFile }) {
  const problems = [];
  let url;
  try { url = new URL(apiUrl); } catch { problems.push("the API address is not a valid URL"); }
  if (url && !["http:", "https:"].includes(url.protocol)) problems.push("the API address must be http or https");
  if (env.HASM_LOADTEST_DISPOSABLE !== ACK_VALUE) problems.push(`set HASM_LOADTEST_DISPOSABLE=${ACK_VALUE} to state that the target installation and its database are disposable`);
  if (url && !LOOPBACK.has(url.hostname) && url.protocol !== "https:") problems.push("a non-loopback API must be https (credentials and tokens would travel in clear text)");
  if (url && !LOOPBACK.has(url.hostname) && !environmentFile) problems.push("a non-loopback API needs --environment <file> describing the (disposable, dedicated) reference environment");
  const ports = [databasePort, env.PGPORT, env.HASM_LOADTEST_DB_PORT].filter((p) => p !== undefined && p !== "");
  if (ports.some((p) => String(p) === "5432")) problems.push("port 5432 is the development cluster and is never a load-test target");
  if (problems.length) { const e = new Error("refused: " + problems.join("; ")); e.code = "target_not_disposable"; throw e; }
}

module.exports = { assertDisposableTarget, ACK_VALUE };
