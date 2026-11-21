"use strict";
// Deterministic pseudo-randomness. Every generated record is derived from (seed, kind, index) alone, so any record can be regenerated
// independently, in any order, on any machine; nothing depends on call order, time or the process.
function xmur3(str) {
  let h = 1779033703 ^ str.length;
  for (let i = 0; i < str.length; i++) { h = Math.imul(h ^ str.charCodeAt(i), 3432918353); h = (h << 13) | (h >>> 19); }
  return () => { h = Math.imul(h ^ (h >>> 16), 2246822507); h = Math.imul(h ^ (h >>> 13), 3266489909); return (h ^= h >>> 16) >>> 0; };
}
function mulberry32(a) {
  return () => { a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
/** A generator for one record: rng(seed, "item", 17). */
function rng(seed, kind, index) {
  const next = mulberry32(xmur3(`${seed}|${kind}|${index}`)());
  return {
    next,
    int: (lo, hi) => lo + Math.floor(next() * (hi - lo + 1)),          // inclusive
    pick: (arr) => arr[Math.floor(next() * arr.length)],
  };
}
module.exports = { rng };
