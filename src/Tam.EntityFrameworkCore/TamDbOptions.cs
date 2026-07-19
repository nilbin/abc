using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

public static class TamDbOptions
{
    /// <summary>
    /// The framework's DbContext conventions a host must not forget: the tenant-stamp interceptor
    /// (write-side mirror of the global read filter — operation code never assigns TenantId by hand),
    /// and the derivation write-guard (docs/40: derivations are structurally read-only). One call in
    /// AddDbContext instead of per-interceptor wiring.
    /// </summary>
    public static DbContextOptionsBuilder UseTamConventions(this DbContextOptionsBuilder options)
        => options.AddInterceptors(new TenantStampInterceptor(), new DerivationWriteGuardInterceptor());
}
