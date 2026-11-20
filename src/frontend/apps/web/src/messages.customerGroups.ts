// Customer groups of the pricing screen (Phase 3.11, company migration 0027). Merged into the catalog by messages.ts. Arabic and English carry exactly the same keys.
export const customerGroupsAr: Record<string, string> = {
  "pricing.tab.groups": "مجموعات العملاء",
  "pricing.groups.hint": "مجموعة العملاء تتيح قوائم أسعار وخصومات لمجموعة كاملة. العميل ينتمي إلى مجموعة واحدة على الأكثر في كل وقت، ولا تُحذف المجموعات: تُعطَّل.",
  "pricing.groups.empty": "لا توجد مجموعات عملاء بعد.", "pricing.groups.create": "إنشاء مجموعة عملاء", "pricing.groups.edit": "تعديل الأسماء", "pricing.groups.list_label": "مجموعات العملاء",
  "pricing.groups.assignments": "انتماء العملاء", "pricing.groups.assignments_empty": "لا يوجد عملاء (أطراف بدور «عميل») بعد.", "pricing.groups.assignments_hint": "يختار المسؤول مجموعة واحدة لكل عميل أو «بدون مجموعة». التغيير يسري على التسعير الجديد فقط.",
  "pricing.groups.deactivate_note": "تعطيل مجموعة لا يغيّر قوائم الأسعار وقواعد الخصم ولا انتماء العملاء القائمة: تبقى سارية. عطّل القوائم والقواعد نفسها لإيقاف تسعير المجموعة، ويمكن نقل العملاء إلى مجموعة أخرى.",
  "field.ambiguous_rule": "توجد قاعدة فعّالة بالنطاق والأولوية والكمية وتاريخ البدء نفسها. لقواعد المجموعات استخدم أولوية أو تاريخ بدء أو حداً أدنى للكمية مختلفاً لكل مجموعة.",
  "pricing.groups.none": "بدون مجموعة", "pricing.groups.party": "العميل", "pricing.groups.group": "المجموعة", "pricing.groups.assign": "حفظ المجموعة",
  "pricing.customer_group": "مجموعة العملاء", "pricing.customer_group_hint": "اختيارية: عميل واحد أو مجموعة عملاء، وليس كلاهما.", "pricing.all_groups": "بدون مجموعة",
  "pricing.scope_customer_group": "مجموعة",
  "field.customer_or_group_not_both": "اختر عميلاً أو مجموعة عملاء، وليس كلاهما.", "field.customer_group_immutable": "مجموعة العملاء لا تتغير.",
  "field.customer_group_not_found": "مجموعة العملاء غير موجودة.", "field.customer_group_inactive": "مجموعة العملاء معطّلة.",
  "pricing.error.customer_group_not_found": "مجموعة العملاء غير موجودة.",
};

export const customerGroupsEn: Record<string, string> = {
  "pricing.tab.groups": "Customer groups",
  "pricing.groups.hint": "A customer group lets one price list or discount rule cover a whole group. A customer belongs to at most one group at a time, and groups are never deleted: they are deactivated.",
  "pricing.groups.empty": "No customer groups yet.", "pricing.groups.create": "Create customer group", "pricing.groups.edit": "Edit names", "pricing.groups.list_label": "Customer groups",
  "pricing.groups.assignments": "Customer membership", "pricing.groups.assignments_empty": "No customers (parties with a customer role) yet.", "pricing.groups.assignments_hint": "Choose one group per customer or \"No group\". A change applies to new pricing only.",
  "pricing.groups.deactivate_note": "Deactivating a group does not change existing price lists, discount rules or customer memberships: they stay in force. Deactivate the lists and rules themselves to stop group pricing, and move customers to another group if needed.",
  "field.ambiguous_rule": "An active rule with the same scope, priority, minimum quantity and start already exists. For group rules use a different priority, start date or minimum quantity per group.",
  "pricing.groups.none": "No group", "pricing.groups.party": "Customer", "pricing.groups.group": "Group", "pricing.groups.assign": "Save group",
  "pricing.customer_group": "Customer group", "pricing.customer_group_hint": "Optional: one customer or one customer group, not both.", "pricing.all_groups": "No group",
  "pricing.scope_customer_group": "Group",
  "field.customer_or_group_not_both": "Choose a customer or a customer group, not both.", "field.customer_group_immutable": "The customer group never changes.",
  "field.customer_group_not_found": "The customer group does not exist.", "field.customer_group_inactive": "The customer group is inactive.",
  "pricing.error.customer_group_not_found": "The customer group does not exist.",
};
