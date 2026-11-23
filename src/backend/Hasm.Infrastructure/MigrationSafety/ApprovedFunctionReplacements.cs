namespace Hasm.Infrastructure.SystemDatabase;

// Owner decision 2026-11-21 (Q5 of docs/PHASE-3.8D-RESUME-REVIEW.md). Changing this file is a SAFEGUARD CHANGE: see docs/DEVELOPMENT.md, "Migration guard".
public static partial class MigrationGuard
{
    /// <summary>
    /// SAFEGUARD-CHANGE ALLOWLIST. A migration may replace (CREATE OR REPLACE) a function that an earlier migration created ONLY when
    /// it is listed here. Adding an entry is a safeguard change: it needs an explicit owner approval recorded in the owner-decision
    /// register, an independent security review of the full new function body, and a before/after catalog test (procedure:
    /// docs/DEVELOPMENT.md, "Migration guard"). The six entries below (each bound to the checksum of its migration) are the replacements that already existed in applied company
    /// migrations when the rule was introduced; applied migrations are immutable, so they are listed, never rewritten.
    /// </summary>
    public static IReadOnlyList<GuardFunctionReplacement> ApprovedFunctionReplacements { get; } =
    [
        // Company 0009 (general-ledger completion) replaces two functions of company 0008.
        new("0009_company_gl_completion.sql", "28e0cd479177fc2c8d7fef4792d9182d58fc12f5dd01b40190f941e746752f5d", "f_660e4c73",
            "Pre-existing in an applied migration. The classic approval-policy evaluation of 0008 is restricted to policies without risk conditions and without stages, which 0009 introduces and evaluates separately."),
        new("0009_company_gl_completion.sql", "28e0cd479177fc2c8d7fef4792d9182d58fc12f5dd01b40190f941e746752f5d", "f_ba4c6a0d",
            "Pre-existing in an applied migration. Posting-grade validation of 0008: a reversal or a verified year-end opening entry may keep an allocation to a cost centre that was deactivated or became a group after the original posting; new entries still allocate to active leaves only."),
        // Company 0027 (customer groups) replaces two immutability trigger functions of company 0026 (pricing).
        new("0027_company_customer_groups.sql", "ffee736191e102f877198afd24eacd5bcd042068cc92110044dc08ab86b79882", "f_4fab1a41",
            "Pre-existing in an applied migration. The price-list immutability check of 0026 additionally freezes the customer-group column added by 0027."),
        new("0027_company_customer_groups.sql", "ffee736191e102f877198afd24eacd5bcd042068cc92110044dc08ab86b79882", "f_86f87c19",
            "Pre-existing in an applied migration. The immutability check of 0026 additionally freezes the customer-group column added by 0027."),
        // Company 0029 (commercial approval) replaces two trigger functions of company 0028 (commercial documents).
        new("0029_company_commercial_approval.sql", "c389037260353fc31db7f3986ee87cea175b3663e7017bff7e9db8665742b83a", "f_bc72c55d",
            "Pre-existing in an applied migration. The document state guard of 0028 is extended with the approved and rejected states, the official number and the sealed quote of the approval workflow."),
        new("0029_company_commercial_approval.sql", "c389037260353fc31db7f3986ee87cea175b3663e7017bff7e9db8665742b83a", "f_7d3cbe20",
            "Pre-existing in an applied migration. The document event guard of 0028 is extended with the event kinds and transition checks of the approval workflow."),
    ];
}
