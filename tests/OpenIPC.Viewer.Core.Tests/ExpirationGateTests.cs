using OpenIPC.Viewer.Core.Expiration;

namespace OpenIPC.Viewer.Core.Tests;

public sealed class ExpirationGateTests
{
    private const string Expiry = "2026-11-20";
    private const string BuiltAt = "2026-11-01T00:00:00.0000000Z";
    private static readonly DateTimeOffset BeforeExpiry = new(2026, 11, 20, 23, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset AtExpiry = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unlimited_build_never_touches_network()
    {
        var clock = new FakeClock();
        var gate = new ExpirationGate(clock);

        await gate.CheckAsync(null, BuiltAt);
        await gate.CheckAsync("   ", BuiltAt);

        Assert.Equal(0, clock.Calls);
    }

    [Fact]
    public async Task Limited_build_contacts_ntp_on_every_check()
    {
        var clock = new FakeClock { Now = BeforeExpiry };
        var gate = new ExpirationGate(clock);

        await gate.CheckAsync(Expiry, BuiltAt);
        await gate.CheckAsync(Expiry, BuiltAt);

        Assert.Equal(2, clock.Calls);
    }

    [Fact]
    public async Task Ntp_time_before_expiry_is_allowed()
    {
        var clock = new FakeClock { Now = BeforeExpiry };
        var gate = new ExpirationGate(clock);

        await gate.CheckAsync(Expiry, BuiltAt);

        Assert.Equal(1, clock.Calls);
    }

    [Fact]
    public async Task Ntp_time_at_expiry_blocks()
    {
        var clock = new FakeClock { Now = AtExpiry };
        var gate = new ExpirationGate(clock);

        var ex = await Assert.ThrowsAsync<ExpirationBlockedException>(
            () => gate.CheckAsync(Expiry, BuiltAt));

        Assert.Equal(ExpirationBlockReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task Offline_build_is_rejected()
    {
        var clock = new FakeClock { Fail = true };
        var gate = new ExpirationGate(clock);

        var ex = await Assert.ThrowsAsync<ExpirationBlockedException>(
            () => gate.CheckAsync(Expiry, BuiltAt));

        Assert.Equal(ExpirationBlockReason.TimeUnavailable, ex.Reason);
        Assert.Equal(1, clock.Calls);
    }

    [Fact]
    public async Task Malformed_expiry_fails_closed()
    {
        var clock = new FakeClock { Now = BeforeExpiry };
        var gate = new ExpirationGate(clock);

        var ex = await Assert.ThrowsAsync<ExpirationBlockedException>(
            () => gate.CheckAsync("not-a-date", BuiltAt));

        Assert.Equal(ExpirationBlockReason.Expired, ex.Reason);
        Assert.Equal(1, clock.Calls);
    }

    private sealed class FakeClock : INtpClock
    {
        public DateTimeOffset Now { get; set; } = BeforeExpiry;
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Fail
                ? Task.FromException<DateTimeOffset>(new InvalidOperationException("offline"))
                : Task.FromResult(Now);
        }
    }
}
