using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MailClient.Infrastructure.Services;

// Decides whether a per-message sync failure is permanent (skip the UID and
// continue) or transient (abort the run and retry the same UID next poll).
// The bias is deliberate: an ambiguous error must retry, never skip.
// A wrong retry only delays a message; a wrong skip silently loses it.
internal enum SyncFailureDisposition
{
    Retry,
    Skip
}

internal static class SyncFailurePolicy
{
    // Cancellation is handled by the caller before classification and must keep propagating.
    public static SyncFailureDisposition Classify(Exception exception) => exception switch
    {
        // Message content problems: retrying will never succeed.
        FormatException => SyncFailureDisposition.Skip,
        MessageNotFoundException => SyncFailureDisposition.Skip,

        // Persistence and network problems: the message itself may be fine.
        DbUpdateException => SyncFailureDisposition.Retry,
        NpgsqlException => SyncFailureDisposition.Retry,
        IOException => SyncFailureDisposition.Retry,
        SocketException => SyncFailureDisposition.Retry,
        TimeoutException => SyncFailureDisposition.Retry,
        HttpRequestException => SyncFailureDisposition.Retry,
        ImapProtocolException => SyncFailureDisposition.Retry,
        ImapCommandException => SyncFailureDisposition.Retry,
        ServiceNotConnectedException => SyncFailureDisposition.Retry,
        ServiceNotAuthenticatedException => SyncFailureDisposition.Retry,

        // Unknown failure: retry rather than risk losing mail.
        _ => SyncFailureDisposition.Retry
    };
}
