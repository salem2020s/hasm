namespace Hasm.Infrastructure.Identity;

/// <summary>Why a user is subject to the central, always-on MFA requirement (Phase 4.1 section 3, D-080 (1); D-164, D-165).</summary>
[Flags]
public enum MfaMandatoryReason
{
    None = 0,
    IdentityAdministrator = 1,
    CompanyAdministratorPreset = 2,
    PrivilegedRole = 4,
}

/// <summary>The central requirement of one user, computed from the user's union of privileges across ALL companies (not from the selected company).</summary>
public sealed record MfaRequirement(MfaMandatoryReason Reasons)
{
    public bool Mandatory => Reasons != MfaMandatoryReason.None;
}
