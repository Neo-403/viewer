using OpenIPC.Viewer.Core;

namespace OpenIPC.Viewer.Core.Tests;

public sealed class BuildExpirationTests
{
    private const string BuiltAt = "2026-11-09T08:30:00.0000000Z";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_spec_never_expires(string? spec)
    {
        Assert.Null(BuildExpiration.Resolve(spec, BuiltAt));
        Assert.False(BuildExpiration.IsExpired(spec, BuiltAt, new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Days_are_counted_from_the_build_timestamp() =>
        Assert.Equal(
            new DateTimeOffset(2027, 11, 9, 8, 30, 0, TimeSpan.Zero),
            BuildExpiration.Resolve("365", BuiltAt));

    [Fact]
    public void Date_is_the_last_usable_utc_day() =>
        Assert.Equal(
            new DateTimeOffset(2027, 11, 10, 0, 0, 0, TimeSpan.Zero),
            BuildExpiration.Resolve("2027-11-09", BuiltAt));

    [Fact]
    public void Date_does_not_depend_on_the_build_timestamp() =>
        Assert.Equal(
            new DateTimeOffset(2027, 11, 10, 0, 0, 0, TimeSpan.Zero),
            BuildExpiration.Resolve(" 2027-11-09 ", null));

    [Fact]
    public void Expiry_check_uses_the_supplied_clock()
    {
        var now = new DateTimeOffset(2026, 11, 19, 8, 29, 59, TimeSpan.Zero);
        Assert.False(BuildExpiration.IsExpired("10", BuiltAt, now));
        Assert.True(BuildExpiration.IsExpired("10", BuiltAt, now.AddSeconds(1)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("365 days")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("2027-13-09")]
    [InlineData("9/11/2027")]
    public void Malformed_spec_fails_closed(string spec) =>
        Assert.Equal(DateTimeOffset.MinValue, BuildExpiration.Resolve(spec, BuiltAt));

    [Fact]
    public void Days_without_a_build_timestamp_fail_closed() =>
        Assert.Equal(DateTimeOffset.MinValue, BuildExpiration.Resolve("30", null));

    [Fact]
    public void Missing_expiry_instant_on_a_limited_build_fails_closed() =>
        Assert.True(BuildExpiration.IsExpired("garbage", BuiltAt, DateTimeOffset.UtcNow));

    [Fact]
    public void Huge_day_count_clamps_instead_of_throwing() =>
        Assert.Equal(DateTimeOffset.MaxValue, BuildExpiration.Resolve("2000000000", BuiltAt));
}
