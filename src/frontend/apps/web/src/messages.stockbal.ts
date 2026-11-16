// Stock balances, item ledger and reconciliation (Phase 3.8A). Arabic and English; merged into the catalog by messages.ts.
export const stockbalAr: Record<string, string> = {
  "nav.stock_balances": "أرصدة المخزون",
  "stockbal.title": "أرصدة المخزون", "stockbal.subtitle": "الكميات والقيم الحالية وبطاقة المادة والمطابقة مع الأستاذ العام (قراءة فقط).",
  "stockbal.tabs_label": "أقسام أرصدة المخزون", "stockbal.tab.balances": "الأرصدة", "stockbal.tab.ledger": "بطاقة المادة", "stockbal.tab.reconciliation": "المطابقة مع الأستاذ",
  "stockbal.search": "بحث", "stockbal.search_hint": "رمز المادة أو اسمها", "stockbal.as_of": "حتى تاريخ", "stockbal.as_of_hint": "اتركه فارغاً لعرض الرصيد الحالي",
  "stockbal.include_zero": "إظهار الأرصدة الصفرية", "stockbal.none": "لا توجد أرصدة.",
  "stockbal.quantity": "الكمية", "stockbal.value": "القيمة", "stockbal.avg_cost": "متوسط التكلفة", "stockbal.method": "طريقة التكلفة",
  "stockbal.method.1": "الوارد أولاً صادر أولاً", "stockbal.method.2": "المتوسط المتحرك",
  "stockbal.open_ledger": "بطاقة المادة", "stockbal.total_value": "إجمالي القيمة",
  "stockbal.ledger_hint": "اختر مادة ومستودعاً لعرض حركاتها بالترتيب مع الرصيد الجاري.", "stockbal.show_ledger": "عرض البطاقة",
  "stockbal.kind": "النوع", "stockbal.kind.1": "استلام", "stockbal.kind.2": "إخراج", "stockbal.kind.3": "تحويل صادر", "stockbal.kind.4": "تحويل وارد",
  "stockbal.in": "وارد", "stockbal.out": "صادر", "stockbal.amount": "القيمة", "stockbal.quantity_after": "الكمية بعد الحركة", "stockbal.value_after": "القيمة بعد الحركة",
  "stockbal.opening": "الرصيد الافتتاحي", "stockbal.closing": "الرصيد الختامي",
  "stockbal.rec_hint": "يقارن قيمة المخزون من سجل التكلفة برصيد حسابات المخزون في الأستاذ العام حتى التاريخ المحدد.", "stockbal.run_reconciliation": "تنفيذ المطابقة",
  "stockbal.costing_value": "قيمة المخزون (سجل التكلفة)", "stockbal.ledger_value": "رصيد الأستاذ العام", "stockbal.difference": "الفرق", "stockbal.account": "الحساب",
  "stockbal.rec_ok": "لا فرق: المخزون مطابق للأستاذ العام.", "stockbal.rec_difference": "يوجد فرق بين سجل التكلفة والأستاذ العام.",

};
export const stockbalEn: Record<string, string> = {
  "nav.stock_balances": "Stock balances",
  "stockbal.title": "Stock balances", "stockbal.subtitle": "Current quantities and values, the item ledger and the reconciliation with the General Ledger (read-only).",
  "stockbal.tabs_label": "Stock balance sections", "stockbal.tab.balances": "Balances", "stockbal.tab.ledger": "Item ledger", "stockbal.tab.reconciliation": "Ledger reconciliation",
  "stockbal.search": "Search", "stockbal.search_hint": "Item code or name", "stockbal.as_of": "As of", "stockbal.as_of_hint": "Leave empty for the current balance",
  "stockbal.include_zero": "Show zero balances", "stockbal.none": "No balances.",
  "stockbal.quantity": "Quantity", "stockbal.value": "Value", "stockbal.avg_cost": "Average cost", "stockbal.method": "Costing method",
  "stockbal.method.1": "FIFO", "stockbal.method.2": "Moving average",
  "stockbal.open_ledger": "Item ledger", "stockbal.total_value": "Total value",
  "stockbal.ledger_hint": "Choose an item and a warehouse to see its movements in order with the running balance.", "stockbal.show_ledger": "Show ledger",
  "stockbal.kind": "Type", "stockbal.kind.1": "Receipt", "stockbal.kind.2": "Issue", "stockbal.kind.3": "Transfer out", "stockbal.kind.4": "Transfer in",
  "stockbal.in": "In", "stockbal.out": "Out", "stockbal.amount": "Amount", "stockbal.quantity_after": "Quantity after", "stockbal.value_after": "Value after",
  "stockbal.opening": "Opening balance", "stockbal.closing": "Closing balance",
  "stockbal.rec_hint": "Compares the stock value of the costing ledger with the balance of the inventory accounts in the General Ledger up to the chosen date.", "stockbal.run_reconciliation": "Run reconciliation",
  "stockbal.costing_value": "Stock value (costing ledger)", "stockbal.ledger_value": "General Ledger balance", "stockbal.difference": "Difference", "stockbal.account": "Account",
  "stockbal.rec_ok": "No difference: the stock agrees with the General Ledger.", "stockbal.rec_difference": "There is a difference between the costing ledger and the General Ledger.",

};
