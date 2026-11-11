using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>Server-side validation and normalisation (trim, NFC, empty to null). Country-neutral: no identifier formats are imposed.</summary>
public static partial class ConfigurationValidation
{
    public static readonly string[] Languages = ["ar", "en"];
    public static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy"];
    public static readonly string[] NumeralSystems = ["latin", "arabic-indic"];

    // ISO 3166-1 alpha-2 officially assigned codes.
    private static readonly HashSet<string> Countries = new(("AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ " +
        "CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY " +
        "HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ " +
        "NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ " +
        "TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW").Split(' '), StringComparer.Ordinal);

    [GeneratedRegex(@"^[\p{Nd}+()\-. ]{5,30}$")] private static partial Regex PhoneShape();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailShape();
    [GeneratedRegex(@"^[A-Za-z0-9_+/-]{1,64}$")] private static partial Regex TimeZoneShape();
    [GeneratedRegex(@"^[A-Z]{3}$")] private static partial Regex CurrencyCodeShape();

    public static bool IsCountry(string code) => Countries.Contains(code);
    public static bool IsCurrencyCodeShape(string? code) => code is not null && CurrencyCodeShape().IsMatch(code);

    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var t = value.Trim().Normalize(NormalizationForm.FormC);
        return t.Length == 0 ? null : t;
    }

    private static bool HasControl(string s) => s.Any(char.IsControl);

    private static string? Text(string? raw, string field, int max, List<FieldError> errors, bool multilineAllowed = false)
    {
        var v = Clean(raw);
        if (v is null) return null;
        if (v.Length > max) errors.Add(new(field, "too_long"));
        else if (HasControl(v)) errors.Add(new(field, "invalid_characters"));
        return v;
    }

    public static bool TryNormalizeProfile(ProfileInput? input, out ProfileData data, out List<FieldError> errors)
    {
        errors = [];
        data = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var nameAr = Text(input.NameAr, "nameAr", 200, errors);
        var nameEn = Text(input.NameEn, "nameEn", 200, errors);
        if (errors.Count == 0 && nameAr is null && nameEn is null) { errors.Add(new("nameAr", "name_required")); errors.Add(new("nameEn", "name_required")); }
        var legal = Text(input.LegalName, "legalName", 200, errors);
        var reg = Text(input.CommercialRegistrationNo, "commercialRegistrationNo", 64, errors);
        var tax = Text(input.TaxRegistrationNo, "taxRegistrationNo", 64, errors);
        var country = Clean(input.CountryCode)?.ToUpperInvariant();
        if (country is null) errors.Add(new("countryCode", "required"));
        else if (!Countries.Contains(country)) errors.Add(new("countryCode", "unknown_country"));
        var a1 = Text(input.AddressLine1, "addressLine1", 200, errors);
        var a2 = Text(input.AddressLine2, "addressLine2", 200, errors);
        var city = Text(input.City, "city", 100, errors);
        var region = Text(input.Region, "region", 100, errors);
        var postal = Text(input.PostalCode, "postalCode", 20, errors);
        var phone = Text(input.Phone, "phone", 30, errors);
        if (phone is not null && !PhoneShape().IsMatch(phone)) errors.Add(new("phone", "invalid_format"));
        var email = Text(input.Email, "email", 254, errors);
        if (email is not null && !EmailShape().IsMatch(email)) errors.Add(new("email", "invalid_format"));
        var website = Text(input.Website, "website", 200, errors);
        if (website is not null && !(Uri.TryCreate(website, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                                     && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host)))
            errors.Add(new("website", "invalid_url"));
        if (errors.Count > 0) return false;
        data = new ProfileData(nameAr, nameEn, legal, reg, tax, country!, a1, a2, city, region, postal, phone, email, website);
        return true;
    }

    public static bool IsKnownTimeZone(string id)
    {
        if (!TimeZoneShape().IsMatch(id)) return false;
        try { return TimeZoneInfo.TryFindSystemTimeZoneById(id, out _); }
        catch (Exception) { return false; }
    }

    /// <summary>Missing optional fields take the documented defaults; the time zone is never defaulted.</summary>
    public static bool TryNormalizeSettings(SettingsInput? input, out SettingsData data, out List<FieldError> errors)
    {
        errors = [];
        data = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        var d = SettingsData.Defaults;
        var lang = Clean(input.DefaultLanguage) ?? d.DefaultLanguage;
        if (!Languages.Contains(lang)) errors.Add(new("defaultLanguage", "unsupported_value"));
        var date = Clean(input.DateFormat) ?? d.DateFormat;
        if (!DateFormats.Contains(date)) errors.Add(new("dateFormat", "unsupported_value"));
        var num = Clean(input.NumeralSystem) ?? d.NumeralSystem;
        if (!NumeralSystems.Contains(num)) errors.Add(new("numeralSystem", "unsupported_value"));
        var tz = Clean(input.TimeZone);
        if (tz is not null && !IsKnownTimeZone(tz)) errors.Add(new("timeZone", "unknown_time_zone"));
        if (errors.Count > 0) return false;
        data = new SettingsData(lang, date, num, tz);
        return true;
    }
}
