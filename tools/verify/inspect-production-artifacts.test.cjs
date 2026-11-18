"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { forbiddenModule, archivePaths, assertWindowsExecutable, expectedNsisPayload } = require("./inspect-production-artifacts.cjs");
test("production inventory rejects each reviewed tool including nested and Windows paths", () => {
  for (const name of ["picomatch", "fdir", "tinyglobby", "vite", "vitest", "@vitejs/plugin-react"])
    for (const prefix of ["/workspace/node_modules/", "C:\\app\\node_modules\\vite\\node_modules\\"])
      assert.equal(forbiddenModule(prefix + name + "/index.js"), true);
  for (const id of ["/src/vite-helper.ts", "\0vite/preload-helper.js", "/node_modules/react/index.js"])
    assert.equal(forbiddenModule(id), false);
});
test("installer paths reject traversal, absolute paths and Node/dependency payloads", () => {
  const listing = entry => "Archive metadata\n----------\nPath = " + entry + "\nSize = 123\n";
  assert.deepEqual(archivePaths(listing("hasm-desktop.exe")), ["hasm-desktop.exe"]);
  for (const entry of ["../outside", "C:\\outside", "/outside", "app/node.exe", "node_modules/picomatch/index.js", "app/package-lock.json", "app/npm.cmd"])
    assert.throws(() => archivePaths(listing(entry)));
  assert.throws(() => archivePaths("unknown listing"));
  assert.throws(() => archivePaths("----------\n"));
});
test("release application must be an x64 PE executable", () => {
  const bytes = Buffer.alloc(128); bytes.write("MZ"); bytes.writeUInt32LE(80, 60); bytes.write("PE\0\0", 80, "binary"); bytes.writeUInt16LE(0x8664, 84);
  assertWindowsExecutable(bytes);
  bytes.writeUInt16LE(0x14c, 84); assert.throws(() => assertWindowsExecutable(bytes));
  assert.throws(() => assertWindowsExecutable(Buffer.from("not an executable")));
});
test("NSIS integrity predicts only Tauri's unique bundle marker and preserves every other byte", () => {
  const compiled = Buffer.from("prefix\0__TAURI_BUNDLE_TYPE_VAR_UNK\0suffix"), before = Buffer.from(compiled);
  const { bytes, markerOffset } = expectedNsisPayload(compiled);
  assert.equal(markerOffset, 7); assert.deepEqual(compiled, before);
  assert.deepEqual(bytes, Buffer.from("prefix\0__TAURI_BUNDLE_TYPE_VAR_NSS\0suffix"));
  for (const offset of [0, 6, markerOffset, markerOffset + 23, bytes.length - 1]) {
    const tampered = Buffer.from(bytes); tampered[offset] ^= 1; assert.notDeepEqual(tampered, expectedNsisPayload(compiled).bytes);
  }
  for (const input of [Buffer.from("no marker"), Buffer.concat([compiled, compiled]), bytes, Buffer.concat([compiled, bytes])])
    assert.throws(() => expectedNsisPayload(input));
});
