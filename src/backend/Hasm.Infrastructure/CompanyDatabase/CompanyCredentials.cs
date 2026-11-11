using System.Text.Json;
using Hasm.Infrastructure.Audit;

namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>A company runtime login. The password is never part of <c>ToString</c>, logs or error messages.</summary>
public sealed record CompanyCredential(string Username, string Password)
{
    public override string ToString() => "CompanyCredential(redacted)";
}

public sealed class CompanyCredentialException(string code) : Exception("The company runtime credential store is unusable.")
{
    public string Code { get; } = code;
}

/// <summary>Source of company runtime credentials. The deployment decides where they live; they are never stored in application tables.</summary>
public interface ICompanyCredentialProvider
{
    /// <summary>The company's runtime credential, or null when none has been provisioned. Throws <see cref="CompanyCredentialException"/> when the store is unusable or unsafe.</summary>
    Task<CompanyCredential?> GetAsync(Guid companyId, CancellationToken ct = default);
}

/// <summary>
/// One owner-only file per company (<c>&lt;company id&gt;.hcred</c>, JSON) in a directory outside the repository and outside every database.
/// The directory may be a mounted secret store. Files are staged privately and moved into place atomically (which is also the rotation),
/// with permissions restricted to the service account (the same rules as the audit keys). The file name carries the company id only,
/// never the physical database name.
/// </summary>
public sealed class FileCompanyCredentialStore(string directory, bool enforcePermissions = true) : ICompanyCredentialProvider
{
    public const string Extension = ".hcred";
    private sealed record Document(int v, string username, string password);

    public string PathFor(Guid companyId) => Path.Combine(directory, companyId.ToString("N") + Extension);

    public bool Exists(Guid companyId) => File.Exists(PathFor(companyId));

    public async Task<CompanyCredential?> GetAsync(Guid companyId, CancellationToken ct = default)
    {
        var path = PathFor(companyId);
        try
        {
            if (!File.Exists(path)) return null;
            if (enforcePermissions) AuditFilePermissions.EnsureOwnerOnly(directory, path);
            var doc = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(path, ct));
            if (doc is not { v: 1, username.Length: > 0, password.Length: > 0 }) throw new CompanyCredentialException("credential_invalid");
            return new CompanyCredential(doc.username, doc.password);
        }
        catch (AuditUnavailableException) { throw new CompanyCredentialException("credential_permissions"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new CompanyCredentialException("credential_unreadable"); }
        catch (JsonException) { throw new CompanyCredentialException("credential_invalid"); }
    }

    /// <summary>Writes the credential to a private temporary file; <see cref="Commit"/> moves it into place.</summary>
    public string Stage(Guid companyId, CompanyCredential credential)
    {
        try
        {
            AuditFilePermissions.CreateOwnerOnlyDirectory(directory);
            var temp = PathFor(companyId) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            AuditFilePermissions.CreateOwnerOnlyFile(temp, JsonSerializer.SerializeToUtf8Bytes(new Document(1, credential.Username, credential.Password)));
            return temp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new CompanyCredentialException("credential_unwritable"); }
    }

    /// <summary>Atomically installs a staged credential (replacing the previous one, which is the rotation).</summary>
    public void Commit(Guid companyId, string stagedPath)
    {
        try { File.Move(stagedPath, PathFor(companyId), overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new CompanyCredentialException("credential_unwritable"); }
    }

    public static void Discard(string stagedPath)
    {
        try { File.Delete(stagedPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>In-memory provider for tests.</summary>
public sealed class InMemoryCompanyCredentialProvider : ICompanyCredentialProvider
{
    private readonly Dictionary<Guid, CompanyCredential> data = [];
    public void Set(Guid companyId, CompanyCredential credential) { lock (data) data[companyId] = credential; }
    public Task<CompanyCredential?> GetAsync(Guid companyId, CancellationToken ct = default)
    {
        lock (data) return Task.FromResult(data.TryGetValue(companyId, out var c) ? c : null);
    }
}
