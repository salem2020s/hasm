"use strict";
// Build-time inventory and release-installer inspection. Never executes or installs the installer.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { createHash } = require("node:crypto");
const { execFileSync, spawnSync } = require("node:child_process");
const { scopeHash } = require("./dependency-audit-policy.cjs");
const SCOPE = "974e885031fcfca8eff952c48b7791be9cacc9d934ddafdfaae264252e3eaa23";
const forbiddenModule = id => /(?:^|\/)node_modules\/(?:picomatch|fdir|tinyglobby|vite|vitest|@vitejs\/plugin-react)(?:\/|$)/i.test(id.replaceAll("\\", "/"));
const digest = bytes => createHash("sha256").update(bytes).digest("hex");
function filesUnder(base, current = base) {
  const result = [];
  for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
    assert.equal(entry.isSymbolicLink(), false, "Artifact symlinks are not permitted");
    const absolute = path.join(current, entry.name);
    if (entry.isDirectory()) result.push(...filesUnder(base, absolute));
    else if (entry.isFile()) result.push(absolute);
    else throw new Error("Unsupported artifact file type");
  }
  return result;
}
function snapshot(base) {
  return filesUnder(base).map(file => ({ file: path.relative(base, file).replaceAll("\\", "/"), sha256: digest(fs.readFileSync(file)) }))
    .sort((a, b) => a.file.localeCompare(b.file, "en"));
}
function archivePaths(text) {
  const separator = /^-{10,}\r?$/m.exec(text);
  assert.ok(separator, "Archive listing has no entry separator");
  const entries = [...text.slice(separator.index + separator[0].length).matchAll(/^Path = (.+)\r?$/gm)].map(match => match[1].trim());
  assert.ok(entries.length > 0, "Installer contains no inspectable entries");
  for (const entry of entries) {
    const normalized = entry.replaceAll("\\", "/");
    assert.ok(!normalized.startsWith("/") && !/^[a-z]:/i.test(normalized) && !normalized.split("/").includes(".."), "Unsafe installer entry path");
    assert.ok(!/(?:^|\/)(?:node_modules|node\.exe|npm(?:\.cmd|\.exe)?|package(?:-lock)?\.json|picomatch|tinyglobby|fdir)(?:\/|$)/i.test(normalized), "Development dependency/runtime in installer payload");
  }
  return entries;
}
function assertWindowsExecutable(bytes) {
  assert.equal(bytes.subarray(0, 2).toString(), "MZ", "Expected Windows executable");
  assert.ok(bytes.length > 64);
  const pe = bytes.readUInt32LE(0x3c);
  assert.ok(pe + 6 <= bytes.length);
  assert.equal(bytes.subarray(pe, pe + 4).toString("binary"), "PE\0\0");
  assert.equal(bytes.readUInt16LE(pe + 4), 0x8664, "Expected x64 application payload");
}
module.exports = { forbiddenModule, archivePaths, assertWindowsExecutable, snapshot };

async function main() {
  assert.equal(process.argv.length, 5, "Usage: node inspect-production-artifacts.cjs <capture|installer> --root <repo>");
  const mode = process.argv[2]; assert.ok(["capture", "installer"].includes(mode)); assert.equal(process.argv[3], "--root");
  assert.equal(process.versions.node.split(".")[0], "24", "Production validation requires Node24");
  const root = path.resolve(process.argv[4]);
  const policy = JSON.parse(fs.readFileSync(path.join(__dirname, "picomatch-exception.json"), "utf8"));
  assert.equal(scopeHash(policy), SCOPE, "Approved inputs changed");
  for (const [tree, expected] of Object.entries(policy.trees))
    assert.equal(execFileSync("git", ["rev-parse", `HEAD:${tree}`], { cwd: root, encoding: "utf8" }).trim(), expected, "Frozen source tree changed");
  const app = path.join(root, "src/frontend/apps/web"), dist = path.join(app, "dist");
  const config = JSON.parse(fs.readFileSync(path.join(root, "src/desktop/src-tauri/tauri.conf.json"), "utf8"));
  assert.equal(path.resolve(root, "src/desktop/src-tauri", config.build.frontendDist), dist, "Tauri embeds different frontend inputs");
  const reportDir = path.join(process.env.RUNNER_TEMP, "hasm-production-inspection");
  fs.mkdirSync(reportDir, { recursive: true });
  const manifestFile = path.join(reportDir, "web-manifest.json");
  if (mode === "capture") {
    const { build } = await import(pathToFileURL(path.join(root, "src/frontend/node_modules/vite/dist/node/index.js")).href);
    let inventory;
    await build({ root: app, configFile: path.join(app, "vite.config.ts"), mode: "production", build: { write: true }, plugins: [{
      name: "hasm-production-inventory", enforce: "post",
      writeBundle(_options, bundle) {
        const modules = [...this.getModuleIds()].map(id => id.replaceAll("\\", "/"));
        assert.ok(modules.length > 0);
        assert.deepEqual(modules.filter(forbiddenModule), [], "Vulnerable development tools must not enter the production graph");
        inventory = { commit: process.env.GITHUB_SHA, node: process.version, platform: process.platform,
          moduleIds: modules.map(id => id.replace(root.replaceAll("\\", "/"), "<workspace>")),
          assets: Object.values(bundle).map(asset => ({ file: asset.fileName, sha256: digest(asset.type === "chunk" ? asset.code : asset.source) }))
            .sort((a, b) => a.file.localeCompare(b.file, "en")) };
      }
    }] });
    assert.ok(inventory);
    assert.equal(fs.realpathSync(path.resolve(root, "src/desktop/src-tauri", config.build.frontendDist)), fs.realpathSync(dist));
    assert.deepEqual(snapshot(dist), inventory.assets, "Written assets differ from the inspected final bundle");
    fs.writeFileSync(manifestFile, JSON.stringify(inventory, null, 2));
    console.log(`Inspected ${inventory.moduleIds.length} production modules and ${inventory.assets.length} written assets; no affected tool modules.`);
    return;
  }
  assert.equal(process.platform, "win32", "Installer inspection requires Windows");
  const inventory = JSON.parse(fs.readFileSync(manifestFile, "utf8"));
  assert.equal(inventory.commit, process.env.GITHUB_SHA);
  assert.deepEqual(snapshot(dist), inventory.assets, "Tauri rebuild changed the inspected Web assets");
  const target = path.join(root, "src/desktop/src-tauri/target/release");
  const installers = filesUnder(path.join(target, "bundle/nsis")).filter(file => file.endsWith(".exe"));
  assert.equal(installers.length, 1, "Expected exactly one newly built release installer");
  const executable = path.join(target, "hasm-desktop.exe");
  assertWindowsExecutable(fs.readFileSync(executable));
  const sevenZip = path.join(process.env.ProgramFiles, "7-Zip/7z.exe");
  const listing = spawnSync(sevenZip, ["l", "-slt", "-sccUTF-8", installers[0]], { encoding: "utf8", timeout: 60000, windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
  assert.equal(listing.status, 0, "Cannot inspect NSIS release installer");
  const entries = archivePaths(listing.stdout);
  const installerHash = digest(fs.readFileSync(installers[0]));
  const extraction = path.join(reportDir, "nsis-extracted-" + installerHash.slice(0, 16));
  const relative = path.relative(fs.realpathSync(reportDir), path.resolve(extraction));
  assert.ok(relative && !relative.startsWith("..") && !path.isAbsolute(relative));
  fs.mkdirSync(extraction); // Fresh private path only; nothing is recursively deleted or overwritten.
  const unpack = spawnSync(sevenZip, ["x", "-y", "-o" + extraction, installers[0]], { encoding: "utf8", timeout: 60000, windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
  assert.equal(unpack.status, 0, "Cannot extract release payload for inspection");
  const payload = filesUnder(extraction);
  const application = payload.filter(file => path.basename(file).toLowerCase() === "hasm-desktop.exe");
  assert.equal(application.length, 1, "Installer application payload is missing or ambiguous");
  fs.writeFileSync(path.join(reportDir, "binary-comparison.json"), JSON.stringify({
    compiled: { sha256: digest(fs.readFileSync(executable)), size: fs.statSync(executable).size },
    extracted: { sha256: digest(fs.readFileSync(application[0])), size: fs.statSync(application[0]).size }
  }, null, 2));
  assert.equal(digest(fs.readFileSync(application[0])), digest(fs.readFileSync(executable)), "Installer does not contain the inspected release executable");
  const result = { commit: process.env.GITHUB_SHA, node: process.version, platform: process.platform, webAssets: inventory.assets,
    installer: path.basename(installers[0]), installerSha256: installerHash, applicationSha256: digest(fs.readFileSync(executable)),
    archiveEntries: entries, extractedPayload: snapshot(extraction), nodeRuntimeOrDependencyPayload: false, installerExecuted: false };
  fs.writeFileSync(path.join(reportDir, "tauri-installer.json"), JSON.stringify(result, null, 2));
  console.log("Release NSIS payload verified against the compiled application and inspected Web inputs; installer was not executed.");
}
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
