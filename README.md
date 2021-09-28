[![Deployment](https://github.com/kelson-dev/RoleSync/actions/workflows/dotnet.yml/badge.svg)](https://github.com/kelson-dev/RoleSync/actions/workflows/dotnet.yml)

# RoleSync
A bot that passively synchronizes a role between an source-server and a follow-server

## Configuration

The bot is configured statically using a json file, named `config.json` in the path specified by the `config_folder_path` ENV variable.

The bot configuration specifies the bots ID, an extra pagination delay in milliseconds, a background sync delay in minutes, and an array of RoleFollowConfigs.

Each RoleFollowConfig defines a role in server that defines whether or not users should have another role in that or another server.

The parameters are:
 1. A source server
 1. A source role
 1. A source channel, preferabely one restricted to users with the source role
 1. A following server
 1. A following role
 1. A channel in the following server preferabely restricted to users with the following role

The channels are used to limit the number of users scanned for having the roles or not in each server.

Do not configure "circular references" 


Example configuration:

```json
{
  "BotId": 1,
  "UserPaginationDelayMs": 500,
  "BackgroundSyncDelayMinutes": 30,
  "Followers": [
    {
      "SourceServerId": 2,
      "SourceRoleId": 69,
      "SourceRestrictedChannelId": 1002,
      "FollowingServerId": 3,
      "FollowingRoleId": 420,
      "FollowingRestrictedChannelId": 1003
    },
    {
      "SourceServerId": 3,
      "SourceRoleId": 1337,
      "SourceRestrictedChannelId": 1003,
      "FollowingServerId": 2,
      "FollowingRoleId": 42,
      "FollowingRestrictedChannelId": 1002
    }
  ]
}
```

In this configuration the role with ID '420' in server '3' is synced to the presence of the role '69' in server '2'
And the role '42' in server '2' is synced to the presence of the role '1337' in server '3'

The bot will watch for role change events and update users immediately, and every 30 minutes will scan the users of the restricted channels to see if it missed a user update event.