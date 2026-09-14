using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MailClient.Infrastructure.Services;

public enum SyncFailureDisposition { Retry, Skip }

public static class SyncFailurePolicy
{
    public static SyncFailureDisposition Classify(Exception exception) => exception switch
    {
        FormatException => SyncFailureDisposition.Skip,
        MessageNotFoundException => SyncFailureDisposition.Skip,
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
        _ => SyncFailureDisposition.Retry
    };
}
