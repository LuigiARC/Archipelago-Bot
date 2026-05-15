using ArchipelagoSphereTracker.src.Resources;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Data;

public static class BotCommands
{
    private static readonly SemaphoreSlim RegisterCommandsLock = new(1, 1);
    private static readonly TimeSpan RegisterCommandsCooldown = TimeSpan.FromSeconds(10);
    private const int RegisterCommandsMaxConcurrency = 4;
    private const int RegisterCommandsMaxRetries = 3;
    private static readonly TimeSpan RegisterCommandsRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> exemptThreadCommands = new(StringComparer.Ordinal);
    private static DateTimeOffset _lastRegisterCommandsAt = DateTimeOffset.MinValue;
    private static bool _handlersRegistered;

    #region Setup

    public static Task InstallCommandsAsync()
    {
        Declare.Services = new ServiceCollection()
            .AddSingleton(Declare.Client)
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public static async Task RegisterCommandsAsync()
    {
        await RegisterCommandsLock.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastRegisterCommandsAt < RegisterCommandsCooldown)
            {
                return;
            }

            _lastRegisterCommandsAt = now;

            var commands = SlashCommandDefinitions.GetAll();
            var builtCommands = commands
                .Select(cmd => cmd.Build() as ApplicationCommandProperties)
                .ToArray();

            await Parallel.ForEachAsync(
                Declare.Client.Guilds,
                new ParallelOptions { MaxDegreeOfParallelism = RegisterCommandsMaxConcurrency },
                async (guild, _) =>
                {
                    await RegisterGuildCommandsWithRetryAsync(guild, builtCommands);
                });

            if (!_handlersRegistered)
            {
                Console.WriteLine("Registering command handlers");
                Declare.Client.SlashCommandExecuted += HandleSlashCommandAsync;
                Declare.Client.AutocompleteExecuted += HandleAutocompleteAsync;
                _handlersRegistered = true;
            }
        }
        finally
        {
            RegisterCommandsLock.Release();
        }
    }
    
    private static async Task RegisterGuildCommandsWithRetryAsync(SocketGuild guild, ApplicationCommandProperties[] builtCommands)
    {
        for (var attempt = 1; attempt <= RegisterCommandsMaxRetries; attempt++)
        {
            try
            {
                Console.WriteLine($"Registering commands for guild: {guild.Name} ({guild.Id}) [attempt {attempt}/{RegisterCommandsMaxRetries}]");
                await Declare.Client.Rest.BulkOverwriteGuildCommands(builtCommands, guild.Id);
                return;
            }
            catch (Exception ex) when (attempt < RegisterCommandsMaxRetries)
            {
                Console.WriteLine($"Failed to register commands for guild {guild.Name} ({guild.Id}) on attempt {attempt}: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(RegisterCommandsRetryDelay.TotalMilliseconds * attempt));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to register commands for guild {guild.Name} ({guild.Id}) after {RegisterCommandsMaxRetries} attempts: {ex.Message}");
            }
        }
    }

    #endregion

    #region Message Handling

    public static async Task MessageReceivedAsync(SocketMessage arg)
    {
        if (arg is not SocketUserMessage message || message.Author.IsBot) return;

        int argPos = 0;
        if (message.HasStringPrefix("/", ref argPos))
        {
            var context = new SocketCommandContext(Declare.Client, message);
            var result = await Declare.CommandService.ExecuteAsync(context, message.Content[argPos..], Declare.Services);

            if (!result.IsSuccess)
                Console.WriteLine(string.Format(Resource.BotCommandFailed, result.ErrorReason));
        }
    }

    public static async Task SendMessageAsync(string message, string channelIdStr)
    {
        try
        {
            if (!ulong.TryParse(channelIdStr, out var channelId)) return;

            if (Declare.Client.GetChannel(channelId) is not IMessageChannel channel)
            {
                Console.WriteLine(string.Format(Resource.BotChannelNotFound, channelIdStr));
                foreach (var guild in Declare.Client.Guilds)
                {
                    foreach (var textChannel in guild.TextChannels)
                        Console.WriteLine(string.Format(Resource.BotChannelId, textChannel.Name, textChannel.Id));
                }
                return;
            }

            await channel.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Format(Resource.BotSendingError, ex.Message));
        }
    }

    public static async Task SendFileAsync(string channelId, Stream stream, string fileName, string? caption = null)
    {
        var chan = Declare.Client.GetChannel(ulong.Parse(channelId)) as IMessageChannel
                   ?? throw new InvalidOperationException("Channel introuvable");
        await chan.SendFileAsync(stream, fileName, caption);
    }

    #endregion

    #region Slash Command Handler

    public static async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        var isThread = command.Channel is IThreadChannel;
        await command.DeferAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var guildUser = command.User as IGuildUser;
                string channelId = command.ChannelId?.ToString() ?? string.Empty;
                string guildId = command.GuildId?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(channelId))
                {
                    await command.FollowupAsync(Resource.BotCommandOutsideServer);
                    return;
                }

                string? alias = command.Data.Options?.FirstOrDefault(o => o.Name == "alias" || o.Name == "added-alias")?.Value as string;
                string? realAlias = alias;

                const int maxLength = 1999;
                string message;

                if (isThread)
                {
                    message = await HandleThreadedCommand(command, guildUser, alias, realAlias, channelId, guildId);

                    // Some commands intentionally return no visible response (e.g. run-server-command on success).
                    // In that case, explicitly close the deferred interaction to avoid a client-side timeout.
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        await command.DeleteOriginalResponseAsync();
                    }
                    else
                    {
                        await SendPaginatedMessageAsync(command, message, maxLength);
                    }
                }
                else
                {
                    message = await HandleGuildCommand(command, guildUser, alias, channelId, guildId);
                    if (!string.IsNullOrWhiteSpace(message))
                        await command.FollowupAsync(message);
                    else
                        await command.FollowupAsync(Resource.BotCommandDone);
                }
            }
            catch (Exception ex)
            {
                await command.FollowupAsync($"❌ {ex.Message}",
                    options: new RequestOptions { Timeout = 10000 });
            }
        });
    }

    private static async Task<string> HandleThreadedCommand(SocketSlashCommand command, IGuildUser? user, string? alias, string? realAlias, string channelId, string guildId)
    {
        var isWorldThread = await WorldThreadsCommands.IsWorldThreadAsync(guildId, channelId);
        if (!isWorldThread && !exemptThreadCommands.Contains(command.CommandName))
        {
            return "This command can only be used in a world thread created with /create-world.";
        }

        return command.CommandName switch
        {
            "get-aliases" => await AliasClass.GetAlias(channelId, guildId),
            "delete-alias" => await AliasClass.DeleteAlias(command, user, alias, channelId, guildId),
            "add-alias" => await AliasClass.AddAlias(command, alias, channelId, guildId),
            "recap-all" => await RecapAndCleanClass.RecapAll(command, channelId, guildId),
            "recap" => await HandleMappedClientCommandAsync(command, channelId, guildId, "!checked", requireTargetUser: true),
            "recap-and-clean" => await HandleRecapByMentionAsync(command, channelId, guildId, cleanAfter: true),
            "clean" => await HandleCleanByMentionAsync(command, channelId, guildId),
            "clean-all" => await RecapAndCleanClass.CleanAll(command, alias, channelId, guildId),
            "list-items" => await HandleMappedClientCommandAsync(command, channelId, guildId, "!remaining", requireTargetUser: true),
            "hint" => await HandleDirectHintAsync(command, channelId, guildId),
            "status" => await HandleServerCommandAsync(guildId, channelId, "/status"),
            "players" => await HandleServerCommandAsync(guildId, channelId, "/players"),
            "analyze-spoiler-log" => await SpoilerAnalysisClass.AnalyzeSpoilerLog(command, channelId, guildId, alias),
            "send-spoiler-log" => await SpoilerLogClass.SendSpoilerLog(command, channelId),
            "status-games-list" => await HandleMappedClientCommandAsync(command, channelId, guildId, "!status", requireTargetUser: false),
            "excluded-item" => await ExcludedItemsCommands.AddExcludedItemAsync(command, alias, channelId, guildId),
            "excluded-item-list" => await ExcludedItemsCommands.GetExcludedItemsByAliasAsync(command, channelId, guildId),
            "delete-excluded-item" => await ExcludedItemsCommands.DeleteExcludedItemAsync(command, channelId, guildId, alias),
            "list-yamls" => YamlClass.ListYamls(channelId),
            "backup-yamls" => await YamlClass.BackupYamls(command, channelId),
            "delete-yaml" => YamlClass.DeleteYaml(command, channelId, guildId),
            "clean-yamls" => YamlClass.CleanYamls(channelId, guildId),
            "send-yaml" => await YamlClass.SendYaml(command, channelId, guildId),
            "list-apworld" => ApworldClass.ListApworld(),
            "backup-apworld" => await ApworldClass.BackupApworld(command),
            "send-apworld" => await ApworldClass.SendApworld(command),
            "generate" => await GenerationClass.GenerateAsync(command, channelId),
            "test-generate" => await GenerationClass.TestGenerateAsync(command, channelId),
            "generate-with-zip" => await GenerationClass.GenerateWithZip(command, channelId),
            "host-world" => await HostingClass.StartWorldAsync(guildId, channelId),
            "start-world" => await HostingClass.StartWorldAsync(
                guildId,
                channelId,
                command.Data.Options?.FirstOrDefault(o => o.Name == "port")?.Value as long?,
                command.Data.Options?.FirstOrDefault(o => o.Name == "external-domain")?.Value as string,
                command.Data.Options?.FirstOrDefault(o => o.Name == "enable-server-log")?.Value as bool? ?? false),
            "run-server-command" => await HandleRunServerCommandAsync(command, user, guildId, channelId),
            "send-patch" => await HostingClass.SendPatchForSlotAsync(
                guildId,
                channelId,
                command.Data.Options?.FirstOrDefault(o => o.Name == "slot-name")?.Value as string ?? string.Empty),
            "stop-host-world" => HostingClass.StopHost(channelId),
            _ => Resource.BotCommandChannel
        };
    }

    private static async Task<string> HandleGuildCommand(SocketSlashCommand command, IGuildUser? user, string? alias, string channelId, string guildId)
    {
        var worldThreadOnlyGuildCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "list-yamls",
            "backup-yamls",
            "delete-yaml",
            "clean-yamls",
            "send-yaml",
            "generate",
            "test-generate",
            "generate-with-zip",
            "status",
            "players",
            "hint",
            "run-server-command",
            "host-world",
            "start-world",
            "stop-host-world"
        };

        if (worldThreadOnlyGuildCommands.Contains(command.CommandName))
        {
            return "Run this command inside a world thread created with /create-world.";
        }

        return command.CommandName switch
        {
            "download-template" => await YamlClass.DownloadTemplate(command),
            "list-apworld" => ApworldClass.ListApworld(),
            "create-world" => await HostingClass.CreateWorldThreadAsync(
                guildId,
                channelId,
                command.User.Id.ToString(),
                command.Data.Options?.FirstOrDefault(o => o.Name == "world-name")?.Value as string),
            "start-world" => await HostingClass.StartWorldAsync(
                guildId,
                channelId,
                command.Data.Options?.FirstOrDefault(o => o.Name == "port")?.Value as long?,
                command.Data.Options?.FirstOrDefault(o => o.Name == "external-domain")?.Value as string,
                command.Data.Options?.FirstOrDefault(o => o.Name == "enable-server-log")?.Value as bool? ?? false),
            "host-world" => await HostingClass.StartWorldAsync(
                guildId,
                channelId,
                command.Data.Options?.FirstOrDefault(o => o.Name == "port")?.Value as long?,
                command.Data.Options?.FirstOrDefault(o => o.Name == "external-domain")?.Value as string),
            "stop-host-world" => HostingClass.StopHost(channelId),
            "apworlds-info" => string.Format(Resource.ApworldInfo, Declare.ApworldInfoSheet),
            "backup-apworld" => await ApworldClass.BackupApworld(command),
            "send-apworld" => await ApworldClass.SendApworld(command),
            "discord" => Resource.Discord, 
            _ => Resource.BotCommandThread
        };
    }

    private static async Task<string> HandleDirectHintAsync(SocketSlashCommand command, string channelId, string guildId)
    {
        var slotName = command.Data.Options?.FirstOrDefault(o => o.Name == "slot-name")?.Value as string;
        var itemName = command.Data.Options?.FirstOrDefault(o => o.Name == "item-name")?.Value as string;

        if (string.IsNullOrWhiteSpace(slotName))
        {
            return "Slot name is required.";
        }

        if (string.IsNullOrWhiteSpace(itemName))
        {
            return "Item name is required.";
        }

        return await HostingClass.SendHintCommandAsSlotAsync(guildId, channelId, slotName, itemName);
    }

    private static async Task<string> HandleRunServerCommandAsync(SocketSlashCommand command, IGuildUser? user, string guildId, string channelId)
    {
        if (user is null || !user.GuildPermissions.Administrator)
        {
            return "You must have Administrator permission to run server console commands.";
        }

        var serverCommand = command.Data.Options?.FirstOrDefault(o => o.Name == "server-command")?.Value as string;
        if (string.IsNullOrWhiteSpace(serverCommand))
        {
            return "server-command is required.";
        }

        var result = await HostingClass.RunServerConsoleCommandAsync(guildId, channelId, serverCommand);

        return string.Equals(result, "OK", StringComparison.Ordinal) ? "Command sent!" : result;
    }

    private static async Task<string> HandleServerCommandAsync(string guildId, string channelId, string serverCommand)
    {
        var result = await HostingClass.RunServerConsoleCommandAsync(guildId, channelId, serverCommand);
        return string.Equals(result, "OK", StringComparison.Ordinal) ? "Command sent!" : result;
    }

    private static async Task<string> HandleMappedClientCommandAsync(
        SocketSlashCommand command,
        string channelId,
        string guildId,
        string clientCommand,
        bool requireTargetUser)
    {
        var slotName = await ResolveSlotNameForClientCommandAsync(command, guildId, channelId, requireTargetUser);
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return requireTargetUser
                ? "No linked slot found for that user in this room thread."
                : "No linked slot found in this room thread. Link a user to a YAML/slot first.";
        }

        return await HostingClass.SendClientCommandAsSlotAsync(guildId, channelId, slotName, clientCommand);
    }

    private static async Task<string?> ResolveSlotNameForClientCommandAsync(
        SocketSlashCommand command,
        string guildId,
        string channelId,
        bool requireTargetUser)
    {
        if (requireTargetUser)
        {
            var targetUserId = GetOptionUserId(command, "user");
            if (string.IsNullOrWhiteSpace(targetUserId))
            {
                return null;
            }

            return await ResolvePrimaryAliasForUserAsync(guildId, channelId, targetUserId);
        }

        var invokerId = command.User.Id.ToString();
        var invokerAlias = await ResolvePrimaryAliasForUserAsync(guildId, channelId, invokerId);
        if (!string.IsNullOrWhiteSpace(invokerAlias))
        {
            return invokerAlias;
        }

        var knownAliases = await ReceiverAliasesCommands.GetReceiver(guildId, channelId);
        return knownAliases.FirstOrDefault();
    }

    private static async Task<string> HandleGetPatchByMentionAsync(SocketSlashCommand command, string channelId, string guildId)
    {
        var targetUserId = GetOptionUserId(command, "user");
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return Resource.HelperNoId;
        }

        var alias = await ResolvePrimaryAliasForUserAsync(guildId, channelId, targetUserId);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return $"No linked alias found for <@{targetUserId}> in this room thread.";
        }

        return await HelperClass.GetPatchByAlias(alias, channelId, guildId);
    }

    private static async Task<string> HandleListItemsByMentionAsync(SocketSlashCommand command, string channelId, string guildId)
    {
        var targetUserId = GetOptionUserId(command, "user");
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return Resource.HelperNoId;
        }

        var alias = await ResolvePrimaryAliasForUserAsync(guildId, channelId, targetUserId);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return $"No linked alias found for <@{targetUserId}> in this room thread.";
        }

        return await HelperClass.ListItems(command, targetUserId, alias, channelId, guildId);
    }

    private static async Task<string> HandleRecapByMentionAsync(SocketSlashCommand command, string channelId, string guildId, bool cleanAfter)
    {
        var targetUserId = GetOptionUserId(command, "user");
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return Resource.HelperNoId;
        }

        var alias = await ResolvePrimaryAliasForUserAsync(guildId, channelId, targetUserId);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return $"No linked alias found for <@{targetUserId}> in this room thread.";
        }

        return cleanAfter
            ? await RecapAndCleanClass.RecapAndClean(command, alias, channelId, guildId, targetUserId)
            : await RecapAndCleanClass.Recap(command, alias, channelId, guildId, targetUserId);
    }

    private static async Task<string> HandleCleanByMentionAsync(SocketSlashCommand command, string channelId, string guildId)
    {
        var targetUserId = GetOptionUserId(command, "user");
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return Resource.HelperNoId;
        }

        var alias = await ResolvePrimaryAliasForUserAsync(guildId, channelId, targetUserId);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return $"No linked alias found for <@{targetUserId}> in this room thread.";
        }

        return await RecapAndCleanClass.Clean(command, alias, channelId, guildId, targetUserId);
    }

    private static string? GetOptionUserId(SocketSlashCommand command, string optionName)
    {
        var user = command.Data.Options?.FirstOrDefault(o => o.Name == optionName)?.Value as IUser;
        return user?.Id.ToString();
    }

    private static async Task<string?> ResolvePrimaryAliasForUserAsync(string guildId, string channelId, string userId)
    {
        var aliases = await ReceiverAliasesCommands.GetReceiversForUserAsync(guildId, channelId, userId);
        return aliases.FirstOrDefault();
    }

    private static async Task SendPaginatedMessageAsync(SocketSlashCommand command, string message, int maxLength)
    {
        while (message.Length > maxLength)
        {
            var splitIndex = message.LastIndexOf("\n", maxLength);
            if (splitIndex < 0) splitIndex = maxLength;

            var part = message[..splitIndex].Trim();
            message = message[(splitIndex + 1)..].Trim();

            await command.FollowupAsync(part, options: new RequestOptions { Timeout = 10000 });
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            await command.FollowupAsync(message, options: new RequestOptions { Timeout = 10000 });
        }
    }

    #endregion

    #region Autocomplete Handler

    public static async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        var name = interaction.Data.Current.Name;
        var input = interaction.Data.Current.Value?.ToString()?.ToLower() ?? "";
        var channelId = interaction.ChannelId.ToString();
        var guildId = interaction.GuildId?.ToString() ?? "";
        var addedAlias = interaction.Data.Options?.FirstOrDefault(o => o.Name == "added-alias")?.Value as string ?? "";

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(guildId))
        {
            await interaction.RespondAsync(Array.Empty<AutocompleteResult>());
            return;
        }

        Func<Task<IEnumerable<string>>> fetcher = name switch
        {
            "alias" => async () => (await AliasChoicesCommands.GetAliasesForGuildAndChannelAsync(guildId, channelId)).AsEnumerable(),

            "added-alias" => async () =>
                        (await ReceiverAliasesCommands.GetReceiver(guildId, channelId)).Distinct(StringComparer.OrdinalIgnoreCase).AsEnumerable(),

            "yamlfile" => () => Task.FromResult(
                    Directory.Exists(YamlPath(channelId))
                        ? Directory.GetFiles(YamlPath(channelId), "*.yaml").Select(f => Path.GetFileName(f)!).AsEnumerable()
                        : Enumerable.Empty<string>()),

            "template" => () => Task.FromResult(
                    Directory.Exists(TemplatePath())
                        ? Directory.GetFiles(TemplatePath(), "*.yaml").Select(f => Path.GetFileName(f)!).AsEnumerable()
                        : Enumerable.Empty<string>()),

                "archive" => () => Task.FromResult(
                    GenerationClass.GetGeneratedArchiveNames(channelId).AsEnumerable()),

            "items" => async () => (await ExcludedItemsCommands.GetItemNamesForAliasAsync(guildId, channelId, addedAlias)).AsEnumerable(),

            "delete-items" => async () => (await ExcludedItemsCommands.GetExcludedItemsByAliasAsync(guildId, channelId, addedAlias)).AsEnumerable(),

            _ => () => Task.FromResult(Enumerable.Empty<string>())
        };

        var allItems = (await fetcher()).ToList();
        var results = FilterWithPagination(allItems, input);

        await interaction.RespondAsync(results);
    }

    private static string YamlPath(string channelId) => Path.Combine(Declare.BasePath, "extern", "Archipelago", "Players", channelId, "yaml");
    private static string TemplatePath() => Path.Combine(Declare.BasePath, "extern", "Archipelago", "Players", "Templates");

    private static AutocompleteResult[] FilterWithPagination(List<string> all, string input)
    {
        int pageSize = 25;
        int page = 1;

        if (input.StartsWith(">") && int.TryParse(input[1..], out var p) && p > 0)
        {
            page = p;
            input = "";
        }

        var filtered = all
            .Where(x => x.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AutocompleteResult(x, x))
            .ToArray();

        return filtered;
    }

    #endregion
}
