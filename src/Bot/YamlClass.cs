using ArchipelagoSphereTracker.src.Resources;
using Discord;
using Discord.WebSocket;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text;

public class YamlClass : Declare
{
    public static async Task<string> DownloadTemplate(SocketSlashCommand command)
    {
        var yamlFile = command.Data.Options.FirstOrDefault()?.Value as string;
        var message = string.Empty;

        if (string.IsNullOrEmpty(yamlFile))
        {
            return Resource.NoFileSelected;
        }

        string templatePath = Path.Combine(BasePath, "extern", "Archipelago", "Players", "Templates", yamlFile);

        if (File.Exists(templatePath))
        {
            await command.FollowupWithFileAsync(templatePath, yamlFile);
        }
        else
        {
            message = Resource.YamlFileNotExists;
        }

        return message;
    }

    public static async Task<string> SendYaml(SocketSlashCommand command, string channelId, string guildId)
    {
        var attachment = command.Data.Options.FirstOrDefault(o => o.Name == "file")?.Value as IAttachment;
        var mappedUser = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as IUser;
        var message = string.Empty;
        if (attachment == null || !attachment.Filename.EndsWith(".yaml"))
        {
            return Resource.YamlWrongFile;
        }

        var playersFolderChannel = Path.Combine(BasePath, "extern", "Archipelago", "Players", channelId, "yaml");

        if (!Directory.Exists(playersFolderChannel))
        {
            Directory.CreateDirectory(playersFolderChannel);
        }

        string filePath = Path.Combine(playersFolderChannel, attachment.Filename);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        using (var response = await HttpClient.GetAsync(attachment.Url))
            if (response.IsSuccessStatusCode)
            {
                await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await response.Content.CopyToAsync(fs);
                }

                if (mappedUser != null)
                {
                    var alias = await ExtractAliasFromYamlAsync(filePath, attachment.Filename);
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        await YamlUserMappingsCommands.AddOrUpdateMappingAsync(
                            guildId,
                            channelId,
                            attachment.Filename,
                            alias,
                            mappedUser.Id.ToString());

                        message = string.Format(Resource.YamlFileSent, attachment.Filename)
                                  + $"\nMapped {attachment.Filename} ({alias}) to <@{mappedUser.Id}>.";
                        return message;
                    }
                }

                message = string.Format(Resource.YamlFileSent, attachment.Filename);
            }
            else
            {
                message = Resource.YamlFileDownloadFailed;
            }

        return message;
    }

    public static async Task<string> SendYamlFromStreamAsync(string channelId, string fileName, Stream content)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return Resource.YamlWrongFile;
        }

        var playersFolderChannel = Path.Combine(BasePath, "extern", "Archipelago", "Players", channelId, "yaml");
        Directory.CreateDirectory(playersFolderChannel);

        string filePath = Path.Combine(playersFolderChannel, Path.GetFileName(fileName));

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await content.CopyToAsync(fs);
        }

        return string.Format(Resource.YamlFileSent, Path.GetFileName(fileName));
    }

    public static string CleanYamls(string channelId, string guildId)
    {
        _ = (channelId, guildId);
        return "YAML cleanup is disabled: player YAML files are persistent and are never deleted.";
    }

    public static string DeleteYaml(SocketSlashCommand command, string channelId, string guildId)
    {
        _ = (command, channelId, guildId);
        return "YAML deletion is disabled: player YAML files are persistent and are never deleted.";
    }

    public static string DeleteYamlByName(string channelId, string guildId, string? fileSelected)
    {
        _ = (channelId, guildId, fileSelected);
        return "YAML deletion is disabled: player YAML files are persistent and are never deleted.";
    }

    public static async Task<string> BackupYamls(SocketSlashCommand command, string channelId)
    {
        var playersFolderChannel = Path.Combine(BasePath, "extern", "Archipelago", "Players", channelId, "yaml");
        var message = string.Empty;
        if (Directory.Exists(playersFolderChannel))
        {
            var backupFolder = Path.Combine(BasePath, "extern", "Archipelago", "Players", channelId, "backup");
            if (!Directory.Exists(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
            }

            var zipPath = Path.Combine(backupFolder, $"backup_yaml_{channelId}.zip");

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using (var zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var files = Directory.GetFiles(playersFolderChannel, "*.yaml");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    zipArchive.CreateEntryFromFile(file, fileName);
                }
            }

            await command.FollowupWithFileAsync(zipPath, $"backup_yaml_{channelId}.zip");

            File.Delete(zipPath);
        }
        else
        {
            message += Resource.YamlNoYaml;
        }

        return message;
    }

    public static async Task<string> BackupYamlsToFileAsync(string channelId, string zipPath)
    {
        var playersFolderChannel = Path.Combine(BasePath, "extern", "Archipelago", "Players", channelId, "yaml");
        var message = string.Empty;
        if (Directory.Exists(playersFolderChannel))
        {
            var zipDirectory = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(zipDirectory))
            {
                Directory.CreateDirectory(zipDirectory);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using (var zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var files = Directory.GetFiles(playersFolderChannel, "*.yaml");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    zipArchive.CreateEntryFromFile(file, fileName);
                }
            }
        }
        else
        {
            message += Resource.YamlNoYaml;
        }

        return message;
    }

    public static string DownloadTemplateToFile(string templateName, string destinationPath)
    {
        if (string.IsNullOrEmpty(templateName))
        {
            return Resource.NoFileSelected;
        }

        string templatePath = Path.Combine(BasePath, "extern", "Archipelago", "Players", "Templates", templateName);

        if (File.Exists(templatePath))
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(templatePath, destinationPath, overwrite: true);
            return string.Empty;
        }

        return Resource.YamlFileNotExists;
    }

    public static string ListYamls(string channelId)
    {
        var yamls = GetYamlFileNames(channelId).ToList();

        if (!yamls.Any())
            return Resource.YamlNoYaml;

        var sb = new StringBuilder(Resource.YamlList);
        sb.AppendLine();
        foreach (var yml in yamls)
        {
            sb.AppendLine(yml);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> GetYamlFileNames(string channelId)
    {
        var playersFolderChannel = Path.Combine(BasePath, "extern", "Archipelago", "Players", channelId, "yaml");

        if (!Directory.Exists(playersFolderChannel))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(playersFolderChannel, "*.yaml")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public static int CopyYamlFilesToChannel(string sourceChannelId, string targetChannelId)
    {
        var sourceDir = Path.Combine(BasePath, "extern", "Archipelago", "Players", sourceChannelId, "yaml");
        if (!Directory.Exists(sourceDir))
        {
            return 0;
        }

        var targetDir = Path.Combine(BasePath, "extern", "Archipelago", "Players", targetChannelId, "yaml");
        Directory.CreateDirectory(targetDir);

        var copied = 0;
        foreach (var file in Directory.GetFiles(sourceDir, "*.yaml", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var destination = Path.Combine(targetDir, fileName);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static async Task<string> ExtractAliasFromYamlAsync(string filePath, string fallbackFileName)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var line = raw.Trim();
                if (line.StartsWith("#"))
                {
                    continue;
                }

                var match = Regex.Match(line, "^name\\s*:\\s*(.+)$", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var value = match.Groups[1].Value.Trim().Trim('"', '\'', ' ');
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not extract YAML alias from {filePath}: {ex.Message}");
        }

        return Path.GetFileNameWithoutExtension(fallbackFileName) ?? string.Empty;
    }
}
