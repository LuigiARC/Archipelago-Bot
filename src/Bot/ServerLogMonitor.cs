using Discord;
using System.Collections.Concurrent;
using System.Diagnostics;

public class ServerLogMonitor
{
    private const int DiscordMessageMaxLength = 2000;
    private const int SafeMessageLength = 1900;
    private const int MaxBatchLines = 30;
    private const int BatchDelayMs = 750;
    private const int PollDelayMs = 100;

    private readonly string _guildId;
    private readonly string _channelId;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private Task? _monitoringTask;
    private readonly ConcurrentQueue<string> _outputQueue;
    private static readonly string[] SuppressedLogFragments =
    {
        "connection rejected (400 bad request)",
        "connection closed"
    };

    public ServerLogMonitor(string guildId, string channelId)
    {
        _guildId = guildId;
        _channelId = channelId;
        _cancellationTokenSource = new CancellationTokenSource();
        _outputQueue = new ConcurrentQueue<string>();
    }

    /// <summary>
    /// Starts monitoring the server process output and posting to Discord thread
    /// </summary>
    public void Start(Process process)
    {
        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
        {
            return; // Already running
        }

        _monitoringTask = Task.Run(async () => await MonitorProcessAsync(process));
    }

    /// <summary>
    /// Called by HostingClass when a new line is available from server output
    /// </summary>
    public void EnqueueLine(string line)
    {
        if (!string.IsNullOrWhiteSpace(line) && !ShouldSuppressLine(line))
        {
            _outputQueue.Enqueue(line);
        }
    }

    private static bool ShouldSuppressLine(string line)
    {
        foreach (var fragment in SuppressedLogFragments)
        {
            if (line.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task MonitorProcessAsync(Process process)
    {
        try
        {
            var channel = Declare.Client.GetChannel(ulong.Parse(_channelId)) as ITextChannel;
            if (channel == null)
            {
                Console.WriteLine($"[ServerLogMonitor] Could not find channel {_channelId}");
                return;
            }

            var batchBuffer = new List<string>();
            var lastFlushUtc = DateTime.UtcNow;

            while (!_cancellationTokenSource.Token.IsCancellationRequested && !process.HasExited)
            {
                try
                {
                    while (_outputQueue.TryDequeue(out var line))
                    {
                        batchBuffer.Add(line);

                        if (batchBuffer.Count >= MaxBatchLines || ComputeMessageLength(batchBuffer) >= SafeMessageLength)
                        {
                            await SendBufferedLinesAsync(channel, batchBuffer);
                            batchBuffer.Clear();
                            lastFlushUtc = DateTime.UtcNow;
                        }
                    }

                    var flushDue = (DateTime.UtcNow - lastFlushUtc).TotalMilliseconds >= BatchDelayMs;
                    if (batchBuffer.Count > 0 && flushDue)
                    {
                        await SendBufferedLinesAsync(channel, batchBuffer);
                        batchBuffer.Clear();
                        lastFlushUtc = DateTime.UtcNow;
                    }

                    if (batchBuffer.Count == 0)
                    {
                        await Task.Delay(PollDelayMs, _cancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ServerLogMonitor] Error in monitoring loop: {ex.Message}");
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
            }

            if (batchBuffer.Count > 0)
            {
                await SendBufferedLinesAsync(channel, batchBuffer);
            }

            // Send any remaining lines when process exits
            await SendRemainingLinesAsync(channel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServerLogMonitor] Fatal error: {ex.Message}");
        }
    }

    private static int ComputeMessageLength(IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0)
        {
            return 0;
        }

        return lines.Sum(line => line.Length) + ((lines.Count - 1) * Environment.NewLine.Length);
    }

    private async Task SendBufferedLinesAsync(ITextChannel channel, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var message = string.Join(Environment.NewLine, lines);
        var chunks = ChunkMessage(message, SafeMessageLength);
        foreach (var chunk in chunks)
        {
            try
            {
                await channel.SendMessageAsync(chunk);
                await Task.Delay(100, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerLogMonitor] Error sending message chunk: {ex.Message}");
            }
        }
    }

    private async Task SendRemainingLinesAsync(ITextChannel channel)
    {
        var remainingLines = new List<string>();
        while (_outputQueue.TryDequeue(out var line))
        {
            remainingLines.Add(line);
        }

        await SendBufferedLinesAsync(channel, remainingLines);
    }

    private List<string> ChunkMessage(string message, int maxLength)
    {
        var chunks = new List<string>();
        var lines = message.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        var currentChunk = "";

        foreach (var line in lines)
        {
            if (line.Length > maxLength)
            {
                if (!string.IsNullOrEmpty(currentChunk))
                {
                    chunks.Add(currentChunk);
                    currentChunk = "";
                }

                var start = 0;
                while (start < line.Length)
                {
                    var take = Math.Min(maxLength, line.Length - start);
                    chunks.Add(line.Substring(start, take));
                    start += take;
                }

                continue;
            }

            if ((currentChunk + Environment.NewLine + line).Length > maxLength && !string.IsNullOrEmpty(currentChunk))
            {
                chunks.Add(currentChunk);
                currentChunk = line;
            }
            else
            {
                currentChunk = string.IsNullOrEmpty(currentChunk) ? line : currentChunk + Environment.NewLine + line;
            }
        }

        if (!string.IsNullOrEmpty(currentChunk))
        {
            chunks.Add(currentChunk);
        }

        return chunks;
    }

    /// <summary>
    /// Stops the log monitor and waits for pending operations
    /// </summary>
    public async Task StopAsync()
    {
        _cancellationTokenSource.Cancel();
        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        _cancellationTokenSource.Dispose();
    }
}
