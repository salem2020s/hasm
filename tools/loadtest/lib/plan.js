"use strict";
// Reference-data plan: a pure, seeded description of what to create. No I/O. Record i of kind k depends only on (seed, k, i).
const crypto = require("crypto");
const { rng } = require("./prng");

const EN_ADJ = ["Fresh", "Dry", "Frozen", "Organic", "Premium", "Classic", "Large", "Small", "Mixed", "Natural", "Roasted", "Salted", "Sweet", "Spicy", "Light"];
const EN_NOUN = ["Olive Oil", "Rice", "Sugar", "Flour", "Tea", "Coffee", "Dates", "Honey", "Cheese", "Yogurt", "Lentils", "Chickpeas", "Pasta", "Tomato Paste", "Juice", "Water", "Detergent", "Soap", "Shampoo", "Paper Towel"];
const AR_ADJ = ["طازج", "جاف", "مجمد", "عضوي", "فاخر", "كلاسيكي", "كبير", "صغير", "مشكل", "طبيعي", "محمص", "مملح", "حلو", "حار", "خفيف"];
const AR_NOUN = ["زيت زيتون", "أرز", "سكر", "طحين", "شاي", "قهوة", "تمر", "عسل", "جبنة", "لبن", "عدس", "حمص", "معكرونة", "معجون بندورة", "عصير", "ماء", "منظف", "صابون", "شامبو", "مناديل"];
const FIRST = ["Ahmad", "Omar", "Khaled", "Layla", "Sara", "Hana", "Yousef", "Nour", "Rania", "Samir", "Tariq", "Dina", "Fadi", "Maya", "Bilal"];
const FAMILY = ["Haddad", "Nasser", "Khoury", "Saleh", "Mansour", "Abbas", "Taha", "Qasem", "Zayed", "Masri", "Suleiman", "Barakat"];
const AR_FIRST = ["أحمد", "عمر", "خالد", "ليلى", "سارة", "هناء", "يوسف", "نور", "رانيا", "سمير", "طارق", "دينا", "فادي", "مايا", "بلال"];
const AR_FAMILY = ["حداد", "ناصر", "خوري", "صالح", "منصور", "عباس", "طه", "قاسم", "زايد", "المصري", "سليمان", "بركات"];
const pad = (n, w = 6) => String(n).padStart(w, "0");

const itemCode = (i) => `LT-I-${pad(i)}`;
const partyCode = (i) => `LT-P-${pad(i)}`;

function item(seed, i) {
  const r = rng(seed, "item", i); const a = r.int(0, EN_ADJ.length - 1), n = r.int(0, EN_NOUN.length - 1);
  return { code: itemCode(i), nameEn: `${EN_ADJ[a]} ${EN_NOUN[n]} ${pad(i)}`, nameAr: `${AR_NOUN[n]} ${AR_ADJ[a]} ${pad(i)}`, itemType: 1 };
}
function party(seed, i) {
  const r = rng(seed, "party", i); const f = r.int(0, FIRST.length - 1), l = r.int(0, FAMILY.length - 1);
  return { code: partyCode(i), displayName: `${FIRST[f]} ${FAMILY[l]} ${AR_FIRST[f]} ${AR_FAMILY[l]} ${pad(i)}`, legalNameAr: `${AR_FIRST[f]} ${AR_FAMILY[l]} ${pad(i)}`, legalNameEn: `${FIRST[f]} ${FAMILY[l]} ${pad(i)}`, customer: r.next() < 0.6 };
}
/** A balanced entry of 2 or 4 lines between distinct postable accounts (accounts = array of ids). Amounts have 2 decimals. */
function journal(seed, i, accountIds, year) {
  const r = rng(seed, "journal", i); const lines = []; const pairs = r.pick([1, 1, 2]);
  for (let p = 0; p < pairs; p++) {
    const dr = r.int(0, accountIds.length - 1); let cr = r.int(0, accountIds.length - 1); if (cr === dr) cr = (cr + 1) % accountIds.length;
    const amount = r.int(100, 9999999) / 100;
    lines.push({ accountId: accountIds[dr], description: `LT line ${i}.${p * 2}`, debit: amount, credit: 0 }, { accountId: accountIds[cr], description: `LT line ${i}.${p * 2 + 1}`, debit: 0, credit: amount });
  }
  const month = r.int(1, 12), day = r.int(1, 28);
  return { entryDate: `${year}-${pad(month, 2)}-${pad(day, 2)}`, description: `LT entry ${pad(i)}`, lines };
}
/** Number of receipts: the first documents (indices 1..receiptCount) are receipts, one cycle of warehouses at least, so issues and transfers always come after receipts. */
function stockReceiptCount(total, warehouses) { return Math.min(total, Math.max(Math.ceil(total * 0.6), warehouses)); }
/**
 * docType 1 receipt, 2 issue, 3 transfer; items are INDEXES into the seeded item space. Receipts come first (indices <= stockReceiptCount) and
 * cycle through the warehouses; item k is only ever stocked in warehouse (k-1) % W, so an issue or transfer draws only items that receipts of
 * that warehouse can have brought in. Quantities are not tracked (approving a stock document changes no stock figure).
 */
function stockDoc(seed, i, { items, warehouses, year, total }) {
  const r = rng(seed, "stockdoc", i); const W = Math.max(1, Math.min(warehouses, items));
  const receipts = stockReceiptCount(total ?? i, W);
  const docType = i <= receipts ? 1 : W < 2 ? 2 : r.next() < 0.6 ? 2 : 3;
  const w1 = docType === 1 ? (i - 1) % W : r.int(0, W - 1); const w2 = docType === 3 ? (w1 + 1 + r.int(0, W - 2)) % W : null;
  const pickItem = () => 1 + w1 + W * r.int(0, Math.floor((items - 1 - w1) / W));
  const seen = new Set(); const lines = [];
  for (let n = r.int(1, 5); n > 0; n--) { const k = pickItem(); if (!seen.has(k)) { seen.add(k); lines.push({ itemIndex: k, quantity: r.int(1, 50) }); } }
  return { docType, sourceWarehouse: docType === 1 ? null : w1, targetWarehouse: docType === 1 ? w1 : docType === 3 ? w2 : null, documentDate: `${year}-${pad(r.int(1, 12), 2)}-${pad(r.int(1, 28), 2)}`, reference: `LT-S-${pad(i)}`, lines };
}

/** A digest over the whole plan: two machines with the same seed and counts get the same value, so "same data" is checkable. */
function planDigest({ seed, items, parties, journals, stockDocs, warehouses = 2, accounts = 8, year = 2026 }) {
  const h = crypto.createHash("sha256"); const add = (x) => h.update(JSON.stringify(x) + "\n");
  add({ seed, items, parties, journals, stockDocs, warehouses, accounts, year });
  for (let i = 1; i <= items; i++) add(item(seed, i));
  for (let i = 1; i <= parties; i++) add(party(seed, i));
  const ids = Array.from({ length: accounts }, (_, k) => `acc-${k}`);
  for (let i = 1; i <= journals; i++) add(journal(seed, i, ids, year));
  for (let i = 1; i <= stockDocs; i++) add(stockDoc(seed, i, { items, warehouses, year, total: stockDocs }));
  return h.digest("hex");
}
module.exports = { stockReceiptCount, item, party, journal, stockDoc, planDigest, itemCode, partyCode, EN_NOUN, EN_ADJ, AR_NOUN, FIRST, FAMILY, AR_FIRST };
