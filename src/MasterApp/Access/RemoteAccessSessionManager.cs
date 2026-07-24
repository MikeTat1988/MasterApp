using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace MasterApp.Access;

public sealed class RemoteAccessSessionManager
{
    public const string SessionCookieName = "masterapp_remote_session";

    private readonly ConcurrentDictionary<string, TicketEntry> _tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _ticketTtl;
    private readonly TimeProvider _timeProvider;

    public RemoteAccessSessionManager(int ticketTtlSeconds, TimeProvider? timeProvider = null)
    {
        _ticketTtl = TimeSpan.FromSeconds(Math.Max(15, ticketTtlSeconds));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RemoteAccessTicket CreateTicket(string redirectPath = "/store.html")
    {
        CleanupExpiredTickets();

        var ticket = GenerateToken();
        var expiresAtUtc = _timeProvider.GetUtcNow().Add(_ticketTtl);
        _tickets[ticket] = new TicketEntry(redirectPath, expiresAtUtc);

        return new RemoteAccessTicket(ticket, redirectPath, expiresAtUtc, (int)_ticketTtl.TotalSeconds);
    }

    public bool TryConsumeTicket(string ticket, IPAddress? remoteAddress, out RemoteAccessSessionResult? result)
    {
        CleanupExpiredTickets();
        result = null;

        var normalizedRemoteAddress = NormalizeRemoteAddress(remoteAddress);
        if (string.IsNullOrWhiteSpace(ticket) || normalizedRemoteAddress is null)
        {
            return false;
        }

        if (!_tickets.TryRemove(ticket, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc < _timeProvider.GetUtcNow())
        {
            return false;
        }

        var sessionId = GenerateToken();
        _sessions[sessionId] = new SessionEntry(normalizedRemoteAddress, _timeProvider.GetUtcNow());
        result = new RemoteAccessSessionResult(sessionId, entry.RedirectPath);
        return true;
    }

    public bool HasValidSession(string? sessionId, IPAddress? remoteAddress)
    {
        var normalizedRemoteAddress = NormalizeRemoteAddress(remoteAddress);
        if (string.IsNullOrWhiteSpace(sessionId) || normalizedRemoteAddress is null)
        {
            return false;
        }

        return _sessions.TryGetValue(sessionId, out var entry) &&
               string.Equals(entry.RemoteAddress, normalizedRemoteAddress, StringComparison.Ordinal);
    }

    public RemoteAccessSnapshot Snapshot()
    {
        CleanupExpiredTickets();
        return new RemoteAccessSnapshot(_sessions.Count, _tickets.Count, (int)_ticketTtl.TotalSeconds);
    }

    public static string? NormalizeRemoteAddress(IPAddress? address)
    {
        return Networking.WifiNetworkInspector.NormalizeAddress(address)?.ToString();
    }

    private void CleanupExpiredTickets()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _tickets)
        {
            if (entry.Value.ExpiresAtUtc < now)
            {
                _tickets.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }

    private sealed record TicketEntry(string RedirectPath, DateTimeOffset ExpiresAtUtc);
    private sealed record SessionEntry(string RemoteAddress, DateTimeOffset CreatedAtUtc);
}

public sealed record RemoteAccessTicket(string Ticket, string RedirectPath, DateTimeOffset ExpiresAtUtc, int TtlSeconds);

public sealed record RemoteAccessSessionResult(string SessionId, string RedirectPath);

public sealed record RemoteAccessSnapshot(int ActiveSessions, int PendingTickets, int TicketTtlSeconds);
