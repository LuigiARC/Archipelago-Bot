using System.Data.SQLite;

public static class WorldThreadsCommands
{
    private static readonly SemaphoreSlim EnableServerLogSchemaGate = new(1, 1);
    private static bool _enableServerLogSchemaReady;

    public static async Task RegisterWorldThreadAsync(string guildId, string channelId, string? parentChannelId, string? worldName)
    {
        await Db.WriteAsync(async conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO WorldThreadsTable
                (GuildId, ChannelId, ParentChannelId, WorldName, CreatedAtUtc, LastStartedAtUtc)
                VALUES
                (@GuildId, @ChannelId, @ParentChannelId, @WorldName, @CreatedAtUtc, COALESCE((
                    SELECT LastStartedAtUtc
                    FROM WorldThreadsTable
                    WHERE GuildId = @GuildId AND ChannelId = @ChannelId
                ), NULL));";
            command.Parameters.AddWithValue("@GuildId", guildId);
            command.Parameters.AddWithValue("@ChannelId", channelId);
            command.Parameters.AddWithValue("@ParentChannelId", parentChannelId ?? string.Empty);
            command.Parameters.AddWithValue("@WorldName", worldName ?? string.Empty);
            command.Parameters.AddWithValue("@CreatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        });
    }

    public static async Task<bool> IsWorldThreadAsync(string guildId, string channelId)
    {
        await using var connection = await Db.OpenReadAsync();
        using var command = new SQLiteCommand(@"
            SELECT 1
            FROM WorldThreadsTable
            WHERE GuildId = @GuildId AND ChannelId = @ChannelId
            LIMIT 1;", connection);
        command.Parameters.AddWithValue("@GuildId", guildId);
        command.Parameters.AddWithValue("@ChannelId", channelId);

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result != null;
    }

    public static async Task MarkWorldStartedAsync(string guildId, string channelId)
    {
        await Db.WriteAsync(async conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
                UPDATE WorldThreadsTable
                SET LastStartedAtUtc = @LastStartedAtUtc
                WHERE GuildId = @GuildId AND ChannelId = @ChannelId;";
            command.Parameters.AddWithValue("@GuildId", guildId);
            command.Parameters.AddWithValue("@ChannelId", channelId);
            command.Parameters.AddWithValue("@LastStartedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        });
    }

    public static async Task<bool> GetServerLogEnabledAsync(string guildId, string channelId)
    {
        await EnsureEnableServerLogColumnAsync().ConfigureAwait(false);

        await using var connection = await Db.OpenReadAsync();
        using var command = new SQLiteCommand(@"
            SELECT COALESCE(EnableServerLog, 0)
            FROM WorldThreadsTable
            WHERE GuildId = @GuildId AND ChannelId = @ChannelId
            LIMIT 1;", connection);
        command.Parameters.AddWithValue("@GuildId", guildId);
        command.Parameters.AddWithValue("@ChannelId", channelId);

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result != null && (long)result == 1;
    }

    public static async Task SetServerLogEnabledAsync(string guildId, string channelId, bool enabled)
    {
        await EnsureEnableServerLogColumnAsync().ConfigureAwait(false);

        await Db.WriteAsync(async conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
                UPDATE WorldThreadsTable
                SET EnableServerLog = @EnableServerLog
                WHERE GuildId = @GuildId AND ChannelId = @ChannelId;";
            command.Parameters.AddWithValue("@GuildId", guildId);
            command.Parameters.AddWithValue("@ChannelId", channelId);
            command.Parameters.AddWithValue("@EnableServerLog", enabled ? 1 : 0);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        });
    }

    private static async Task EnsureEnableServerLogColumnAsync()
    {
        if (_enableServerLogSchemaReady)
        {
            return;
        }

        await EnableServerLogSchemaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_enableServerLogSchemaReady)
            {
                return;
            }

            await Db.WriteAsync(async conn =>
            {
                using var command = conn.CreateCommand();
                command.CommandText = @"
                    ALTER TABLE WorldThreadsTable
                    ADD COLUMN EnableServerLog INTEGER DEFAULT 0;";

                try
                {
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    Console.WriteLine("Added missing EnableServerLog column to WorldThreadsTable.");
                }
                catch (SQLiteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                    // Column already exists; this is expected on healthy databases.
                }
            }).ConfigureAwait(false);

            _enableServerLogSchemaReady = true;
        }
        finally
        {
            EnableServerLogSchemaGate.Release();
        }
    }
}

