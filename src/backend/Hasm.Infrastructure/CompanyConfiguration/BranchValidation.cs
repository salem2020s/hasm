using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.CompanyConfiguration;

public static partial class ConfigurationValidation
{
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_-]{0,19}$")] private static partial Regex BranchCodeShape();

    /// <summary>Trim, upper case (invariant) and shape-check a branch code; null when invalid.</summary>
    public static string? NormalizeBranchCode(string? raw)
    {
        var v = Clean(raw)?.ToUpperInvariant();
        return v is not null && BranchCodeShape().IsMatch(v) ? v : null;
    }

    /// <summary>
    /// Validates a branch. At least one non-empty name (Arabic or English) is required; both may be given. Address and contact fields are
    /// optional and follow the company-profile rules. <paramref name="existingCode"/> is set on update: the code cannot change.
    /// </summary>
    public static bool TryNormalizeBranch(BranchInput? input, string? existingCode, out BranchData data, out List<FieldError> errors)
    {
        errors = [];
        data = default!;
        if (input is null) { errors.Add(new("body", "required")); return false; }
        string? code;
        if (existingCode is null)
        {
            code = NormalizeBranchCode(input.Code);
            if (code is null) errors.Add(new("code", Clean(input.Code) is null ? "required" : "invalid_format"));
        }
        else
        {
            code = existingCode;
            if (Clean(input.Code) is { } supplied && !string.Equals(supplied, existingCode, StringComparison.OrdinalIgnoreCase))
                errors.Add(new("code", "immutable"));
        }
        var nameAr = Text(input.NameAr, "nameAr", 200, errors);
        var nameEn = Text(input.NameEn, "nameEn", 200, errors);
        if (nameAr is null && nameEn is null && errors.All(e => e.Field != "nameAr" && e.Field != "nameEn"))
        { errors.Add(new("nameAr", "name_required")); errors.Add(new("nameEn", "name_required")); }
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
        data = new BranchData(code!, nameAr, nameEn, a1, a2, city, region, postal, phone, email, website);
        return true;
    }
}
