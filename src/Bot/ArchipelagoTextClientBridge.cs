using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class ArchipelagoTextClientBridge
{
    private const int DefaultCommandTimeoutMs = 10000;
    private const string ConnectedBanner = "Now that you are connected, you can use !help to list commands to run via the server. If your client supports it, you may have additional local commands you can list with /help.";
    private static readonly System.Text.RegularExpressions.Regex AnsiEscapeRegex = new("\\x1B\\[[0-?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] ClientCandidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new[] { "ArchipelagoTextClient.exe", "TextClient.exe", "CommonClient.exe" }
        : new[] { "ArchipelagoTextClient", "TextClient", "CommonClient" };

    private sealed record TimedLine(DateTimeOffset Timestamp, string Line);

    public static async Task<string> ExecuteHintAsSlotAsync(string slotName, string itemName, string serverAddress)
    {
        var sanitizedSlot = SanitizeLine(slotName);
        var sanitizedItem = SanitizeLine(itemName);

        if (string.IsNullOrWhiteSpace(sanitizedSlot))
        {
            return "Slot name is required.";
        }

        if (string.IsNullOrWhiteSpace(sanitizedItem))
        {
            return "Item name is required.";
        }

        return await ExecuteMultiLineCommandAsSlotAsync(sanitizedSlot, BuildHintCommand(sanitizedItem), serverAddress);
    }

    public static async Task<string> ExecuteCommandAsSlotAsync(string slotName, string clientCommand, string serverAddress)
    {
        var sanitizedSlot = SanitizeLine(slotName);
        var sanitizedCommand = SanitizeLine(clientCommand);

        if (string.IsNullOrWhiteSpace(sanitizedSlot))
        {
            return "Slot name is required.";
        }

        if (string.IsNullOrWhiteSpace(sanitizedCommand))
        {
            return "Command is required.";
        }

        return await ExecuteMultiLineCommandAsSlotAsync(sanitizedSlot, sanitizedCommand, serverAddress);
    }

    private static async Task<string> ExecuteMultiLineCommandAsSlotAsync(string slotName, string command, string serverAddress)
    {
        var textClientPath = ResolveTextClientPath();
        if (string.IsNullOrWhiteSpace(textClientPath))
        {
            return "Archipelago text client not found. Set ARCHIPELAGO_TEXT_CLIENT_PATH to the executable path.";
        }

        if (!File.Exists(textClientPath))
        {
            return $"Archipelago text client not found: {textClientPath}";
        }

        var output = new ConcurrentQueue<TimedLine>();
        var startInfo = new ProcessStartInfo
        {
            FileName = textClientPath,
            WorkingDirectory = Declare.ExtractPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--nogui");
        startInfo.ArgumentList.Add("--connect");
        startInfo.ArgumentList.Add(serverAddress);
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(slotName);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => EnqueueLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => EnqueueLine(output, e.Data);

        try
        {
            if (!process.Start())
            {
                return "Could not start Archipelago text client process.";
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMs = ParseTimeoutOrDefault(DefaultCommandTimeoutMs);
            var connectedAt = await WaitForConnectedBannerAsync(output, timeoutMs);
            if (connectedAt is null)
            {
                await TryExitAsync(process);
                return "Text client did not report successful connection banner in time.";
            }

            var sentAt = DateTimeOffset.UtcNow;
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();

            var commandOutput = await WaitForAllHintResponseLinesAsync(output, sentAt, timeoutMs, command);

            await TryExitAsync(process);

            return string.IsNullOrWhiteSpace(commandOutput)
                ? "No response received from Archipelago text client after sending the command."
                : commandOutput;
        }
        catch (Exception ex)
        {
            return $"Could not execute Archipelago command via text client: {ex.Message}";
        }
    }

    private static string BuildHintCommand(string itemName)
    {
        if (itemName.Contains(' ', StringComparison.Ordinal))
        {
            return $"!hint \"{itemName.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        return $"!hint {itemName}";
    }

    private static async Task<DateTimeOffset?> WaitForConnectedBannerAsync(
        ConcurrentQueue<TimedLine> output,
        int timeoutMs)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var banner = output
                .Where(x => x.Line.Contains(ConnectedBanner, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Timestamp)
                .FirstOrDefault();

            if (banner is not null)
            {
                return banner.Timestamp;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<string?> WaitForAllHintResponseLinesAsync(
        ConcurrentQueue<TimedLine> output,
        DateTimeOffset sentAt,
        int timeoutMs,
        string command)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var settledAt = DateTimeOffset.MinValue;
        var lastCount = 0;
        var linesAfterEcho = new List<string>();

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var lines = output
                .Where(x => x.Timestamp >= sentAt)
                .OrderBy(x => x.Timestamp)
                .Select(x => x.Line)
                .ToList();

            var echoIndex = lines.FindIndex(line => IsCommandEchoLine(line, command));
            if (echoIndex >= 0)
            {
                var nextIndex = echoIndex + 1;
                if (nextIndex < lines.Count)
                {
                    linesAfterEcho.Clear();

                    foreach (var line in lines.Skip(nextIndex))
                    {
                        if (IsCommandResponseLine(line, command))
                        {
                            linesAfterEcho.Add(line);
                        }
                    }

                    if (linesAfterEcho.Count > 0)
                    {
                        if (lines.Count != lastCount)
                        {
                            lastCount = lines.Count;
                            settledAt = DateTimeOffset.UtcNow;
                        }
                        else if (settledAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - settledAt >= TimeSpan.FromMilliseconds(400))
                        {
                            return string.Join(Environment.NewLine, linesAfterEcho);
                        }
                    }
                }
            }

            await Task.Delay(100);
        }

        return linesAfterEcho.Count > 0 ? string.Join(Environment.NewLine, linesAfterEcho) : null;
    }

    private static bool IsCommandEchoLine(string line, string command)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Common text client echo format: "<slot>: !command ..."
        return trimmed.EndsWith(": " + command, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandResponseLine(string line, string command)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (IsCommandEchoLine(trimmed, command))
        {
            return false;
        }

        if (trimmed.Contains(ConnectedBanner, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal) ||
            trimmed.StartsWith("/exit", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static async Task TryExitAsync(Process process)
    {
        var exitCommand = Environment.GetEnvironmentVariable("ARCHIPELAGO_TEXT_CLIENT_EXIT_COMMAND");
        await process.StandardInput.WriteLineAsync(string.IsNullOrWhiteSpace(exitCommand) ? "/exit" : exitCommand.Trim());
        await process.StandardInput.FlushAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(3000));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static void EnqueueLine(ConcurrentQueue<TimedLine> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var cleanedLine = AnsiEscapeRegex.Replace(line, string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (!string.IsNullOrWhiteSpace(cleanedLine))
        {
            output.Enqueue(new TimedLine(DateTimeOffset.UtcNow, cleanedLine));
        }
    }

    private static int ParseTimeoutOrDefault(int fallback)
    {
        var raw = Environment.GetEnvironmentVariable("ARCHIPELAGO_TEXT_CLIENT_TIMEOUT_MS");
        return int.TryParse(raw, out var parsed) && parsed >= 1000
            ? parsed
            : fallback;
    }

    private static string? ResolveTextClientPath()
    {
        var explicitPath = (Environment.GetEnvironmentVariable("ARCHIPELAGO_TEXT_CLIENT_PATH") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        foreach (var candidate in ClientCandidates)
        {
            var path = Path.Combine(Declare.ExtractPath, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string SanitizeLine(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }
}