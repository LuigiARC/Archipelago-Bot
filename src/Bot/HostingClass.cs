using ArchipelagoSphereTracker.src.Resources;
using Discord;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

public class HostingClass : Declare
{
    private const string HostNotRunningMessage = "No local Archipelago host is running.";
    private const string HostRunningMessage = "Local Archipelago host is running for channel {0}.";
    private const string HostZipNotFoundMessage = "Generated multiworld zip was not found.";
    private const string HostServerNotFoundMessage = "ArchipelagoServer not found: {0}";
    private const string HostStoppedMessage = "Local Archipelago host stopped.";
    private const string HostStartFailedMessage = "Could not start ArchipelagoServer.";
    private const string HostStartErrorMessage = "Could not start local host: {0}";
    private const string HostStartedMessage = "Local Archipelago host started for channel {0}. Zip: {1}. Connect with {2}.";
    private const string HostStopErrorMessage = "Could not stop local host: {0}";
    private const string HostThreadCreateWarning = "Could not create a room thread automatically; continuing in this channel.";
    private const string HostThreadUsersAddedMessage = "Auto-added linked users to room thread: {0}.";
    private const string CreateWorldThreadMessage = "World thread ready: <#{0}>. Upload YAML files in this thread, then generate and host.";
    private const string HostNotRunningForChannelMessage = "No local Archipelago host is running for this channel/thread.";
    private const string HostPortInUseMessage = "A local Archipelago host is already running on port {0} for channel {1}. Use a different port to run multiple servers.";
    private const int DefaultHostPort = 38281;

    private static readonly object HostLock = new();
    private static readonly Dictionary<string, HostSession> HostSessionsByChannel = new(StringComparer.Ordinal);

    private sealed class HostSession
    {
        public required string ChannelId { get; init; }
        public required Process Process { get; init; }
        public required string ZipPath { get; init; }
        public required int LocalPort { get; init; }
        public required string LocalServerAddress { get; init; }
        public required string DisplayConnectAddress { get; init; }
        public ServerLogMonitor? LogMonitor { get; init; }
    }

    public static bool IsHostRunning()
    {
        lock (HostLock)
        {
            CleanupExitedHostsLocked();
            return HostSessionsByChannel.Values.Any(session => !session.Process.HasExited);
        }
    }

    public static string GetHostStatus()
    {
        lock (HostLock)
        {
            CleanupExitedHostsLocked();

            var session = HostSessionsByChannel.Values.FirstOrDefault(s => !s.Process.HasExited);
            if (session is null)
            {
                return HostNotRunningMessage;
            }

            return string.Format(HostRunningMessage, session.ChannelId);
        }
    }

    public static async Task<string> HostWorldAsync(
        string guildId,
        string channelId,
        string? archiveName = null,
        bool createRoomThread = true,
        bool enableServerLog = false,
        string? connectAddressOverride = null,
        long? localPort = null)
    {
        var normalizedPortResult = NormalizePort(localPort);
        if (!normalizedPortResult.IsValid)
        {
            return normalizedPortResult.ErrorMessage!;
        }

        var localPortValue = normalizedPortResult.Port;

        lock (HostLock)
        {
            CleanupExitedHostsLocked();

            if (TryGetRunningSessionLocked(channelId, out _))
            {
                return string.Format(HostRunningMessage, channelId);
            }

            var channelUsingPort = FindChannelUsingPortLocked(localPortValue, channelId);
            if (!string.IsNullOrWhiteSpace(channelUsingPort))
            {
                return string.Format(HostPortInUseMessage, localPortValue, channelUsingPort);
            }
        }

        var archivePath = string.IsNullOrWhiteSpace(archiveName)
            ? GenerationClass.GetLatestGeneratedArchivePath(channelId)
            : GenerationClass.GetGeneratedArchivePath(channelId, archiveName);

        if (archivePath is null)
        {
            var generationResult = await GenerationClass.GenerateAsyncForWeb(channelId);
            if (!string.IsNullOrWhiteSpace(generationResult.Message))
            {
                return generationResult.Message;
            }

            if (string.IsNullOrWhiteSpace(generationResult.ZipPath) || !File.Exists(generationResult.ZipPath))
            {
                return HostZipNotFoundMessage;
            }
            archivePath = generationResult.ZipPath;
        }

        var serverPath = GetServerPath();
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            return string.Format(HostServerNotFoundMessage, serverPath);
        }

        var startInfo = CreateProcessStartInfo(serverPath, archivePath, localPortValue);

        try
        {
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            // Initialize log monitor if enabled
            ServerLogMonitor? monitor = null;
            if (enableServerLog)
            {
                monitor = new ServerLogMonitor(guildId, channelId);
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.WriteLine(e.Data);
                    monitor?.EnqueueLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var message = "⚠️ " + e.Data;
                    Console.WriteLine(message);
                    monitor?.EnqueueLine(message);
                }
            };

            process.Exited += (_, _) =>
            {
                ServerLogMonitor? monitorToStop = null;

                lock (HostLock)
                {
                    if (HostSessionsByChannel.TryGetValue(channelId, out var runningSession)
                        && ReferenceEquals(runningSession.Process, process))
                    {
                        HostSessionsByChannel.Remove(channelId);
                        monitorToStop = runningSession.LogMonitor;
                    }
                }

                process.Dispose();
                if (monitorToStop != null)
                {
                    _ = monitorToStop.StopAsync();
                }

                Console.WriteLine(HostStoppedMessage);
            };

            if (!process.Start())
            {
                process.Dispose();
                return HostStartFailedMessage;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Ensure console commands are sent as complete lines immediately.
            process.StandardInput.NewLine = "\n";
            process.StandardInput.AutoFlush = true;

            var localServerAddress = $"localhost:{localPortValue.ToString(CultureInfo.InvariantCulture)}";
            var displayConnectAddress = string.IsNullOrWhiteSpace(connectAddressOverride)
                ? localServerAddress
                : connectAddressOverride.Trim();

            lock (HostLock)
            {
                HostSessionsByChannel[channelId] = new HostSession
                {
                    ChannelId = channelId,
                    Process = process,
                    ZipPath = archivePath,
                    LocalPort = localPortValue,
                    LocalServerAddress = localServerAddress,
                    DisplayConnectAddress = displayConnectAddress,
                    LogMonitor = monitor
                };
            }

            // Start the log monitor after the process has started
            if (monitor != null)
            {
                monitor.Start(process);
            }

            if (process.HasExited)
            {
                lock (HostLock)
                {
                    if (HostSessionsByChannel.TryGetValue(channelId, out var runningSession)
                        && ReferenceEquals(runningSession.Process, process))
                    {
                        HostSessionsByChannel.Remove(channelId);
                        _ = runningSession.LogMonitor?.StopAsync();
                    }
                }

                process.Dispose();

                return string.Format(HostStartErrorMessage, $"ArchipelagoServer exited immediately with code {process.ExitCode}.");
            }

            var archiveLabel = string.IsNullOrWhiteSpace(archiveName) ? Path.GetFileName(archivePath) : archiveName;
            var roomThread = createRoomThread
                ? await CreateRoomThreadAsync(channelId, archiveLabel)
                : null;
            var effectiveChannelId = roomThread?.Id.ToString() ?? channelId;

            lock (HostLock)
            {
                if (HostSessionsByChannel.TryGetValue(channelId, out var runningSession)
                    && ReferenceEquals(runningSession.Process, process))
                {
                    HostSessionsByChannel.Remove(channelId);
                    HostSessionsByChannel[effectiveChannelId] = new HostSession
                    {
                        ChannelId = effectiveChannelId,
                        Process = runningSession.Process,
                        ZipPath = runningSession.ZipPath,
                        LocalPort = runningSession.LocalPort,
                        LocalServerAddress = runningSession.LocalServerAddress,
                        DisplayConnectAddress = runningSession.DisplayConnectAddress,
                        LogMonitor = runningSession.LogMonitor
                    };
                }
            }

            var connectAddress = displayConnectAddress;

            var message = string.Format(HostStartedMessage, effectiveChannelId, archiveLabel, connectAddress);
            var lines = new List<string> { message };

            if (roomThread != null)
            {
                lines.Add($"Room thread created: <#{roomThread.Id}>");
            }
            else if (createRoomThread)
            {
                lines.Add(HostThreadCreateWarning);
            }

            if (enableServerLog)
            {
                lines.Add("✅ Server output logging enabled for this thread.");
            }

            var portalLinks = await BuildPortalLinksAsync(guildId, effectiveChannelId);
            if (!string.IsNullOrWhiteSpace(portalLinks))
            {
                lines.Add(portalLinks);
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return string.Format(HostStartErrorMessage, ex.Message);
        }
    }

    public static async Task<string> StartWorldAsync(string guildId, string channelId, long? port = null, string? externalDomain = null, bool enableServerLog = false)
    {
        var isWorldThread = await WorldThreadsCommands.IsWorldThreadAsync(guildId, channelId);
        if (!isWorldThread)
        {
            return "This command can only be used in a world thread created with /create-world.";
        }

        var effectiveUrl = ResolveStartWorldUrl(port, externalDomain);
        var message = await HostWorldAsync(
            guildId,
            channelId,
            archiveName: null,
            createRoomThread: false,
            enableServerLog,
            connectAddressOverride: effectiveUrl,
            localPort: port);
        if (IsHostRunningForChannel(channelId))
        {
            await WorldThreadsCommands.MarkWorldStartedAsync(guildId, channelId);
            if (enableServerLog)
            {
                await WorldThreadsCommands.SetServerLogEnabledAsync(guildId, channelId, true);
            }
        }

        return message;
    }

    public static async Task<string> SendHintCommandAsSlotAsync(string guildId, string channelId, string slotName, string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return "Item name is required.";
        }

        var hintCommand = itemName.Contains(' ', StringComparison.Ordinal)
            ? $"!hint \"{itemName.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"!hint {itemName}";

        return await SendClientCommandAsSlotAsync(guildId, channelId, slotName, hintCommand);
    }

    public static async Task<string> SendClientCommandAsSlotAsync(string guildId, string channelId, string slotName, string clientCommand)
    {
        var isWorldThread = await WorldThreadsCommands.IsWorldThreadAsync(guildId, channelId);
        if (!isWorldThread)
        {
            return "This command can only be used in a world thread created with /create-world.";
        }

        HostSession? hostSession;
        lock (HostLock)
        {
            CleanupExitedHostsLocked();
            if (!TryGetRunningSessionLocked(channelId, out hostSession))
            {
                return HostNotRunningForChannelMessage;
            }
        }

        var serverAddress = hostSession.LocalServerAddress;

        return await ArchipelagoTextClientBridge.ExecuteCommandAsSlotAsync(slotName, clientCommand, serverAddress);
    }

    public static async Task<string> SendPatchForSlotAsync(string guildId, string channelId, string slotName)
    {
        var isWorldThread = await WorldThreadsCommands.IsWorldThreadAsync(guildId, channelId);
        if (!isWorldThread)
        {
            return "This command can only be used in a world thread created with /create-world.";
        }

        if (string.IsNullOrWhiteSpace(slotName))
        {
            return "Slot name is required.";
        }

        var archivePath = ResolveArchivePathForChannel(channelId);
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return HostZipNotFoundMessage;
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .ToList();

            if (entries.Count == 0)
            {
                return $"No files were found in generated archive '{Path.GetFileName(archivePath)}'.";
            }

            var exactMatches = entries
                .Select(entry => new
                {
                    Entry = entry,
                    Slot = ExtractSlotNameFromPatch(entry.Name)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Slot)
                    && string.Equals(x.Slot, slotName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var selected = exactMatches;

            if (selected.Count == 0)
            {
                var normalizedInput = NormalizeSlotName(slotName);
                selected = entries
                    .Select(entry => new
                    {
                        Entry = entry,
                        Slot = ExtractSlotNameFromPatch(entry.Name)
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Slot)
                        && string.Equals(NormalizeSlotName(x.Slot!), normalizedInput, StringComparison.Ordinal))
                    .ToList();
            }

            if (selected.Count == 0)
            {
                return $"No patch file found for slot '{slotName}' in '{Path.GetFileName(archivePath)}'.";
            }

            if (selected.Count > 1)
            {
                var names = string.Join(", ", selected.Select(x => x.Entry.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                return $"Multiple patch files match slot '{slotName}': {names}. Please refine the slot name.";
            }

            var entryToSend = selected[0].Entry;
            await using var entryStream = entryToSend.Open();
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer);
            buffer.Position = 0;

            await BotCommands.SendFileAsync(channelId, buffer, entryToSend.Name, $"Patch for slot {slotName}");
            return $"Patch sent: {entryToSend.Name}";
        }
        catch (Exception ex)
        {
            return $"Could not send patch file for slot '{slotName}': {ex.Message}";
        }
    }

    public static async Task<string> RunServerConsoleCommandAsync(string guildId, string channelId, string serverCommand)
    {
        var isWorldThread = await WorldThreadsCommands.IsWorldThreadAsync(guildId, channelId);
        if (!isWorldThread)
        {
            return "This command can only be used in a world thread created with /create-world.";
        }

        if (string.IsNullOrWhiteSpace(serverCommand))
        {
            return "server-command is required.";
        }

        var trimmedCommand = serverCommand.Trim();

        lock (HostLock)
        {
            CleanupExitedHostsLocked();

            if (!TryGetRunningSessionLocked(channelId, out var hostSession))
            {
                return HostNotRunningForChannelMessage;
            }

            if (hostSession.Process.StartInfo.RedirectStandardInput is false)
            {
                return "Server stdin is not available for command execution.";
            }

            try
            {
                Console.WriteLine($"[AST] Sending server command: {trimmedCommand}");
                hostSession.Process.StandardInput.Write(trimmedCommand);
                hostSession.Process.StandardInput.Write("\n");
                hostSession.Process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                return $"Could not write command to ArchipelagoServer console: {ex.Message}";
            }
        }

        return "OK";
    }

    public static string StopHost()
    {
        lock (HostLock)
        {
            CleanupExitedHostsLocked();
            if (HostSessionsByChannel.Count == 0)
            {
                return HostNotRunningMessage;
            }

            foreach (var session in HostSessionsByChannel.Values.ToList())
            {
                TryStopSessionLocked(session);
            }

            HostSessionsByChannel.Clear();
            return HostStoppedMessage;
        }
    }

    public static string StopHost(string channelId)
    {
        lock (HostLock)
        {
            CleanupExitedHostsLocked();

            if (!TryGetRunningSessionLocked(channelId, out var hostSession))
            {
                return HostNotRunningForChannelMessage;
            }

            var stopResult = TryStopSessionLocked(hostSession);
            HostSessionsByChannel.Remove(channelId);

            if (!string.IsNullOrWhiteSpace(stopResult))
            {
                return stopResult;
            }

            return HostStoppedMessage;
        }
    }

    public static async Task<string> CreateWorldThreadAsync(string guildId, string parentChannelId, string requesterUserId, string? worldName)
    {
        var roomThread = await CreateRoomThreadAsync(parentChannelId, worldName ?? "Archipelago World");
        if (roomThread == null)
        {
            return HostThreadCreateWarning;
        }

        await EnsureCleanThreadWorkspaceAsync(guildId, roomThread.Id.ToString());
        await WorldThreadsCommands.RegisterWorldThreadAsync(
            guildId,
            roomThread.Id.ToString(),
            parentChannelId,
            roomThread.Name);

        if (ulong.TryParse(requesterUserId, out var requesterId))
        {
            var guild = Declare.Client.GetGuild(ulong.Parse(guildId));
            var requester = guild?.GetUser(requesterId);
            if (requester != null)
            {
                try
                {
                    await roomThread.AddUserAsync(requester);
                }
                catch
                {
                    // Best effort: requester may already be in thread.
                }
            }
        }

        var lines = new List<string>
        {
            string.Format(CreateWorldThreadMessage, roomThread.Id)
        };

        var portalLinks = await BuildPortalLinksAsync(guildId, roomThread.Id.ToString());
        if (!string.IsNullOrWhiteSpace(portalLinks))
        {
            lines.Add(portalLinks);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetServerPath()
    {
        var serverName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ArchipelagoServer.exe"
            : "ArchipelagoServer";

        return Path.Combine(ExtractPath, serverName);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string serverPath, string zipPath, int localPort)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            WorkingDirectory = ExtractPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(Path.GetFullPath(zipPath));
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(localPort.ToString(CultureInfo.InvariantCulture));

        return startInfo;
    }

    private static string GetDefaultHostAddress(int localPort)
    {
        return $"localhost:{localPort.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task<IThreadChannel?> CreateRoomThreadAsync(string parentChannelId, string archiveLabel)
    {
        if (!ulong.TryParse(parentChannelId, out var parsedChannelId))
        {
            return null;
        }

        if (Declare.Client.GetChannel(parsedChannelId) is not ITextChannel textChannel)
        {
            return null;
        }

        var threadTitle = BuildHostThreadTitle(archiveLabel);

        try
        {
            return await textChannel.CreateThreadAsync(
                threadTitle,
                autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                type: ThreadType.PublicThread);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Could not create host thread: {ex.Message}");
            return null;
        }
    }

    private static string BuildHostThreadTitle(string archiveLabel)
    {
        var label = Path.GetFileNameWithoutExtension(archiveLabel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "Archipelago World";
        }

        const int maxLength = 90;
        if (label.Length > maxLength)
        {
            label = label[..maxLength].Trim();
        }

        return $"AP Room - {label}";
    }

    private static async Task<int> SyncYamlMappingsToHostThreadAsync(string guildId, string sourceChannelId, IThreadChannel roomThread)
    {
        var threadChannelId = roomThread.Id.ToString();
        var mappings = await YamlUserMappingsCommands.GetMappingsAsync(guildId, threadChannelId);

        if (mappings.Count == 0)
        {
            return 0;
        }

        var addedUsersCount = 0;
        var addedUserIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Alias) || string.IsNullOrWhiteSpace(mapping.UserId))
            {
                continue;
            }

            var currentAliasUsers = await ReceiverAliasesCommands.GetAllUsersIds(guildId, threadChannelId, mapping.Alias);
            if (!currentAliasUsers.Contains(mapping.UserId, StringComparer.Ordinal))
            {
                await ReceiverAliasesCommands.InsertReceiverAlias(guildId, threadChannelId, mapping.Alias, mapping.UserId, "0");
            }

            var recapExists = await RecapListCommands.CheckIfExists(guildId, threadChannelId, mapping.UserId, mapping.Alias);
            if (!recapExists)
            {
                await RecapListCommands.AddOrEditRecapListAsync(guildId, threadChannelId, mapping.UserId, mapping.Alias);
            }

            if (!addedUserIds.Add(mapping.UserId))
            {
                continue;
            }

            if (!ulong.TryParse(mapping.UserId, out var userId))
            {
                continue;
            }

            var guild = Declare.Client.GetGuild(ulong.Parse(guildId));
            var user = guild?.GetUser(userId);
            if (user == null)
            {
                continue;
            }

            try
            {
                await roomThread.AddUserAsync(user);
                addedUsersCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not auto-add user {mapping.UserId} to thread {threadChannelId}: {ex.Message}");
            }
        }

        return addedUsersCount;
    }

    private static async Task EnsureCleanThreadWorkspaceAsync(string guildId, string channelId)
    {
        // Keep player YAMLs and generated outputs persistent across thread lifecycle events.
        await YamlUserMappingsCommands.DeleteMappingsForChannelAsync(guildId, channelId);
    }

    private static string ResolveStartWorldUrl(long? port, string? externalDomain)
    {
        var normalizedPortResult = NormalizePort(port);
        var effectivePort = normalizedPortResult.Port;

        var baseValue = string.IsNullOrWhiteSpace(externalDomain)
            ? (Environment.GetEnvironmentVariable("WEB_BASE_URL") ?? string.Empty).Trim()
            : externalDomain.Trim();

        if (string.IsNullOrWhiteSpace(baseValue))
        {
            baseValue = (Environment.GetEnvironmentVariable("ARCHIPELAGO_SERVER_ADDRESS") ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(baseValue))
        {
            baseValue = GetDefaultHostAddress(effectivePort);
        }

        if (Uri.TryCreate(baseValue, UriKind.Absolute, out var absoluteUri) ||
            Uri.TryCreate("http://" + baseValue, UriKind.Absolute, out absoluteUri))
        {
            var builder = new UriBuilder(absoluteUri)
            {
                Port = effectivePort
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return baseValue.Contains(':', StringComparison.Ordinal)
            ? baseValue
            : $"{baseValue}:{effectivePort.ToString(CultureInfo.InvariantCulture)}";
    }

    private static Task<string?> BuildPortalLinksAsync(string guildId, string channelId)
    {
        return Task.FromResult<string?>(null);
    }

    private static string? ResolveArchivePathForChannel(string channelId)
    {
        lock (HostLock)
        {
            CleanupExitedHostsLocked();

            if (TryGetRunningSessionLocked(channelId, out var session)
                && !string.IsNullOrWhiteSpace(session.ZipPath)
                && File.Exists(session.ZipPath))
            {
                return session.ZipPath;
            }
        }

        return GenerationClass.GetLatestGeneratedArchivePath(channelId);
    }

    private static string? ExtractSlotNameFromPatch(string fileName)
    {
        var match = Regex.Match(fileName, @"^AP_[^_]+_P\d+_(?<slot>.+?)\.[^.]+$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["slot"].Value;
    }

    private static string NormalizeSlotName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsHostRunningForChannel(string channelId)
    {
        lock (HostLock)
        {
            CleanupExitedHostsLocked();
            return TryGetRunningSessionLocked(channelId, out _);
        }
    }

    private static bool TryGetRunningSessionLocked(string channelId, out HostSession session)
    {
        if (HostSessionsByChannel.TryGetValue(channelId, out var existing)
            && !existing.Process.HasExited)
        {
            session = existing;
            return true;
        }

        session = null!;
        return false;
    }

    private static string? FindChannelUsingPortLocked(int port, string? excludedChannelId)
    {
        foreach (var kvp in HostSessionsByChannel)
        {
            var channel = kvp.Key;
            var session = kvp.Value;
            if (session.Process.HasExited)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(excludedChannelId)
                && string.Equals(channel, excludedChannelId, StringComparison.Ordinal))
            {
                continue;
            }

            if (session.LocalPort == port)
            {
                return channel;
            }
        }

        return null;
    }

    private static (bool IsValid, int Port, string? ErrorMessage) NormalizePort(long? requestedPort)
    {
        if (!requestedPort.HasValue)
        {
            return (true, DefaultHostPort, null);
        }

        if (requestedPort.Value is < 1 or > 65535)
        {
            return (false, DefaultHostPort, "Port must be between 1 and 65535.");
        }

        return (true, (int)requestedPort.Value, null);
    }

    private static string? TryStopSessionLocked(HostSession session)
    {
        try
        {
            session.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            return string.Format(HostStopErrorMessage, ex.Message);
        }

        try
        {
            session.Process.Dispose();
        }
        catch
        {
            // Best effort disposal only.
        }

        if (session.LogMonitor != null)
        {
            _ = session.LogMonitor.StopAsync();
        }

        return null;
    }

    private static void CleanupExitedHostsLocked()
    {
        var exitedChannels = HostSessionsByChannel
            .Where(kvp => kvp.Value.Process.HasExited)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var channel in exitedChannels)
        {
            if (!HostSessionsByChannel.TryGetValue(channel, out var session))
            {
                continue;
            }

            try
            {
                session.Process.Dispose();
            }
            catch
            {
                // Best effort disposal only.
            }

            if (session.LogMonitor != null)
            {
                _ = session.LogMonitor.StopAsync();
            }

            HostSessionsByChannel.Remove(channel);
        }
    }
}
