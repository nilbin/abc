using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.SystemOps;

// The framework's own admin capabilities as FRAMEWORK PACKAGES (docs/22, the package tier):
// registered through the exact PluginBuilder surface a vendor plugin uses — every framework
// capability exercises the seams the plugin system sells, daily. Each package claims the wire
// prefixes it has always owned (D4: those names are live and permanent), ships its own forms
// and grids (the host no longer hand-wires framework admin UI), and is ALWAYS active — never
// in the activation table, never entitlement-gated. Deactivating admin surfaces only ever
// removes capability (the pipeline 404s before authorization), so the tier is fail-closed
// by the same construction as plugins.

/// <summary>Tenant custom fields (docs/15): registry operations + the fields grid.</summary>
[TamPackage("tam.extensions", "extensions", "web.extensions")]
public sealed class TamExtensionsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(DefineExtensionField))
            .AddOperationType(typeof(RetireExtensionField))
            .AddViewType(typeof(ExtensionFieldList))
            .Form<DefineExtensionField.Input>("web.extensions.define", "extensions.define-field", form =>
            {
                form.Field(x => x.Entity);
                form.Field(x => x.Key);
                form.Field(x => x.Type);
                form.Field(x => x.Labels).Renderer("culture-text");
                form.Field(x => x.Required);
                form.Field(x => x.MaxLength);
            })
            .Grid<ExtensionFieldList.Result>("web.extensions.fields", "extensions.fields", grid =>
            {
                grid.Column(x => x.Entity);
                grid.Column(x => x.Key);
                grid.Column(x => x.Type);
                grid.Column(x => x.Required);
                grid.Column(x => x.State);
                grid.ToolbarAction("extensions.define-field");
            });
    }
}

/// <summary>Tenant-managed roles (D1): definition + list, validated against the catalogue.</summary>
[TamPackage("tam.roles", "roles", "web.roles")]
public sealed class TamRolesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(DefineRole))
            .AddViewType(typeof(RoleList))
            .Form<DefineRole.Input>("web.roles.define", "roles.define", form =>
            {
                form.Field(x => x.Name);
                form.Field(x => x.Levels).Renderer("level-map");
                form.Field(x => x.Permissions).Renderer("string-list");
            })
            .Grid<RoleList.Result>("web.roles", "roles.list", grid =>
            {
                grid.Column(x => x.Name);
                grid.Column(x => x.Levels);
                grid.Column(x => x.Permissions);
                grid.ToolbarAction("roles.define");
            });
    }
}

/// <summary>The audit READ side (D3). Capture stays in the pipeline transaction — core,
/// unconditionally; this package is only the history surface.</summary>
[TamPackage("tam.audit", "audit", "web.audit")]
public sealed class TamAuditPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddViewType(typeof(AuditLog))
            .Grid<AuditLog.Result>("web.audit.list", "audit.entries", grid =>
            {
                grid.Column(x => x.Timestamp);
                grid.Column(x => x.OperationId);
                grid.Column(x => x.ActorName);
                grid.Column(x => x.Entity);
                grid.Column(x => x.Field);
                grid.Column(x => x.OldValue);
                grid.Column(x => x.NewValue);
            });
    }
}

/// <summary>Plugin activation admin (docs/22). Wears the package label for uniformity, but its
/// always-on status is non-negotiable — who activates the activator.</summary>
[TamPackage("tam.plugins", "plugins", "web.plugins")]
public sealed class TamPluginsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(ActivatePlugin))
            .AddOperationType(typeof(DeactivatePlugin))
            .AddViewType(typeof(PluginList))
            .Grid<PluginList.Result>("web.plugins", "plugins.list", grid =>
            {
                grid.Column(x => x.PluginId);
                grid.Column(x => x.Active);
                grid.RowAction("plugins.activate");
                grid.RowAction("plugins.deactivate");
            });
    }
}

/// <summary>Tenant packages (docs/22 P3): declarative field/role bundles, install/uninstall.</summary>
[TamPackage("tam.tenantpackages", "packages", "web.packages")]
public sealed class TamTenantPackagesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(InstallPackage))
            .AddOperationType(typeof(UninstallPackage))
            .AddViewType(typeof(PackageList))
            .Form<InstallPackage.Input>("web.packages.install", "packages.install", form =>
            {
                form.Field(x => x.Document).Renderer("multiline");
                form.Field(x => x.DryRun);
            })
            .Grid<PackageList.Result>("web.packages", "packages.list", grid =>
            {
                grid.Column(x => x.Package);
                grid.Column(x => x.Version);
                grid.Column(x => x.InstalledAt);
                grid.ToolbarAction("packages.install");
                grid.RowAction("packages.uninstall");
            });
    }
}

/// <summary>Tenant automation rules (docs/22 P5): declarative Px conditions as data.</summary>
[TamPackage("tam.rules", "rules", "web.rules")]
public sealed class TamRulesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        // The evaluator IS a gate: pure-over-input, pre-transaction, target set = rule rows.
        // The executor has no rules special case — the P5 feature dogfoods the gate seam.
        plugin.GateAll<RulesGate>(pure: true);
        plugin.Model
            .AddOperationType(typeof(DefineAutomationRule))
            .AddOperationType(typeof(RetireRule))
            .AddViewType(typeof(RuleList))
            .Form<DefineAutomationRule.Input>("web.rules.define", "rules.define", form =>
            {
                form.Field(x => x.Name);
                form.Field(x => x.OnOperation);
                form.Field(x => x.Condition).Renderer("multiline");
                form.Field(x => x.Messages).Renderer("culture-text");
                form.Field(x => x.TargetField);
            })
            .Grid<RuleList.Result>("web.rules", "rules.list", grid =>
            {
                grid.Column(x => x.Name);
                grid.Column(x => x.OnOperation);
                grid.Column(x => x.Retired);
                grid.ToolbarAction("rules.define");
                grid.RowAction("rules.retire");
            });
    }
}

/// <summary>Tenant hierarchy admin (docs/26). The tree itself (paths, cascade, act-as) is core;
/// these are the structural operations, which enforce subtree/cycle invariants — a
/// security-sensitive package by review policy.</summary>
[TamPackage("tam.tenancy", "tenants", "web.tenants")]
public sealed class TamTenancyPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(CreateTenant))
            .AddOperationType(typeof(MoveTenant))
            .AddOperationType(typeof(RenameTenant))
            .AddViewType(typeof(TenantList))
            .Form<CreateTenant.Input>("web.tenants.create", "tenants.create", form =>
            {
                form.Field(x => x.Id);
                form.Field(x => x.DisplayName);
            })
            .Form<MoveTenant.Input>("web.tenants.move", "tenants.move", form =>
            {
                form.Field(x => x.TenantId);
                form.Field(x => x.NewParentId);
            })
            .Form<RenameTenant.Input>("web.tenants.rename", "tenants.rename", form =>
            {
                form.Field(x => x.TenantId);
                form.Field(x => x.DisplayName);
            })
            .Grid<TenantList.Result>("web.tenants", "tenants.list", grid =>
            {
                grid.Column(x => x.Id);
                grid.Column(x => x.DisplayName);
                grid.Column(x => x.Path);
                grid.ToolbarAction("tenants.create");
                grid.ToolbarAction("tenants.move");
                grid.ToolbarAction("tenants.rename");
            });
    }
}

/// <summary>User & invite admin (docs/26). Auth itself is unaffected by this surface — actor
/// resolution reads the identity tables directly; the invite ACCEPT page lives with the auth
/// server (Tam.Auth.OpenIddict).</summary>
[TamPackage("tam.users", "users", "web.users")]
public sealed class TamUsersPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(DefineUser))
            .AddOperationType(typeof(InviteUser))
            .AddOperationType(typeof(DeactivateUser))
            .AddViewType(typeof(UserList))
            .Form<InviteUser.Input>("web.users.invite", "users.invite", form =>
            {
                form.Field(x => x.Email);
                form.Field(x => x.DisplayName);
                form.Field(x => x.Roles).Renderer("string-list");
            })
            .Grid<UserList.Result>("web.users", "users.list", grid =>
            {
                grid.Column(x => x.UserName);
                grid.Column(x => x.DisplayName);
                grid.Column(x => x.Roles);
                grid.Column(x => x.Active);
                grid.ToolbarAction("users.invite");
                grid.RowAction("users.deactivate");
            });
    }
}

/// <summary>Subscription admin (docs/24). The ENFORCEMENT (entitlement gate, seat lease) is
/// core — a billing check can't live behind the activation it gates; this is only the surface
/// the billing provider drives.</summary>
[TamPackage("tam.subscriptions", "subscriptions")]
public sealed class TamSubscriptionsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(SetPlan))
            .AddViewType(typeof(CurrentSubscription));
    }
}

/// <summary>Integration settings + secrets (docs/25). The vault SERVICE (encryption) is core;
/// this is the masked admin surface.</summary>
[TamPackage("tam.vault", "settings", "secrets")]
public sealed class TamVaultPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(SetSetting))
            .AddOperationType(typeof(SetSecret))
            .AddViewType(typeof(SettingList))
            .AddViewType(typeof(SecretList));
    }
}

/// <summary>Integration operations surface (docs/25): schedules, manual runs, run history,
/// dead-letter + requeue. The drivers (inbox, retry queue, scheduler, outbox) are core loops.</summary>
[TamPackage("tam.integrations", "integrations", "web.integrations")]
public sealed class TamIntegrationsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(ScheduleIntegration))
            .AddOperationType(typeof(RunIntegration))
            .AddOperationType(typeof(RequeueDeadLetter))
            .AddViewType(typeof(IntegrationRunList))
            .AddViewType(typeof(DeadLetterList));
    }
}
