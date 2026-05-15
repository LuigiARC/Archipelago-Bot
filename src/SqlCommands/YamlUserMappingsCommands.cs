using System.Data.SQLite;

public static class YamlUserMappingsCommands
{
    public sealed record YamlUserMapping(string YamlFile, string Alias, string UserId);

    public static async Task AddOrUpdateMappingAsync(string guildId, string channelId, string yamlFile, string alias, string userId)
    {
        await Db.WriteAsync(async conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO YamlUserMappingTable (GuildId, ChannelId, YamlFile, Alias, UserId)
                VALUES (@GuildId, @ChannelId, @YamlFile, @Alias, @UserId);";
            command.Parameters.AddWithValue("@GuildId", guildId);
            command.Parameters.AddWithValue("@ChannelId", channelId);
            command.Parameters.AddWithValue("@YamlFile", yamlFile);
            command.Parameters.AddWithValue("@Alias", alias);
            command.Parameters.AddWithValue("@UserId", userId);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        });
    }

    public static async Task<List<YamlUserMapping>> GetMappingsAsync(string guildId, string channelId)
    {
        var mappings = new List<YamlUserMapping>();

        await using var connection = await Db.OpenReadAsync();
        using var command = new SQLiteCommand(@"
            SELECT YamlFile, Alias, UserId
            FROM YamlUserMappingTable
            WHERE GuildId = @GuildId AND ChannelId = @ChannelId
            ORDER BY YamlFile;", connection);
        command.Parameters.AddWithValue("@GuildId", guildId);
        command.Parameters.AddWithValue("@ChannelId", channelId);

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var yamlFile = reader["YamlFile"]?.ToString() ?? string.Empty;
            var alias = reader["Alias"]?.ToString() ?? string.Empty;
            var userId = reader["UserId"]?.ToString() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(yamlFile) && !string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(userId))
            {
                mappings.Add(new YamlUserMapping(yamlFile, alias, userId));
            }
        }

        return mappings;
    }

    public static async Task DeleteMappingForYamlAsync(string guildId, string channelId, string yamlFile)
    {
        await Db.WriteAsync(async conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
                DELETE FROM YamlUserMappingTable
                WHERE GuildId = @GuildId AND ChannelId = @ChannelId AND YamlFile = @YamlFile;";
            command.Parameters.AddWithValue("@GuildId", guildId);
            command.Parameters.AddWithValue("@ChannelId", channelId);
            command.Parameters.AddWithValue("@YamlFile", yamlFile);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        });
    }

    public static async Task DeleteMappingsForChannelAsync(string guildId, string channelId)
    {
        await Db.WriteAsync(async conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
                DELETE FROM YamlUserMappingTable
                WHERE GuildId = @GuildId AND ChannelId = @ChannelId;";
            command.Parameters.AddWithValue("@GuildId", guildId);
            command.Parameters.AddWithValue("@ChannelId", channelId);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        });
    }

    public static async Task<int> CopyMappingsAsync(string guildId, string sourceChannelId, string targetChannelId)
    {
        var mappings = await GetMappingsAsync(guildId, sourceChannelId);
        if (mappings.Count == 0)
        {
            return 0;
        }

        var copied = 0;
        foreach (var mapping in mappings)
        {
            await AddOrUpdateMappingAsync(guildId, targetChannelId, mapping.YamlFile, mapping.Alias, mapping.UserId);
            copied++;
        }

        return copied;
    }
}
