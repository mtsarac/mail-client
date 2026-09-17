using System.Text.Json;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Runtime;

public sealed class RuntimeSettingsStore(AppDbContext db) : IRuntimeSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.Set<RuntimeConfiguration>().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (row is null)
        {
            row = new RuntimeConfiguration
            {
                Id = 1,
                SettingsJson = JsonSerializer.Serialize(new RuntimeSettings(), SerializerOptions),
                UpdatedAt = DateTime.UtcNow
            };
            db.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached;
                row = await db.Set<RuntimeConfiguration>().SingleAsync(item => item.Id == 1, cancellationToken);
            }
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

    private static RuntimeSettingsSnapshot ToSnapshot(RuntimeConfiguration row)
    {
        var settings = JsonSerializer.Deserialize<RuntimeSettings>(row.SettingsJson, SerializerOptions)
            ?? throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsInvalid);
        settings.Validate();
        return new RuntimeSettingsSnapshot(settings, row.Version, row.UpdatedAt);
    }
}
