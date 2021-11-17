WriteLine("Gate Open: START");

var configFolder = new DirectoryInfo(
    GetEnvironmentVariable("config_folder_path")
    ?? "./Configurations");

string key = GetEnvironmentVariable("credentials_token") 
    ?? File.ReadAllText("./Configurations/bot.credentials");

BotConfig config = JsonSerializer.Deserialize<BotConfig>(
    File.ReadAllText(
        Path.Combine(configFolder.FullName, "config.json")),
    TensorJson.Default.BotConfig)!;

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

foreach (var followConfig in (config.Followers ?? Enumerable.Empty<RoleFollowConfig>()))
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
    DateTimeOffset start = DateTimeOffset.UtcNow;
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

            WriteLine($"From: {sourceGuild.Name} @{sourceRole.Name} -- To: {followGuild.Name} {followRole.Name}");

            var sourceRestrictedChannel = await sourceGuild.GetTextChannelAsync(subscription.SourceRestrictedChannelId);
            Trace("Scanning #{0} in {1} for additions in {2}", sourceRestrictedChannel.Name, sourceGuild.Name, followGuild.Name);
            await foreach (var page in sourceRestrictedChannel.GetUsersAsync())
            {
                foreach (var sourceUser in page)
                {
                    Trace("Checking user {0} ({1})", sourceUser.Id, sourceUser.Nickname ?? sourceUser.Username);
                    if (ContainsRoleId(sourceUser.RoleIds, subscription.SourceRoleId))
                    {
                        var followUser = await followGuild.GetUserAsync(sourceUser.Id);
                        if (followUser is not null)
                        {
                            Trace("Found as {0} in {1}", followUser.Nickname ?? followUser.Username, followGuild.Name);
                            if (!ContainsRoleId(followUser.RoleIds, subscription.FollowingRoleId))
                                await TryAndIgnoreError(() =>
                                {
                                    WriteLine("Adding @{0} in {1}", followRole.Name, followGuild.Name);
                                    return followUser.AddRoleAsync(followRole);
                                });
                        }
                    }
                }
                Trace("Delay");
                await ExtraPaginationDelay();
            }
            Trace("Completed additions");

            var followRestrictedChannel = await followGuild.GetTextChannelAsync(subscription.FollowingRestrictedChannelId);
            Trace("Scanning #{0} in {1} for removals from {2}", followRestrictedChannel.Name, followGuild.Name, sourceGuild.Name);
            await foreach (var page in followRestrictedChannel.GetUsersAsync())
            {
                var userArray = page.ToArray();
                foreach (var followUser in userArray)
                {
                    Trace("Checking user {0} ({1})", followUser.Id, followUser.Nickname ?? followUser.Username);
                    if (ContainsRoleId(followUser.RoleIds, followRole.Id))
                    {
                        var sourceUser = await sourceGuild.GetUserAsync(followUser.Id);
                        if (sourceUser is not null)
                        {
                            Trace("Found as {0} in {1}", sourceUser.Nickname ?? sourceUser.Username, sourceGuild.Name);
                            if (!ContainsRoleId(sourceUser.RoleIds, sourceRole.Id))
                                await TryAndIgnoreError(() =>
                                {
                                    Trace("Removing @{0} in {1}", followRole.Name, followGuild.Name);
                                    return followUser.RemoveRoleAsync(followRole);
                                });
                        }
                    }
                }
                Trace("Delay");
                await ExtraPaginationDelay();
            }

            Trace("Completed removals");
        }
    }
    Trace($"Background update completed in {DateTimeOffset.UtcNow - start}");
}

bool ContainsRoleId(IReadOnlyCollection<ulong> roles, ulong roleId)
{
    if (roles is null)
        return false;
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

void Trace(string messageTemplate, params object[] args)
{
    if (config.Trace)
        WriteLine($"[{DateTimeOffset.UtcNow}] " + string.Format(messageTemplate, args));
}

// Pads out the delay between user page requests to stay way clear of any rate limits
Task ExtraPaginationDelay() => Task.Delay(TimeSpan.FromMilliseconds(config.UserPaginationDelayMs));

internal class BotConfig
{ 
    public ulong BotId { get; set; }
    public int UserPaginationDelayMs { get; set; }
    public int BackgroundSyncDelayMinutes { get; set; }
    public bool Trace { get; set; } = false;
    public RoleFollowConfig[]? Followers { get; set; }
}

internal class RoleFollowConfig
{
    public ulong SourceServerId { get; set; }
    public ulong SourceRoleId { get; set; }
    public ulong SourceRestrictedChannelId { get; set; }
    public ulong FollowingServerId { get; set; }
    public ulong FollowingRoleId { get; set; }
    public ulong FollowingRestrictedChannelId { get; set; }
}

[JsonSerializable(typeof(BotConfig))]
[JsonSerializable(typeof(RoleFollowConfig))]
internal partial class TensorJson : JsonSerializerContext { }