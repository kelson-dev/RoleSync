WriteLine("Gate Open: START");

var configFolder = new DirectoryInfo(
    GetEnvironmentVariable("config_folder_path")
    ?? "./Configurations");

string key = GetEnvironmentVariable("credentials_token") 
    ?? File.ReadAllText("./Configurations/bot.credentials");

BotConfig config = JsonSerializer.Deserialize<BotConfig>(
    File.ReadAllText(
        Path.Combine(configFolder.FullName, "config.json")),
    Json.Default.BotConfig)!;

ConcurrentDictionary<ulong, ImmutableList<RoleFollowConfig>> followToConfig = new();
ConcurrentDictionary<ulong, ImmutableList<RoleFollowConfig>> sourceToConfig = new();

DiscordSocketClient client = new (new()
{
    AlwaysDownloadUsers = false,
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildPresences | GatewayIntents.GuildMembers,
    MessageCacheSize = 0,
});
await client.LoginAsync(TokenType.Bot, key);

var guilds = await client.Rest.GetGuildsAsync(false);

foreach (var followConfig in config.Followers)
{
    if (await IsFollowConfigValid(followConfig))
    {
        followToConfig.AddOrUpdate(
            followConfig.FollowingServerId,
            (id) => ImmutableList<RoleFollowConfig>.Empty.Add(followConfig),
            (id, list) => list.Add(followConfig));

        sourceToConfig.AddOrUpdate(
            followConfig.SourceServerId,
            (id) => ImmutableList<RoleFollowConfig>.Empty.Add(followConfig),
            (id, list) => list.Add(followConfig));
    }
}

client.GuildMemberUpdated += HandleUserUpdated;
client.GuildAvailable += (SocketGuild guild) =>
{
    WriteLine($"Guild {guild.Name} {guild.Id} available to socket client");
    //guild.DownloadUsersAsync().ContinueWith(t => WriteLine($"Guild {guild.Name} user caching complete"));
    return Task.CompletedTask;
};

await client.StartAsync();
while (true)
{
    await BackgroundUpdate();

    await Task.Delay(TimeSpan.FromMinutes(config.BackgroundSyncDelayMinutes));
}

async Task HandleUserUpdated(SocketGuildUser from, SocketGuildUser to)
{
    if (to.IsBot || from.Roles.Count == to.Roles.Count)
        return;
    var rolesSet = to.Roles.Select(r => r.Id).ToImmutableSortedSet();
    if (sourceToConfig.TryGetValue(from.Guild.Id, out var configs))
    {
        foreach (var config in configs)
        {
            var followingGuild = client.GetGuild(config.FollowingServerId);
            if (followingGuild == null)
                continue;
            var followingRole = followingGuild.GetRole(config.FollowingRoleId);
            if (followingRole == null)
                continue;
            var followingUser = followingGuild.GetUser(to.Id);
            bool sourceHasRole = rolesSet.Contains(config.SourceRoleId);
            bool followerHasRole = followingUser.Roles.Contains(followingRole);

            if (sourceHasRole && !followerHasRole)
                await TryAndIgnoreError(() => followingUser.AddRoleAsync(followingRole));
            else if (!sourceHasRole && followerHasRole)
                await TryAndIgnoreError(() =>  followingUser.RemoveRoleAsync(followingRole));
        }
    }
}

async Task BackgroundUpdate()
{
#if RELEASE
    await Task.Delay(TimeSpan.FromSeconds(30)); // minimum delay in release builds
#endif
    WriteLine($"Background sync check [{DateTimeOffset.UtcNow}]");
    foreach (var configs in followToConfig.Values)
    {
        if (configs.Count == 0)
            continue;
        var followGuild = await client.Rest.GetGuildAsync(configs[0].FollowingServerId);
        if (followGuild == null)
            continue;

        foreach (var subscription in configs)
        {
            var followRole = followGuild.GetRole(configs[0].FollowingRoleId);
            if (followRole == null)
                continue;

            var sourceGuild = await client.Rest.GetGuildAsync(subscription.SourceServerId);
            if (sourceGuild == null)
                continue;
            var sourceRole = sourceGuild.GetRole(subscription.SourceRoleId);
            if (sourceRole == null)
                continue;

            var sourceRestrictedChannel = await sourceGuild.GetTextChannelAsync(subscription.SourceRestrictedChannelId);
            await foreach (var page in sourceRestrictedChannel.GetUsersAsync())
            {
                foreach (var sourceUser in page)
                {
                    if (ContainsRoleId(sourceUser.RoleIds, subscription.SourceRoleId))
                    {
                        var followUser = await followGuild.GetUserAsync(sourceUser.Id);

                        if (!ContainsRoleId(followUser.RoleIds, subscription.FollowingRoleId))
                        {
                            await TryAndIgnoreError(() => followUser.AddRoleAsync(followRole));
                        }
                    }
                }
                await ExtraPaginationDelay();
            }

            var followRestrictedChannel = await followGuild.GetTextChannelAsync(subscription.FollowingRestrictedChannelId);
            await foreach (var page in followRestrictedChannel.GetUsersAsync())
            {
                var userArray = page.ToArray();
                foreach (var followUser in userArray)
                {
                    if (ContainsRoleId(followUser.RoleIds, followRole.Id))
                    {
                        var sourceUser = await sourceGuild.GetUserAsync(followUser.Id);
                        if (!ContainsRoleId(sourceUser.RoleIds, sourceRole.Id))
                            await TryAndIgnoreError(() => followUser.RemoveRoleAsync(followRole));
                    }
                }
                await ExtraPaginationDelay();
            }
        }
    }
}

bool ContainsRoleId(IReadOnlyCollection<ulong> roles, ulong roleId)
{
    foreach (var role in roles)
        if (role == roleId)
            return true;
    return false;
}

async Task<bool> IsFollowConfigValid(RoleFollowConfig sub)
{
    var sourceGuild = await client.Rest.GetGuildAsync(sub.SourceServerId);
    if (sourceGuild == null)
    {
        WriteLine($"Source Guild {sub.SourceServerId} could not be found");
        return false;
    }
    var sourceRole = sourceGuild.GetRole(sub.SourceRoleId);
    if (sourceRole == null)
    {
        WriteLine($"Source Role {sub.SourceRoleId} could not be found");
        return false;
    }

    var followGuild = await client.Rest.GetGuildAsync(sub.FollowingServerId);
    if (followGuild == null)
    {
        WriteLine($"Following Guild {sub.FollowingServerId} could not be found");
        return false;
    }
    var followRole = followGuild.GetRole(sub.FollowingRoleId);
    if (followRole == null)
    {
        WriteLine($"Following Role {sub.FollowingRoleId} could not be found");
        return false;
    }
    return true;
}

async Task TryAndIgnoreError(Func<Task> task)
{
    try
    {
        await task();
    }
    catch (Exception yeet)
    {
        WriteLine(yeet.Message);
    }
}

// Pads out the delay between user page requests to stay way clear of any rate limits
Task ExtraPaginationDelay() => Task.Delay(TimeSpan.FromMilliseconds(config.UserPaginationDelayMs));

internal record BotConfig(
    ulong BotId,
    int UserPaginationDelayMs,
    int BackgroundSyncDelayMinutes,
    RoleFollowConfig[] Followers);

internal record RoleFollowConfig(
    ulong SourceServerId,
    ulong SourceRoleId,
    ulong SourceRestrictedChannelId,
    ulong FollowingServerId,
    ulong FollowingRoleId,
    ulong FollowingRestrictedChannelId);

[JsonSerializable(typeof(BotConfig))]
[JsonSerializable(typeof(RoleFollowConfig))]
internal partial class Json : JsonSerializerContext { }