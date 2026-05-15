using ArchipelagoSphereTracker.src.Resources;
using Discord;

public static class SlashCommandDefinitions
{
    public static IEnumerable<SlashCommandBuilder> GetAll()
    {
        var commands = new List<SlashCommandBuilder>
        {
            new SlashCommandBuilder().WithName("get-aliases").WithDescription(Resource.SCGetAliasesDescription),

            new SlashCommandBuilder()
            .WithName("add-alias")
            .WithDescription(Resource.SCAddAliasDescription)
            .AddOption(AliasOption("alias"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName(Resource.SCAddAliasSkipMention)
                    .WithDescription(Resource.SCAddAliasSkipMentionDescription)
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .AddChoice($"{Resource.None}", "0")
                    .AddChoice($"{Resource.Filler}", "1")
                    .AddChoice($"{Resource.Trap}", "16")
                    .AddChoice($"{Resource.Filler} + {Resource.Trap}", "17")
                    .AddChoice($"{Resource.Filler} + {Resource.Trap} + {Resource.Useful}", "21")
                    .AddChoice($"{Resource.Filler} + {Resource.Trap} + {Resource.Useful} + {Resource.Required}", "27")
                    .AddChoice($"{Resource.Filler} + {Resource.Trap} + {Resource.Useful} + {Resource.Required} + {Resource.Progression}", "31")),

            new SlashCommandBuilder()
                .WithName("delete-alias")
                .WithDescription(Resource.SCDeleteAliasDescription)
                .AddOption(AliasOption("added-alias")),

            new SlashCommandBuilder().WithName("status-games-list").WithDescription(Resource.SCStatusGameListDescription),

             new SlashCommandBuilder()
                .WithName("recap-all").WithDescription(Resource.SCRecapAllDescription),

            new SlashCommandBuilder()
                .WithName("recap")
                .WithDescription(Resource.SCRecapDescription)
                .AddOption(UserOption("user", "Discord user linked to a player alias")),

            new SlashCommandBuilder()
                .WithName("recap-and-clean")
                .WithDescription(Resource.RCRecapAndCleanDescription)
                .AddOption(UserOption("user", "Discord user linked to a player alias")),

            new SlashCommandBuilder()
                .WithName("clean")
                .WithDescription(Resource.SCCleanDescription)
                .AddOption(UserOption("user", "Discord user linked to a player alias")),

            new SlashCommandBuilder().WithName("clean-all").WithDescription(Resource.SCCleanAllDescription),

            new SlashCommandBuilder()
                .WithName("hint")
                .WithDescription("Run !hint directly on the Archipelago server as a specific slot.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("slot-name")
                    .WithDescription("Slot/player name from the yaml file")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("item-name")
                    .WithDescription("Item name to hint")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)),

            new SlashCommandBuilder()
                .WithName("status")
                .WithDescription("Show the current Archipelago world status (unlockables and connections)."),

            new SlashCommandBuilder()
                .WithName("players")
                .WithDescription("Show users currently online in the Archipelago room."),

            new SlashCommandBuilder()
                .WithName("list-items")
                .WithDescription(Resource.SCListItemDescription)
                .AddOption(UserOption("user", "Discord user linked to a player alias")),

            new SlashCommandBuilder()
                .WithName("analyze-spoiler-log")
                .WithDescription("Analyse les sphères bloquantes et les dépendances du spoiler log")
                .AddOption(AliasOption("alias"))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("sphere")
                    .WithDescription("Sphère maximale à analyser (optionnel)")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("missing-mode")
                    .WithDescription("first = première sphère bloquante, full = toutes les checks manquantes")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(false)
                    .AddChoice("lowest-sphere-only", "first")
                    .AddChoice("full", "full"))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("hide-items")
                    .WithDescription("Masquer le nom des items dans le rapport")
                    .WithType(ApplicationCommandOptionType.Boolean)
                    .WithRequired(false)),

            new SlashCommandBuilder()
                .WithName("send-spoiler-log")
                .WithDescription("Upload le spoiler log pour l'analyse")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("file")
                    .WithDescription("Fichier spoiler log (.txt/.json)")
                    .WithType(ApplicationCommandOptionType.Attachment)
                    .WithRequired(true)),

            new SlashCommandBuilder()
                .WithName("apworlds-info")
                .WithDescription(Resource.SCApworldInfoDescription),

            new SlashCommandBuilder()
                .WithName("discord")
                .WithDescription(Resource.DiscordDesc),

            new SlashCommandBuilder()
                .WithName("excluded-item")
                .WithDescription(Resource.SCExcludedItemDesc)
                .AddOption(AliasOption("added-alias"))
                .AddOption(ItemsOption("items")),

             new SlashCommandBuilder()
                .WithName("excluded-item-list")
                .WithDescription(Resource.SCExcludedItemListDesc),

            new SlashCommandBuilder()
                .WithName("delete-excluded-item")
                .WithDescription(Resource.SCDeleteExcludedItemDesc)
                .AddOption(AliasOption("added-alias"))
                .AddOption(ItemsOption("delete-items")),

        };

        if (Declare.IsArchipelagoMode)
        {
            commands.AddRange(new[]
            {
                new SlashCommandBuilder().WithName("list-yamls").WithDescription(Resource.SCListYamlsDescription),
                new SlashCommandBuilder().WithName("list-apworld").WithDescription(Resource.SCListApworldDescription),

                new SlashCommandBuilder()
                    .WithName("create-world")
                    .WithDescription("Create a dedicated world thread with a clean slate for uploads/generation/hosting.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("world-name")
                        .WithDescription("Optional world thread name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("host-world")
                    .WithDescription("(Legacy alias) Start the world linked to this thread."),

                new SlashCommandBuilder()
                    .WithName("start-world")
                    .WithDescription("Start the world linked to this thread.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("port")
                        .WithDescription("Optional Archipelago port override (default is the Archipelago server default port)")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("external-domain")
                        .WithDescription("Optional external domain override")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("enable-server-log")
                        .WithDescription("Enable server output logging to this thread")
                        .WithType(ApplicationCommandOptionType.Boolean)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("run-server-command")
                    .WithDescription("Admin only: run a direct command on the live Archipelago server process.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("server-command")
                        .WithDescription("Raw server console command to execute")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("send-patch")
                    .WithDescription("Send a generated patch file from the world zip by slot name.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("slot-name")
                        .WithDescription("Slot/player name from the generated patch filename")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),

                new SlashCommandBuilder().WithName("stop-host-world").WithDescription("Stop the local Archipelago server started by the bot."),

                new SlashCommandBuilder().WithName("backup-yamls").WithDescription(Resource.SCBackupYamlDescription),
                new SlashCommandBuilder().WithName("backup-apworld").WithDescription(Resource.SCBackupApworldDescription),

                new SlashCommandBuilder()
                    .WithName("download-template")
                    .WithDescription(Resource.SCDownloadYamlTemplateDescription)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("template")
                        .WithDescription(Resource.SCTemplateDescription)
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)
                        .WithAutocomplete(true)),

                new SlashCommandBuilder()
                    .WithName("delete-yaml")
                    .WithDescription(Resource.SCDeleteYamlDescription)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("yamlfile")
                        .WithDescription(Resource.SCDeleteYamlChooseDescription)
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)
                        .WithAutocomplete(true)),

                new SlashCommandBuilder().WithName("clean-yamls").WithDescription(Resource.SCCleanYamlDescription),

                new SlashCommandBuilder()
                    .WithName("send-yaml")
                    .WithDescription(Resource.SCSendYamlDescription)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("file")
                        .WithDescription(Resource.SCSendYamlChooseDescription)
                        .WithType(ApplicationCommandOptionType.Attachment)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("user")
                        .WithDescription("Discord user linked to this YAML player config")
                        .WithType(ApplicationCommandOptionType.User)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("generate-with-zip")
                    .WithDescription(Resource.SCGenerateWithZipDescription)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("file")
                        .WithDescription(Resource.SCGenerateWithZipChooseDescription)
                        .WithType(ApplicationCommandOptionType.Attachment)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("skip-prog-balancing")
                        .WithDescription("skip-prog-balancing")
                        .WithType(ApplicationCommandOptionType.Boolean)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("send-apworld")
                    .WithDescription(Resource.SCSendApworldDescription)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("file")
                        .WithDescription(Resource.SCSendApworldChooseDescription)
                        .WithType(ApplicationCommandOptionType.Attachment)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("generate")
                    .WithDescription(Resource.SCGenerateDescription)
                    .AddOption(new SlashCommandOptionBuilder()
                            .WithName("skip-prog-balancing")
                            .WithDescription("skip-prog-balancing")
                            .WithType(ApplicationCommandOptionType.Boolean)
                        .WithRequired(true)),

                new SlashCommandBuilder().WithName("test-generate").WithDescription(Resource.SCTestGenerateDescription)
            });
        }
        return commands;
    }

    #region Helper Methods

    private static SlashCommandOptionBuilder AliasOption(string name)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(Resource.SCChooseAnAlias)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true);
    }

    private static SlashCommandOptionBuilder BooleanOption(string name, string description)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(true);
    }

    private static SlashCommandOptionBuilder UserOption(string name, string description)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.User)
            .WithRequired(true);
    }

    private static SlashCommandOptionBuilder ItemsOption(string item)
    {
        return new SlashCommandOptionBuilder()
            .WithName(item)
            .WithDescription(Resource.SCChooseAnItem)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true);
    }

    #endregion
}
