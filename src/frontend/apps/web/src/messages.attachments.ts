// Attachments (Phase 3.12). Merged into the catalog by messages.ts.
export const attachmentsAr: Record<string, string> = {
  "att.tab": "المرفقات", "att.title": "المرفقات",
  "att.hint": "تُحفظ المرفقات كملفات على الخادم وبياناتها الوصفية في قاعدة الشركة، ولا يصل إليها إلا الخادم بعد التحقق من الصلاحية. يُتحقق من بصمة كل ملف عند كل قراءة. الملف الحساس يُشفَّر على مستوى التطبيق ولا يظهر إلا لمن يملك صلاحية المرفقات الحساسة.",
  "att.empty": "لا توجد مرفقات.", "att.name": "الملف", "att.size": "الحجم", "att.type": "النوع", "att.added": "أُضيف في", "att.actions": "إجراءات",
  "att.sensitive": "حساس (مشفّر)", "att.sensitive_option": "مرفق حساس (يُشفَّر ويقتصر على من يملك الصلاحية)", "att.choose_file": "اختر ملفاً", "att.upload": "رفع", "att.uploaded": "تم رفع الملف.",
  "att.download": "تنزيل", "att.remove": "إزالة", "att.removed": "أُزيل المرفق.", "att.reason": "سبب الإزالة", "att.remove_title": "إزالة المرفق",
  "att.remove_message": "لن يعود الملف متاحاً من التطبيق. يبقى سجله وأثره في التدقيق. اكتب سبب الإزالة.", "att.confirm": "إزالة", "att.cancel": "إلغاء", "att.allowed": "الأنواع المسموحة: PDF وPNG وJPEG ونص وCSV وWord وExcel.",
  "att.error.integrity_failure": "فشل التحقق من سلامة الملف: لم يُسلَّم. سُجّل الفشل في سجل التدقيق.", "att.error.attachment_not_found": "المرفق غير موجود.", "att.error.file_too_large": "الملف أكبر من الحد المسموح.",
  "att.error.encryption_key_unavailable": "مفتاح التشفير غير متاح على الخادم: لا يمكن إتمام العملية.", "att.error.encryption_key_invalid": "مفتاح التشفير غير صالح على الخادم.", "att.error.encryption_not_configured": "التشفير غير مُعدّ على الخادم.",
  "att.error.attachment_storage_not_configured": "مجلد المرفقات غير مُعدّ على الخادم.", "att.error.already_removed": "أُزيل المرفق مسبقاً.", "att.error.revision_conflict": "تغيّر المرفق في مكان آخر.",
  "att.error.invalid_request": "الطلب غير مكتمل.", "att.error.concurrent_change": "تعارض تغيير متزامن: أعد المحاولة.", "att.error.precondition_required": "تعذّر التحقق من نسخة السجل.",
  "field.file_empty": "الملف فارغ.", "field.file_too_large": "الملف أكبر من الحد المسموح.", "field.content_type_mismatch": "محتوى الملف لا يطابق نوعه.", "field.content_type_not_allowed": "نوع الملف غير مسموح.",
  "field.subject_not_found": "السجل غير موجود.", "field.subject_kind_invalid": "نوع السجل غير صالح.", "field.file_name_invalid": "اسم الملف غير صالح.", "field.too_long": "النص أطول من الحد المسموح.",
};

export const attachmentsEn: Record<string, string> = {
  "att.tab": "Attachments", "att.title": "Attachments",
  "att.hint": "Attachments are files kept on the server with their metadata in the company database; only the server reaches them, after checking permission. The hash of every file is verified on every read. A sensitive file is encrypted at application level and is visible only to users with the sensitive attachments permission.",
  "att.empty": "No attachments.", "att.name": "File", "att.size": "Size", "att.type": "Type", "att.added": "Added", "att.actions": "Actions",
  "att.sensitive": "Sensitive (encrypted)", "att.sensitive_option": "Sensitive attachment (encrypted, limited to users with the permission)", "att.choose_file": "Choose a file", "att.upload": "Upload", "att.uploaded": "The file was uploaded.",
  "att.download": "Download", "att.remove": "Remove", "att.removed": "The attachment was removed.", "att.reason": "Reason for removal", "att.remove_title": "Remove the attachment",
  "att.remove_message": "The file will no longer be available from the application. Its record and audit trail remain. Enter the reason for removal.", "att.confirm": "Remove", "att.cancel": "Cancel", "att.allowed": "Allowed types: PDF, PNG, JPEG, text, CSV, Word and Excel.",
  "att.error.integrity_failure": "The file failed its integrity check and was not delivered. The failure is recorded in the audit trail.", "att.error.attachment_not_found": "The attachment does not exist.", "att.error.file_too_large": "The file is larger than the allowed limit.",
  "att.error.encryption_key_unavailable": "The encryption key is unavailable on the server: the operation cannot be completed.", "att.error.encryption_key_invalid": "The encryption key on the server is invalid.", "att.error.encryption_not_configured": "Encryption is not configured on the server.",
  "att.error.attachment_storage_not_configured": "The attachments folder is not configured on the server.", "att.error.already_removed": "The attachment was already removed.", "att.error.revision_conflict": "The attachment was changed elsewhere.",
  "att.error.invalid_request": "The request is incomplete.", "att.error.concurrent_change": "A concurrent change conflicted: try again.", "att.error.precondition_required": "The record revision could not be verified.",
  "field.file_empty": "The file is empty.", "field.file_too_large": "The file is larger than the allowed limit.", "field.content_type_mismatch": "The file content does not match its type.", "field.content_type_not_allowed": "This file type is not allowed.",
  "field.subject_not_found": "The record does not exist.", "field.subject_kind_invalid": "Invalid record kind.", "field.file_name_invalid": "Invalid file name.", "field.too_long": "The text is longer than allowed.",
};
