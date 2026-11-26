using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>Settings for adding vendors automatically from the connected mailboxes (section "Vendors").</summary>
public sealed class VendorAutoAddOptions
{
    /// <summary>Off = vendors are only added by hand (Vendor Emails → Find vendors).</summary>
    public bool AutoAdd { get; set; } = true;
    /// <summary>A company domain is added when its mail scores at least this (0–100) as bench-sales traffic.</summary>
    public int MinScore { get; set; } = 50;
    /// <summary>Personal addresses (Gmail/Yahoo recruiters) need a higher score, and are never added when they look like a consultant.</summary>
    public int PersonalMinScore { get; set; } = 70;
    /// <summary>How often the mailboxes are scanned for new vendors.</summary>
    public int EveryHours { get; set; } = 6;
    /// <summary>How far back each scan reads.</summary>
    public int Days { get; set; } = 30;
}

/// <summary>
/// Finds the staffing vendors your team actually emails (the same scan as "Find vendors") and adds the clear ones to the vendor
/// directory, so Vendor Emails fills up without anyone typing domains in. Runs shortly after start-up and then every few hours.
/// A vendor someone switched off (or deleted domains from) is left alone: known domains, active or not, are never added again.
/// </summary>
public sealed class VendorAutoAdd(IServiceScopeFactory scopes, IOptions<VendorAutoAddOptions> options, IOptions<MicrosoftOptions> ms,
    MailSync sync, ILogger<VendorAutoAdd> log) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (!o.AutoAdd || !ms.Value.Configured) { log.LogInformation("Automatic vendor adding is off."); return; }
        var every = TimeSpan.FromHours(Math.Clamp(o.EveryHours, 1, 168));
        var wait = TimeSpan.FromMinutes(1);   // first scan shortly after start-up
        while (true)
        {
            try { await Task.Delay(wait, stop); } catch (OperationCanceledException) { return; }
            try { await RunAsync(stop); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Automatic vendor adding failed"); }
            wait = every;
        }
    }

    /// <summary>Scans every connected mailbox and adds the clear vendors. Returns the names added.</summary>
    public async Task<List<string>> RunAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct)) return [];
        try
        {
            var o = options.Value;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = await db.MailConnections.Where(c => c.Status == "Active").Select(c => c.UserId).ToListAsync(ct);
            if (users.Count == 0) return [];

            var found = await scope.ServiceProvider.GetRequiredService<VendorDiscovery>().ScanAsync(users, o.Days, ct);
            var picks = found.Vendors
                .Where(v => v.IsAddress ? v.Score >= o.PersonalMinScore && !v.LikelyConsultant : v.Score >= o.MinScore)
                .ToList();
            if (picks.Count == 0) { log.LogInformation("Vendor scan: {Read} emails in {Boxes} mailboxes, no new vendors.", found.MessagesRead, found.Mailboxes); return []; }

            var names = (await db.Vendors.Select(v => v.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = new List<string>();
            foreach (var p in picks)
            {
                var name = UniqueName(p.SuggestedName, p.Key, names);
                var v = new Vendor { Name = name, Domains = [p.Key.ToLowerInvariant()], CreatedByUserId = null };
                db.Vendors.Add(v);
                try { await db.SaveChangesAsync(ct); names.Add(name); added.Add($"{name} ({p.Key})"); }
                catch (DbUpdateException ex)
                {
                    db.Entry(v).State = EntityState.Detached;
                    log.LogInformation("Vendor {Name} ({Key}) not added: {Reason}", name, p.Key, ex.InnerException?.Message ?? ex.Message);
                }
            }
            if (added.Count == 0) return added;

            await scope.ServiceProvider.GetRequiredService<AuditService>()
                .LogAsync("VENDOR_AUTO_ADD", $"Added {added.Count} vendor(s) found in the mailboxes: {string.Join(", ", added.Take(25))}{(added.Count > 25 ? "…" : "")}");
            log.LogInformation("Added {Count} vendors automatically; re-reading mailboxes for their past emails.", added.Count);
            // New vendors apply to older mail too: re-read the last FirstSyncDays of every mailbox (tracked emails are skipped).
            await db.MailConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.SyncedThroughUtc, (DateTime?)null), ct);
            sync.Kick();
            return added;
        }
        finally { _running.Release(); }
    }

    /// <summary>The suggested name, or "Name (domain)" when another vendor already has it.</summary>
    private static string UniqueName(string suggested, string key, HashSet<string> taken)
    {
        var name = suggested.Trim() is { Length: >= 2 } s ? s : key;
        if (name.Length > 200) name = name[..200];
        if (!taken.Contains(name)) return name;
        var alt = $"{name} ({key})";
        return alt.Length > 200 ? alt[..200] : alt;
    }
}
