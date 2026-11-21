using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Expiration;

public enum ExpirationBlockReason
{
    Expired,
    TimeUnavailable,
}

public sealed class ExpirationBlockedException : Exception
{
    public ExpirationBlockedException()
        : this(ExpirationBlockReason.Expired, MessageFor(ExpirationBlockReason.Expired))
    {
    }

    public ExpirationBlockedException(string message)
        : this(ExpirationBlockReason.Expired, message)
    {
    }

    public ExpirationBlockedException(string message, Exception innerException)
        : this(ExpirationBlockReason.Expired, message, innerException)
    {
    }

    public ExpirationBlockedException(ExpirationBlockReason reason)
        : this(reason, MessageFor(reason))
    {
    }

    public ExpirationBlockedException(ExpirationBlockReason reason, Exception innerException)
        : this(reason, MessageFor(reason), innerException)
    {
    }

    public ExpirationBlockedException(ExpirationBlockReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public ExpirationBlockedException(ExpirationBlockReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public ExpirationBlockReason Reason { get; }

    public string DisplayMessage => MessageFor(Reason);

    private static string MessageFor(ExpirationBlockReason reason) => reason == ExpirationBlockReason.TimeUnavailable
        ? "无网络环境，需联网后使用 / No network available. Connect to the network before using this build."
        : "当前版本与当前系统组件不兼容。/ This build is incompatible with the current system components.";
}

public interface INtpClock
{
    Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Startup gate for time-limited builds. Every launch resolves UTC from a
/// public NTP server: there is no cache and no offline fallback. An unlimited
/// build never touches the network or this gate's failure paths.
/// </summary>
public sealed class ExpirationGate
{
    private readonly INtpClock _clock;

    public ExpirationGate(INtpClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public static ExpirationGate Default { get; } = new ExpirationGate(new SntpClock());

    public Task CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(BuildExpiration.ConfiguredExpiry, BuildExpiration.ConfiguredBuiltAt, cancellationToken);

    public async Task CheckAsync(
        string? expiry,
        string? builtAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expiry)) return;

        DateTimeOffset networkNow;
        try
        {
            networkNow = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExpirationBlockedException(ExpirationBlockReason.TimeUnavailable, ex);
        }

        if (BuildExpiration.IsExpired(expiry, builtAt, networkNow))
            throw new ExpirationBlockedException(ExpirationBlockReason.Expired);
    }
}
