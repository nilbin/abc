using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.SystemOps;

public static class SystemModule
{
    /// <summary>
    /// Registers the framework's own capabilities as FRAMEWORK PACKAGES (docs/22 package tier)
    /// — each one an ordinary <see cref="ITamPlugin"/>-shaped module claiming its permanent wire
    /// prefixes, always active, shipping its own forms and grids. This aggregate is the one-line
    /// host opt-in; a host may also compose the packages individually.
    /// </summary>
    public static TamModelBuilder AddTamSystem(this TamModelBuilder builder) => builder
        .AddPackage<TamExtensionsPackage>()
        .AddPackage<TamRolesPackage>()
        .AddPackage<TamAuditPackage>()
        .AddPackage<TamPluginsPackage>()
        .AddPackage<TamTenantPackagesPackage>()
        .AddPackage<TamRulesPackage>()
        .AddPackage<TamTenancyPackage>()
        .AddPackage<TamUsersPackage>()
        .AddPackage<TamSubscriptionsPackage>()
        .AddPackage<TamVaultPackage>()
        .AddPackage<TamIntegrationsPackage>();
}

// ---------------------------------------------------------------- tenant custom fields
