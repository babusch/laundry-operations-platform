using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Laundry.Edge.Security;

// Bounded development identity proof only. Restart invalidates tickets; no production session persistence is claimed.
public sealed class OperatorTicketStore(TimeProvider clock) : ITicketStore
{
    private readonly ConcurrentDictionary<string, AuthenticationTicket> tickets = new();
    private readonly object gate = new();
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        lock (gate)
        {
            foreach (var pair in tickets)
                if (pair.Value.Properties.ExpiresUtc is not { } expires || expires <= clock.GetUtcNow()) tickets.TryRemove(pair.Key, out _);
            if (tickets.Count >= 256) throw new InvalidOperationException("Development operator identity capacity reached.");
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            tickets[key] = ticket;
            return Task.FromResult(key);
        }
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket) => Task.CompletedTask; // No sliding sessions.
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (tickets.TryGetValue(key, out var ticket) && ticket.Properties.ExpiresUtc is { } expires && expires > clock.GetUtcNow())
            return Task.FromResult<AuthenticationTicket?>(ticket);
        tickets.TryRemove(key, out _);
        return Task.FromResult<AuthenticationTicket?>(null);
    }
    public Task RemoveAsync(string key) { tickets.TryRemove(key, out _); return Task.CompletedTask; }
}
