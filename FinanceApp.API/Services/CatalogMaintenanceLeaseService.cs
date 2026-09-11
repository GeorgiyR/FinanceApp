using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Services;

public static class CatalogMaintenanceLeaseNames
{
    public const string AllCatalogDataRefresh = "catalog-data-refresh";
}

public interface ICatalogMaintenanceLeaseService
{
    Task<bool> TryAcquireAsync(string leaseName, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> TryRenewAsync(string leaseName, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task ReleaseAsync(string leaseName, string leaseOwner, CancellationToken cancellationToken);
}

public sealed class CatalogMaintenanceLeaseService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : ICatalogMaintenanceLeaseService
{
    public async Task<bool> TryAcquireAsync(string leaseName, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseExpiresAtUtc = now.Add(leaseDuration);

        if (!db.Database.IsRelational())
        {
            var lease = await GetOrCreateLeaseAsync(db, leaseName, now, cancellationToken);
            if (lease is null)
            {
                return false;
            }

            var available = string.IsNullOrWhiteSpace(lease.LeaseOwner)
                            || string.Equals(lease.LeaseOwner, leaseOwner, StringComparison.Ordinal)
                            || !lease.LeaseExpiresAtUtc.HasValue
                            || lease.LeaseExpiresAtUtc.Value < now;
            if (!available)
            {
                return false;
            }

            lease.LeaseOwner = leaseOwner;
            lease.LeaseExpiresAtUtc = leaseExpiresAtUtc;
            lease.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogMaintenanceLeases
            SET LeaseOwner = {leaseOwner},
                LeaseExpiresAtUtc = {leaseExpiresAtUtc},
                UpdatedAtUtc = {now}
            WHERE LeaseName = {leaseName}
              AND (LeaseOwner IS NULL OR LeaseOwner = {leaseOwner} OR LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc < {now})
            """, cancellationToken);
        if (updated == 1)
        {
            return true;
        }

        if (updated == 0)
        {
            var existing = await db.CatalogMaintenanceLeases
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.LeaseName == leaseName, cancellationToken);

            if (existing is not null)
            {
                return false;
            }

            var created = new CatalogMaintenanceLease
            {
                LeaseName = leaseName,
                LeaseOwner = leaseOwner,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            db.CatalogMaintenanceLeases.Add(created);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // Another instance may have created the lease concurrently.
                return false;
            }
        }

        return false;
    }

    public async Task<bool> TryRenewAsync(string leaseName, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseExpiresAtUtc = now.Add(leaseDuration);

        if (!db.Database.IsRelational())
        {
            var lease = await db.CatalogMaintenanceLeases.FirstOrDefaultAsync(x => x.LeaseName == leaseName, cancellationToken);
            if (lease is null || !string.Equals(lease.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            {
                return false;
            }

            lease.LeaseExpiresAtUtc = leaseExpiresAtUtc;
            lease.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogMaintenanceLeases
            SET LeaseExpiresAtUtc = {leaseExpiresAtUtc},
                UpdatedAtUtc = {now}
            WHERE LeaseName = {leaseName}
              AND LeaseOwner = {leaseOwner}
            """, cancellationToken);
        return updated == 1;
    }

    public async Task ReleaseAsync(string leaseName, string leaseOwner, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (!db.Database.IsRelational())
        {
            var lease = await db.CatalogMaintenanceLeases.FirstOrDefaultAsync(x => x.LeaseName == leaseName, cancellationToken);
            if (lease is null || !string.Equals(lease.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            {
                return;
            }

            lease.LeaseOwner = null;
            lease.LeaseExpiresAtUtc = null;
            lease.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE CatalogMaintenanceLeases
            SET LeaseOwner = NULL,
                LeaseExpiresAtUtc = NULL,
                UpdatedAtUtc = {now}
            WHERE LeaseName = {leaseName}
              AND LeaseOwner = {leaseOwner}
            """, cancellationToken);
    }

    private static async Task<CatalogMaintenanceLease?> GetOrCreateLeaseAsync(
        AppDbContext db,
        string leaseName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var lease = await db.CatalogMaintenanceLeases.FirstOrDefaultAsync(x => x.LeaseName == leaseName, cancellationToken);
        if (lease is not null)
        {
            return lease;
        }

        var created = new CatalogMaintenanceLease
        {
            LeaseName = leaseName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CatalogMaintenanceLeases.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            return await db.CatalogMaintenanceLeases.FirstOrDefaultAsync(x => x.LeaseName == leaseName, cancellationToken);
        }
    }
}
