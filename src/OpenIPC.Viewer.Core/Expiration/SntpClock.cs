using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Expiration;

/// <summary>
/// Minimal SNTP client using only BCL types. Only free mainland-China time
/// sources are used; several are tried so one blocked UDP/123 path does not
/// lock out a time-limited build.
/// </summary>
public sealed class SntpClock : INtpClock
{
    private static readonly string[] Servers =
    {
        "ntp.aliyun.com",
        "ntp1.aliyun.com",
        "ntp2.aliyun.com",
        "ntp3.aliyun.com",
        "time1.cloud.tencent.com",
        "time2.cloud.tencent.com",
        "time3.cloud.tencent.com",
        "ntp.sjtu.edu.cn",
    };

    private static readonly DateTimeOffset NtpEpoch =
        new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int NtpPort = 123;
    private const int PacketSize = 48;
    private const byte ClientMode = 0x1B;

    private readonly TimeSpan _serverTimeout;

    public SntpClock(TimeSpan? serverTimeout = null)
    {
        var timeout = serverTimeout ?? TimeSpan.FromSeconds(3);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(serverTimeout));
        _serverTimeout = timeout;
    }

    public async Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var server in Servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await QueryAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("No public NTP server responded.", lastError);
    }

    private async Task<DateTimeOffset> QueryAsync(string server, CancellationToken cancellationToken)
    {
        using (var client = new UdpClient())
        {
            var request = new byte[PacketSize];
            request[0] = ClientMode;

            var sentAt = DateTimeOffset.UtcNow;
            var receiveTask = client.ReceiveAsync();
            await client.SendAsync(request, request.Length, server, NtpPort).ConfigureAwait(false);

            var timeoutTask = Task.Delay(_serverTimeout, cancellationToken);
            var completed = await Task.WhenAny(receiveTask, timeoutTask).ConfigureAwait(false);
            if (completed != receiveTask)
            {
                Observe(receiveTask);
                await timeoutTask.ConfigureAwait(false);
                throw new TimeoutException($"NTP server {server} did not respond.");
            }

            var reply = await receiveTask.ConfigureAwait(false);
            var data = reply.Buffer;
            if (data.Length < PacketSize)
                throw new InvalidOperationException("Short NTP reply.");
            if ((data[0] & 0x07) != 0x04)
                throw new InvalidOperationException("Not an NTP server reply.");
            var stratum = data[1];
            if (stratum == 0 || stratum > 15)
                throw new InvalidOperationException("NTP server is not synchronized.");

            var receivedAt = DateTimeOffset.UtcNow;
            var serverReceive = ReadTimestamp(data, 32);
            var serverSend = ReadTimestamp(data, 40);
            var offsetTicks = ((serverReceive - sentAt).Ticks + (serverSend - receivedAt).Ticks) / 2;
            return receivedAt.AddTicks(offsetTicks);
        }
    }

    private static void Observe(Task task) =>
        task.ContinueWith(
            t => { _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private static DateTimeOffset ReadTimestamp(byte[] data, int offset)
    {
        var seconds = ReadUInt32(data, offset);
        var fraction = ReadUInt32(data, offset + 4);
        var ticks = checked((long)seconds * TimeSpan.TicksPerSecond
                            + (long)fraction * TimeSpan.TicksPerSecond / 0x1_0000_0000L);
        return NtpEpoch.AddTicks(ticks);
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24)
        | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8)
        | data[offset + 3];
}
