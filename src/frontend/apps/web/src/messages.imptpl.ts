// Reusable import mapping templates (Phase 3.9C). Merged into the catalog by messages.ts.
export const impTplAr: Record<string, string> = {
  "imp.tpl.title": "قوالب الربط", "imp.tpl.hint": "يحفظ القالب ربط الحقول بعناوين الأعمدة (لا بمواضعها) لتطبيقه على ملفات أخرى للمحوّل نفسه. التطبيق يملأ النموذج أدناه فقط: حفظ الربط والتحقق والتجربة والموافقة تبقى كما هي.",
  "imp.tpl.apply_label": "تطبيق قالب", "imp.tpl.apply": "تطبيق", "imp.tpl.applied": "طُبّق القالب: راجع الربط ثم احفظه.",
  "imp.tpl.applied_unmatched": "طُبّق القالب ولم يُعثر على عمود ل {n} حقل/حقول: {fields}. راجع الربط ثم احفظه.",
  "imp.tpl.save_label": "حفظ الربط الحالي كقالب", "imp.tpl.save_hint": "يُحفظ الربط المحفوظ للدفعة كنسخة جديدة إن وُجد قالب بالاسم نفسه.", "imp.tpl.save_first": "احفظ ربط الدفعة أولاً.",
  "imp.tpl.save": "حفظ القالب", "imp.tpl.retire": "إيقاف استخدام القالب", "imp.tpl.retire_title": "إيقاف استخدام القالب", "imp.tpl.retire_message": "لن يُعرض القالب «⁨{name}⁩» بعد الآن للتطبيق.", "imp.tpl.retired": "أُوقف استخدام القالب.", "imp.tpl.retire_missing": "القالب المحدد لم يعد في القائمة: حُدّثت القائمة، اختره من جديد.", "imp.tpl.busy": "عملية على القوالب قيد التنفيذ، انتظر انتهاءها.", "imp.tpl.saved": "حُفظ القالب.",
  "imp.error.default_too_long": "القيمة الافتراضية أطول من 400 حرف: لا تصلح لقالب.",
  "imp.error.header_unusable": "عنوان عمود فارغ أو طويل لا يصلح لقالب.", "imp.error.header_ambiguous": "عنوانان متطابقان بعد التوحيد: لا يمكن حفظ قالب موثوق.",
  "imp.error.batch_not_mapped": "لا ربط محفوظاً لهذه الدفعة.", "imp.error.template_not_found": "القالب غير موجود أو أُوقف استخدامه.", "imp.error.already_retired": "أُوقف استخدام القالب مسبقاً.",
  "imp.error.version_conflict": "حُفظت نسخة أخرى من القالب في الوقت نفسه.", "imp.error.template_incompatible": "القالب لا يطابق حقول المحوّل الحالية.", "imp.error.name_invalid": "اسم القالب مطلوب وبحد أقصى 100 حرف.",
};

export const impTplEn: Record<string, string> = {
  "imp.tpl.title": "Mapping templates", "imp.tpl.hint": "A template stores which header feeds each field (never column positions) so it can be applied to other files of the same adapter. Applying only fills the form below: saving the mapping, validation, dry run and approval are unchanged.",
  "imp.tpl.apply_label": "Apply a template", "imp.tpl.apply": "Apply", "imp.tpl.applied": "The template was applied: review the mapping, then save it.",
  "imp.tpl.applied_unmatched": "The template was applied; no column was found for {n} field(s): {fields}. Review the mapping, then save it.",
  "imp.tpl.save_label": "Save the current mapping as a template", "imp.tpl.save_hint": "The batch's SAVED mapping is stored as a new version when a template with the same name exists.", "imp.tpl.save_first": "Save the batch mapping first.",
  "imp.tpl.save": "Save template", "imp.tpl.retire": "Retire", "imp.tpl.retire_title": "Retire the template", "imp.tpl.retire_message": "The template \"{name}\" will no longer be offered for applying.", "imp.tpl.retired": "The template was retired.", "imp.tpl.retire_missing": "The selected template is no longer in the list: the list was refreshed, choose it again.", "imp.tpl.busy": "A template operation is in progress; wait for it to finish.", "imp.tpl.saved": "The template was saved.",
  "imp.error.default_too_long": "A default longer than 400 characters cannot be part of a template.",
  "imp.error.header_unusable": "An empty or overlong column header cannot be part of a template.", "imp.error.header_ambiguous": "Two headers are identical after normalization: a reliable template cannot be saved.",
  "imp.error.batch_not_mapped": "This batch has no saved mapping.", "imp.error.template_not_found": "The template does not exist or is retired.", "imp.error.already_retired": "The template is already retired.",
  "imp.error.version_conflict": "Another version of the template was saved at the same time.", "imp.error.template_incompatible": "The template does not match the current fields of the adapter.", "imp.error.name_invalid": "The template name is required, at most 100 characters.",
};
