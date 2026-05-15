# ArchipelagoSphereTracker
Archipelago-Bot is a Discord bot to **host and track Archipelago rooms** and automate thread/channel operations.

- Multi-server, multi-channel, multi-thread support
- Game Generation
- Sending patches directly to players
- Hinting items
- Checking who is currently online (/players)


## Overview

This is a major "rewrite" of the original Archipelago Sphere Tracker which moves the focus from tracking an Archipelago(.gg) game to hosting and managing the server directly from discord.

``` Self-Hosted Qwen was used to change a lot of the functionality from the original since I needed in the day we were starting the sync. My previous solution was to use https://seashells.io/ on the terminal I was running the server on but that was very unrealiable and seemed to only update every 2 lines. I for many reasons comdemn using large LLMs and supporting large datacenters for things that can be done with my own consumer-grade GPU. Similarly, this was made (more like adapted) because I didn't want to hog resources from Archipelago.gg and I needed a reliable way for my players to track progress```

---


## Quick install (release)

### 0) Create a bot/application in your Discord Developer Portal

### 1) Download and Extract

From releases:

- Windows x64: `ast-win-x64-vX.X.X.zip`
- Linux x64: `ast-linux-x64-vX.X.X.tar.gz`
- Extract to a dedicated folder.

### 2) Configure

Add a `.env` file in the same folder as the executable. (Check .env configuration in the next section.)

### 3) Start from a terminal or command prompt

- Windows: `ArchipelagoSphereTracker.exe --ArchipelagoMode`
- Linux: `./ArchipelagoSphereTracker --ArchipelagoMode`

### 4) Create a Thread and Generate your world!

- Run `/create-world` on a (command) channel, this will create a thread and a folder in the bot structure for the player YAMLs
- Send (or tell the other players) to send their YAMLs (and appworlds if needed) using the `/send-yaml` (& `/send-appworld`)
- Run `/generate` and consequently `/start-world`
- If patches are needed, players can get them by running `/send-patch` with the slot name. 

**Note that if the slot name is too big it will get trimmed. Maybe check with /players if unsure.**

**Additionally, sometimes appworlds are too big in size to be sent to the bot. This is a discord limitation and currently the workaround is to ask the bot hoster to download the appworld unfortunately. A similar problem occurs with games that need a rom. Workaround has been for the host to generate the world in their machine, put it in the thread folder and run /start-world.**


---

## `.env` configuration

Recognized variables:

```dotenv
# Required
DISCORD_TOKEN=YOUR_DISCORD_BOT_TOKEN

# Optional (default: 5199)
WEB_PORT=5199

# Optional (default external URL for start-world; can include a port like world.example.com:1111)
WEB_BASE_URL=https://world.example.com:1111
```

### Important notes

- `DISCORD_TOKEN` is required for bot login.
- `WEB_PORT` defines the exposed port for the archipelago server (`0.0.0.0:<port>`).

**Note that if hosting multiple servers, the port must be different for each**
- `WEB_BASE_URL` is used by default as the external URL for `/start-world`, for example `hostname.com`.

---

## Required Discord permissions

Recommended permission integer:

```text
395137117248
```

Permissions included:

- View channels
- Send messages
- Create public threads
- Create private threads
- Send messages in threads
- Manage messages
- Manage threads
- Embed links
- Attach files
- Add reactions
- Read message history
- Use Slash Commands

---

## Slash commands

- `/list-yamls`
- `/list-apworld`
- `/create-world`
- `/generate`
- `/generate-with-zip`
- `/start-world`
- `/send-patch`
- `/stop-host-world`
- `/download-template`
- `/send-yaml`
- `/send-apworld`
- `/delete-yaml`
- `/clean-yamls`
- `/backup-yamls`
- `/backup-apworld`
- `/test-generate`

---

## Build from source

```bash
# 1) Clone
git clone github.com/LuigiARC/Archipelago-Bot.git
cd Archipelago-Bot

# 2) Configure .env
cp .env.example .env 2>/dev/null || true
# then edit .env

# 3) Restore / build
dotnet restore
dotnet build --configuration Release

# 4) Publish Windows x64
dotnet publish ArchipelagoSphereTracker.csproj -c Release -r win-x64 /p:SelfContained=true /p:PublishSingleFile=true /p:PublishTrimmed=false /p:IncludeAllContentForSelfExtract=true /p:Version=X.X.X

# 5) Publish Linux x64
dotnet publish ArchipelagoSphereTracker.csproj -c Release -r linux-x64 /p:SelfContained=true /p:PublishSingleFile=true /p:PublishTrimmed=false /p:IncludeAllContentForSelfExtract=true /p:Version=X.X.X
```

Expected output binaries:

- Windows: `bin/Release/net8.0/win-x64/publish/ArchipelagoSphereTracker.exe`
- Linux: `bin/Release/net8.0/linux-x64/publish/ArchipelagoSphereTracker`

---

## Project structure

```text
src/
  Bot/                # Discord commands and core bot logic
  SqlCommands/        # SQLite access + migrations
  TrackerLib/         # stream parser, datapackage, models
  Install/            # Archipelago setup/backup flows
apworld/
  # apworld assets/templates
Install/
  # distributed install scripts
```

---

## Storage and data

- Local SQLite database: `AST.db`
- The bot stores channels, aliases, game status, hints, recap data, etc.
- DB migrations are applied automatically at startup when needed.

---

## Troubleshooting

### Bot does not start

- Check `DISCORD_TOKEN` in `.env`.

### Slash commands do not appear

- Check OAuth2/bot permissions on the Discord server.
- Confirm the bot is online and connected.
- Wait a short time after inviting the bot (Discord propagation).

### Multiworld generation errors

- Verify required files exist (`yaml`, `apworld`, ROMs when needed).

---

## FAQ

### Which games are supported?

All games supported by Archipelago MultiWorld are potentially usable together in multiworld.

### Can I host the bot on a VPS?

Yes. Linux x64 is a common deployment target. Prefer systemd + reverse proxy when portal is enabled.

---

## License

This project is distributed under the MIT License. See [LICENSE](LICENSE).
