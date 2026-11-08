namespace Hasm.Infrastructure.Configuration;

/// <summary>Removes known secret values from text before it is logged or shown.</summary>
public static class SecretRedactor
{
    public const string Mask = "***";

    public static string Scrub(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }
        return text;
    }
}
