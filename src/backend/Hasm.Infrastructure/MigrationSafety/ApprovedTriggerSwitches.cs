namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>
/// One migration that is allowed to disable and re-enable triggers. It is bound to the exact migration identity (set, number, file name),
/// to its checksum and to the exact triggers it switches ("table/trigger" physical names, sorted), so an edited migration, the same text
/// under another number or in the other set, or a migration that switches an additional trigger is never covered.
/// </summary>
public sealed record ApprovedTriggerSwitch(MigrationSet Set, int Version, string Name, string Checksum, IReadOnlyList<string> Triggers);

// Owner decision 2026-11-22 (Q1). Changing this file is a SAFEGUARD CHANGE: see docs/DEVELOPMENT.md, "Migration guard" and docs/MIGRATION-GUARD.md.
public static partial class MigrationGuard
{
    /// <summary>
    /// SAFEGUARD-CHANGE ALLOWLIST (frozen). A migration may contain a matched "ALTER TABLE t DISABLE TRIGGER x ... ALTER TABLE t ENABLE TRIGGER x"
    /// pair ONLY when it is listed here. The first 26 entries are exactly the migrations on <c>main</c> that already used the pattern when the rule was introduced (the 27th and the 28th are the approved NEW uses of X4: the cash account presets and the short-close preset); they
    /// are all SYSTEM migrations (no company migration uses it). Each pauses the permission-immutability trigger (and, in some, the role-permission trigger) of the
    /// system database for exactly one versioned insert of permission rows into a preset role, inside the migration's own transaction, and re-enables it before the
    /// transaction ends (the runner verifies this after every script). Applied migrations are immutable, so they are listed, never rewritten. Adding an entry is a
    /// safeguard change: explicit owner approval recorded in the owner-decision register, an independent security review of the migration, and tests.
    /// </summary>
    public static IReadOnlyList<ApprovedTriggerSwitch> ApprovedTriggerSwitches { get; } =
    [
        new(MigrationSet.System, 6, "0006_company_configuration_support.sql", "debedc9bd0f637a2d59a6f140c7fa4da9494c582e44f1c80dc8641c90de37c08",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 7, "0007_branch_management_access.sql", "e3e01f167854e09d226ad56eaca51c0d6a7bbea3d55c72cdaaff94a8c29250cb",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 8, "0008_warehouse_management_access.sql", "6139b4186ab3033fe7695ddf7398c3462d1937091b32c4c17b180793c7448e00",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 9, "0009_fiscal_calendar_access.sql", "bb2c3183ed6d5441206eb73ff181d887b12f703c2e5f29d696caa3122a59aa5b",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 10, "0010_chart_of_accounts_access.sql", "bc25cf34681f21d152e3f25cc5cce8e766c1fdc5682defdaa280a1228acbf05f",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 11, "0011_general_ledger_access.sql", "f4c5e7f2547db13f599d1553f18b386f321c63a2dc0113d60ec5996e72f52e70",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 12, "0012_cost_center_scope.sql", "93116982f3ce604b488cb3ab659b3c75691b0bb1776366f5352381bc99fc5ed4",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 13, "0013_import_framework_access.sql", "a0a8d612a1c78a0fb8f93992843e61da93404012322daf79ce824934cc42a2fb",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 14, "0014_financial_reporting_access.sql", "6f1cfed10fcce6cf8f2ede0aab5b4330d5717d47d8a0fb0b285117f5d8193ed0",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 15, "0015_inventory_master_data_access.sql", "da583e7427b97d8083b90fedb3ddb7e23b1187ed420fc8b6eb28c5779d619540",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 16, "0016_stock_documents_access.sql", "5f7a487b84e5a1fd59aa92da7ab006f5b7397043f8ef2d913081b25dc3299da6",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 17, "0017_inventory_accounting_access.sql", "1cdb4dccfb963ee0e4a26c7a991c287924619ef8cfdd34dcec7c15efe4b931f2",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 18, "0018_stock_posting_access.sql", "0ff833c34cfe83126db94a99a113e29ec0f1ba3e051fb5009e4ac854f69a7020",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 19, "0019_stock_reversal_access.sql", "43bbc569c4d84fdaa4be30a247dcf826ba4b896a1472f8903732cfff382ea1c5",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 20, "0020_stock_balance_access.sql", "8fdd0f4131b25337682448e82ae58d318f6661a28f807ec447c78356b456cc1e",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 21, "0021_stocktake_access.sql", "d2aac90e19ea71e9628a274472d675fffc23e7546b25882cb3cd3c25138e727c",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 22, "0022_negative_stock_access.sql", "3546694dae2892dc241e757504a40ee5420dc5c871f3fd6cf666f4bf3e68738e",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 23, "0023_party_registry.sql", "9d33adff94cb79c0c0133a7929f62006b14bddecdea6f066a35fd533d502edcc",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 24, "0024_tax_access.sql", "f53cbaed6b4e8190fe2f12ca62ca859af87089acc601fb8a3ac4bf44782ad5eb",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 25, "0025_party_roles_access.sql", "6ecefbf81ea025934138e8eb359144054c99c1c486000c9ab649b60f220e5680",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 26, "0026_attachments_access.sql", "05bd3bb4dac449c853d3a6940f9c7b500841212695f0bf8030b39cab740188ca",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 27, "0027_pricing_access.sql", "de980d8d01e5f177edcaa980c77d896b1c7bb31abe561d351d4da36b0a919e23",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 28, "0028_commercial_documents_access.sql", "e1cb7bcfa277187ac46d5c4c3027e696c41baefdb08a082e9fcd13dfeb1abd4b",
            ["t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 29, "0029_commercial_approver.sql", "902fa5bd6ec1fa94a04a82bbf95569fd4a5386ec1bebef706add375bc31efa80",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 30, "0030_attachment_retention_access.sql", "004c557674706584afb9e753dc50d3929e2cc467cc2b5a9f23564e00b21f8f9b",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        new(MigrationSet.System, 34, "0034_company_mfa_policy.sql", "f9aedd4eee2de5ee1c4db6fa84ca48d92b75fda4b906c039d37c521c70794ead",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        // X4-PRESET-ROLE (owner Option A, 2026-11-22; plan docs/X4-PRESET-ROLES-PLAN.md): the two dedicated cash and bank account presets. A NEW migration: it creates two preset roles and their permission rows, so
        // both immutability triggers are paused. The number is provisional until the final merge round; when it changes, Version, Name and Checksum of this entry change with it, in the same reviewed change.
        new(MigrationSet.System, 36, "0036_cash_account_presets.sql", "22f90773c2601e80db36ce5282c3cb422746ae7a39bae84f1800ff6322dfb907",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
        // X4-PRESET-ROLE (owner Option A, D-254; plan docs/X4-PRESET-ROLES-PLAN.md section 3.3): the dedicated short-close preset Commercial Document Closer (D-201). A NEW migration: it creates one preset role and its two
        // permission rows, so both immutability triggers are paused. If the number changes at the merge round, Version, Name and Checksum of this entry change with it, in the same reviewed change.
        new(MigrationSet.System, 41, "0041_commercial_document_closer.sql", "f9946bb7c4cad858b21afc04a6823c5e177cb082a8b28f87acec0052b2da4977",
            ["t_359332ac/r_d3408f58", "t_3e3d4119/r_2601e368"]),
    ];
}
