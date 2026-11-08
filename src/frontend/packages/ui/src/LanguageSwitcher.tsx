import { useI18n } from "./i18n";

export function LanguageSwitcher() {
  const { locale, setLocale } = useI18n();
  return (
    <button type="button" onClick={() => setLocale(locale === "ar" ? "en" : "ar")}>
      {locale === "ar" ? "English" : "العربية"}
    </button>
  );
}
