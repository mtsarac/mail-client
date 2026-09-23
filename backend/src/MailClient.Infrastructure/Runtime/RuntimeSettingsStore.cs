using System.Text.Json;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Runtime;

public sealed class RuntimeSettingsStore(
    AppDbContext db,
    ILogger<RuntimeSettingsStore>? logger = null,
    MailClientMetrics? metrics = null) : IRuntimeSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.Set<RuntimeConfiguration>().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (row is null)
        {
            await SeedDefaultsAsync(cancellationToken);
            row = await db.Set<RuntimeConfiguration>().SingleAsync(item => item.Id == 1, cancellationToken);
        }

        return ToSnapshot(row);
    }

    public async Task<RuntimeSettingsSnapshot> ReplaceAsync(int expectedVersion, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        var row = await db.Set<RuntimeConfiguration>().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (row is null)
        {
            await GetAsync(cancellationToken);
            row = await db.Set<RuntimeConfiguration>().SingleAsync(item => item.Id == 1, cancellationToken);
        }

        if (row.Version != expectedVersion)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsConflict);
        }

        var now = DateTime.UtcNow;
        row.SettingsJson = JsonSerializer.Serialize(settings, SerializerOptions);
        row.Version++;
        row.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsConflict);
        }

        return new RuntimeSettingsSnapshot(settings, row.Version, now);
    }

    private async Task SeedDefaultsAsync(CancellationToken cancellationToken)
    {
        var seed = new RuntimeConfiguration
        {
            Id = 1,
            SettingsJson = JsonSerializer.Serialize(new RuntimeSettings(), SerializerOptions),
            UpdatedAt = DateTime.UtcNow
        };
        if (!db.Database.IsNpgsql())
        {
            db.Add(seed);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Concurrent first readers (e.g. background services at startup) race to seed the singleton row;
        // ON CONFLICT lets the losers skip instead of failing with a logged unique-key violation.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RuntimeConfigurations" ("Id", "SchemaVersion", "SettingsJson", "UpdatedAt", "Version")
            VALUES ({seed.Id}, {seed.SchemaVersion}, CAST({seed.SettingsJson} AS jsonb), {seed.UpdatedAt}, {seed.Version})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
    }

    private RuntimeSettingsSnapshot ToSnapshot(RuntimeConfiguration row)
    {
        RuntimeSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<RuntimeSettings>(row.SettingsJson, SerializerOptions)
                ?? throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsInvalid);
            settings.Validate();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            metrics?.RecordRuntimeSettingsLoadFailure();
            logger?.LogError(
                "Persisted runtime settings version {Version} are unreadable or invalid ({ErrorType}); operations depending on them will fail until corrected.",
                row.Version, ex.GetType().Name);
            throw;
        }

        return new RuntimeSettingsSnapshot(settings, row.Version, row.UpdatedAt);
    }
}
