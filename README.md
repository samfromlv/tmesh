# TMesh - Telegram ↔ Meshtastic Bridge

<div align="center">

**Secure, bidirectional communication between Meshtastic mesh networks and Telegram groups**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download)

[Features](#features) • [How It Works](#how-it-works) • [Installation](#installation) • [Usage](#usage) • [Configuration](#configuration)

</div>

---

## Overview

TMesh bridges the gap between Meshtastic off-grid mesh networks and Telegram groups, enabling seamless two-way communication. It creates a virtual Meshtastic node that connects via MQTT, encrypts messages using PKI (Public Key Infrastructure), and intelligently manages message delivery to prevent network congestion.

### Key Highlights

- **🔐 End-to-End Encryption**: Uses Meshtastic PKI for secure device-to-device and channel communication
- **📱 Telegram Integration**: Full bot integration with webhook support and message threading
- **🔒 Private Channel Support**: Register entire private Meshtastic channels (not just individual devices)
- **⚡ Smart Queue Management**: Rate-limiting and prioritization prevent mesh network overload
- **📊 Delivery Tracking**: Real-time message status updates via Telegram reactions
- **🔄 Bidirectional Sync**: Messages flow seamlessly in both directions with reply support
- **🌐 Multi-Gateway Support**: Intelligent routing across multiple MQTT gateways
- **📍 Position Tracking**: Track and display device locations on maps
- **📈 Analytics Collection**: Optional PostgreSQL database for network telemetry and metrics
- **🐳 Docker Ready**: Easy deployment with Docker containers
- **🔒 TLS/SSL Support**: Secure MQTT connections with certificate validation

---

## Features

### Communication Features
- **Message Threading**: Reply to Telegram messages in Meshtastic app and vice versa
- **Private Channel Registration**: Register entire Meshtastic private channels to Telegram groups
- **Device Registration**: Register individual Meshtastic devices to Telegram groups
- **Multiple Channels**: Support for primary and secondary Meshtastic channels
- **Position Sharing**: Automatic position tracking with `/position` command and map display
- **Ping/Pong Handling**: Configurable ping responses via direct message or public channel
- **Customizable Messages**: Configure ping replies and unregistered device responses
- **Text Message Delivery**: Bidirectional text message forwarding with sender identification

### Network Management
- **Multi-Gateway Routing**: Intelligent message routing across multiple MQTT gateways
- **Dynamic Hop Limits**: Automatically adjusts hop count based on last seen gateway
- **Gateway Health Monitoring**: HTTP endpoints for monitoring gateway and bot health
- **TLS/SSL MQTT**: Secure MQTT connections with optional certificate validation
- **Gateway Bridging**: Bridge direct messages between gateways for admin commands
- **Trace Route Support**: Display mesh network routes with SNR information

### Device & Channel Management
- **Quick Device Registration**: Add devices with `/add_device !deviceid` or interactive flow
- **Quick Channel Registration**: Add private channels with `/add_channel` command
- **Device Removal**: Remove devices with `/remove_device !deviceid` or from all chats
- **Channel Removal**: Remove channels with `/remove_channel <channelId>` or from all chats
- **Public Key Pinning**: Prevents MITM attacks by pinning device keys on registration
- **Device Discovery**: Automatic device discovery via MQTT node info broadcasts
- **Position Tracking**: Track device locations with accuracy and timestamp
- **Filter Support**: Filter device lists by name in `/status` and `/position` commands

### Analytics & Telemetry
- **Optional Analytics Database**: PostgreSQL database for collecting network metrics
- **Device Metrics**: Tracks device positions, channel utilization, and air utilization
- **Time-Series Data**: Historical telemetry data with timestamp-based querying
- **Privacy-Focused**: Analytics is optional and can be disabled
- **Performance Insights**: Monitor mesh network health and device connectivity patterns

### Security & Administration
- **Admin Mode**: Special `/admin` commands for announcements and debugging
- **Password Protection**: Admin commands require password authentication
- **MQTT Password Derivation**: Automatic password generation for MQTT users
- **Key Verification**: 6-digit verification codes sent through mesh network
- **Rate Limiting**: Configurable message rate limits with code attempt restrictions
- **TLS Support**: Encrypted MQTT connections with certificate validation

### Delivery Status & Tracking
- Real-time delivery status with emoji reactions:
  - ✍️ Message created
  - 👀 Queued for delivery
  - 🕊️ Acknowledged by mesh network
  - 👌 Delivered to target device
  - 👎 Delivery failed
  - 🤷 Unknown status (no ACK after 2 minutes)
- Multi-device and multi-channel status tracking
- Queue delay estimation
- Reply message correlation

---

## How It Works

### Architecture

TMesh consists of two main components:

1. **TBot** (Main Service)
   - Manages Telegram bot operations
   - Handles MQTT connectivity to Meshtastic network with TLS support
   - Processes message encryption/decryption with PKI
   - Maintains SQLite database for registrations, device info, channels, and positions
   - Optional PostgreSQL analytics database for telemetry collection
   - Implements message queue and rate limiting
   - Manages multi-gateway routing and health monitoring
   - Handles admin commands and announcements
   - Supports both individual device and private channel registrations

2. **TProxy** (Webhook Proxy)
   - Receives Telegram webhook updates
   - Publishes updates to MQTT for TBot consumption
   - Validates webhook security tokens
   - Provides health monitoring endpoints for gateway status

```
┌─────────────┐          ┌──────────┐          ┌─────────────┐
│  Telegram   │◄────────►│  TProxy  │          │   TBot      │
│   Users     │          │(Webhook) │          │(Main Logic) │
└─────────────┘          └──────────┘          └─────────────┘
                               │                      │
                               │                      │
                               └──────────┬──────────┘
                                         │
                                    ┌────▼────┐
                                    │  MQTT   │
                                    │ Broker  │
                                    │(TLS/SSL)│
                                    └────┬────┘
                                         │
                    ┌────────────────────┼────────────────────┐
                    │                    │                    │
               ┌────▼────┐         ┌────▼────┐         ┌────▼────┐
               │Gateway 1│         │Gateway 2│         │Gateway 3│
               └────┬────┘         └────┬────┘         └────┬────┘
                    │                    │                    │
                    └────────────────────┼────────────────────┘
                                         │
                               ┌─────────▼─────────┐
                               │   Meshtastic      │
                               │   Mesh Network    │
                               │  (Virtual Node)   │
                               └───────────────────┘
                                         │
                               ┌─────────▼─────────┐
                               │ Optional          │
                               │ PostgreSQL        │
                               │ Analytics DB      │
                               └───────────────────┘
```

### Communication Flow

**Telegram → Meshtastic (Device or Channel):**
1. User sends message in Telegram group (optionally replying to previous message)
2. Telegram sends webhook to TProxy
3. TProxy publishes to MQTT status topic
4. TBot receives message, looks up registered devices and channels
5. Message queued with rate limiting and priority
6. If replying: correlates original Meshtastic message for reply chain
7. Selects optimal gateway based on device's/channel's last seen location
8. Calculates dynamic hop limit based on distance to gateway
9. **For devices**: Message encrypted with device's pinned public key (PKI)
10. **For channels**: Message encrypted with channel key
11. Encrypted packet sent to MQTT → selected gateway → Meshtastic mesh
12. Delivery status updates sent back to Telegram as reactions
13. If no ACK after 2 minutes: status changes to 🤷 (unknown)
14. **Optional**: Device metrics (position, channel util, air util) recorded in analytics database

**Meshtastic → Telegram:**
1. Device sends encrypted message to virtual node via mesh or channel
2. Gateway receives and forwards to MQTT broker
3. TBot decrypts using private key (device) or channel key
4. Checks device/channel registration and validates pinned public key (for devices)
5. Updates device position if included in message
6. **Optional**: Records telemetry data in analytics database
7. If replying to message: preserves reply chain in Telegram
8. Sends formatted message to registered Telegram chats
9. Sends ACK back to Meshtastic device through optimal gateway

### Private Channel Support

TMesh now supports registering entire private Meshtastic channels (not just well-known public channels):

**How it works:**
- Register a private channel using `/add_channel` command
- Provide the channel name and encryption key (PSK)
- All devices using that private channel can communicate with the Telegram group
- Multiple devices on the channel share the same Telegram conversation
- Channel messages are encrypted with the channel's PSK
- Public (well-known) channels cannot be registered to prevent spam

**Benefits:**
- Group communication: Multiple Meshtastic devices communicate as a team with Telegram users
- Simplified management: Register once instead of per-device
- Team coordination: Perfect for search and rescue, events, or group activities
- Privacy: Private channels keep your communications separate from public mesh

**Limitations:**
- Public/well-known channels (like LongFast) cannot be registered
- Channel registration requires knowing the exact channel name and PSK
- All messages to the channel are broadcast to all devices on that channel

### Security Model

- **PKI Encryption for Devices**: Each Meshtastic device has a unique X25519 key pair
- **Channel Encryption**: Private channels use AES-128 or AES-256 encryption with PSK
- **Key Pinning**: Device public keys are pinned on first registration to prevent MITM attacks
- **End-to-End**: All messages encrypted end-to-end using elliptic curve (devices) or AES (channels)
- **Device Verification**: One-time codes sent through mesh network for device registration
- **Channel Verification**: Channel key validation during channel registration
- **Webhook Security**: Telegram requests validated with secret tokens
- **TLS/SSL Support**: Optional encrypted MQTT connections with certificate validation
- **Admin Authentication**: Password-protected admin commands
- **Rate Limiting**: Prevents abuse with configurable message and verification limits
- **OK to MQTT**: Only devices with this flag enabled are accessible

### Analytics & Telemetry

**Optional Feature**: TMesh can collect network telemetry data in a PostgreSQL database for analysis and monitoring.

**What is collected (if enabled):**
- Device ID and timestamp
- Device position (latitude, longitude, accuracy)
- Last position update time
- Channel utilization percentage
- Air utilization percentage

**Data retention:**
- Time-series data stored in PostgreSQL
- Indexed by device ID and timestamp for efficient queries
- No message content is ever stored
- Position history maintained for analysis

**Privacy considerations:**
- Analytics is completely optional (disabled by default)
- No personally identifiable information collected
- No message content stored
- Only technical metrics for network optimization
- See [PRIVACY.md](PRIVACY.md) for full details

**Use cases:**
- Monitor mesh network health and coverage
- Track device connectivity patterns
- Analyze network congestion and channel utilization
- Identify optimal gateway placement
- Debug connectivity issues

### Special Features

#### Multi-Gateway Intelligence
- Tracks which gateway last saw each device or channel
- Routes messages through the gateway with best path to destination
- Dynamic hop limit calculation based on device-gateway relationship
- Gateway health monitoring via HTTP endpoints
- Automatic failover between gateways

#### Message Threading & Replies
- Reply to Telegram messages in Meshtastic app
- Reply to Meshtastic messages in Telegram
- Maintains conversation threading across platforms
- Reply chains preserved in both directions
- Works with both device and channel registrations

#### Position Tracking
- Automatic position capture from Meshtastic broadcasts
- Location history with timestamps and accuracy
- Map display with horizontal accuracy visualization
- `/position` command to view device locations
- Filter by device name: `/position MyDevice`
- Optional storage in analytics database for historical tracking

#### Trace Route Support
- Displays complete message path through mesh network
- Shows Signal-to-Noise Ratio (SNR) between hops
- Resolves node IDs to friendly names
- Helps diagnose mesh network connectivity

#### Ping/Pong System
- Configurable ping words (default: "ping")
- Option to reply via direct message or public channel
- Customizable pong response text
- Helps verify mesh connectivity

#### Admin Commands
Protected by password, accessible via `/admin login <password>` then commands:
- `public_text_primary <message>` - Send announcement to primary channel
- `public_text <channel> <message>` - Send announcement to specific channel
- `text <deviceId> <message>` - Send direct message to any device
- `nodeinfo <deviceId>` - Query device information from database
- `removenode <deviceId>` - Remove device from database
- `logout` - Exit admin mode

---

## Prerequisites

### Infrastructure Requirements

- **MQTT Broker** (e.g., Mosquitto, HiveMQ, EMQX)
  - Must be accessible from both TMesh and your Meshtastic MQTT gateway(s)
  - Support for QoS 1 (at least once delivery)
  - Recommended: TLS/SSL encryption enabled
  - Recommended: Persistent sessions enabled

- **Telegram Bot**
  - Create via [@BotFather](https://t.me/botfather)
  - Note your bot API token and username
  - Bot must be added as admin to target groups

- **Meshtastic Gateway(s)**
  - One or more Meshtastic devices with MQTT enabled
  - "OK to MQTT" setting enabled in LoRa configuration
  - Connected to the same MQTT broker
  - Primary channel must match TMesh configuration
  - For multi-gateway: Note device IDs of gateway nodes

- **Public Webhook Endpoint** (for TProxy)
  - HTTPS endpoint accessible by Telegram servers
  - Can use: ngrok, Cloudflare Tunnel, reverse proxy, or cloud hosting

- **Optional: PostgreSQL Database** (for analytics)
  - PostgreSQL 12+ for analytics and telemetry collection
  - Can be disabled if not needed

### Software Requirements

- Docker and Docker Compose (recommended)
  
  **OR**
  
- .NET 8.0 Runtime
- SQLite support
- PostgreSQL client libraries (if using analytics)

### Meshtastic Configuration

Your Meshtastic devices must have:
- **Primary Channel Name**: `LongFast` (or custom - must match config)
- **Primary Channel PSK**: `AQ==` (default) or your custom PSK in base64
- **LoRa Settings**: "OK to MQTT" must be enabled
- **MQTT Module**: Enabled and configured to your broker
- **Position**: Enable position broadcasts for location tracking

---

## Installation

### Quick Start with Docker Compose

1. **Clone the repository**

   ```bash
   git clone https://github.com/yourusername/TMesh.git
   cd TMesh/src/TMesh
   ```

2. **Generate PKI key pair for your virtual node**

   ```bash
   docker run --rm -v $(pwd)/config:/tbot/config \
     -v $(pwd)/data:/tbot/data \
     tmesh-tbot /generatekeys
   ```

   Copy the PublicKey and PrivateKey from the output - you'll need them for configuration.

3. **Create configuration files**

   **For TBot** (`config/appsettings.json`):
   ```json
   {
     "TBot": {
       "MqttAddress": "your-mqtt-broker.com",
       "MqttPort": 8883,
       "MqttUser": "your-mqtt-user",
       "MqttPassword": "your-mqtt-password",
       "MqttUseTls": true,
       "MqttAllowUntrustedCertificates": false,
       "MqttTelegramTopic": "TProxy/prod/telegram/update",
       "MqttStatusTopic": "TBot/prod/status",
       "MqttMeshtasticTopicPrefix": "msh/US/2/e",
       
       "TelegramApiToken": "123456789:ABCdefGHIjklMNOpqrsTUVwxyz",
       "TelegramBotUserName": "your_bot_username",
       "TelegramWebhookSecret": "your-random-secret-string",
       "TelegramUpdateWebhookUrl": "https://your-domain.com/update",
       "TelegramBotMaxConnections": 5,
       
       "SQLiteConnectionString": "Data Source=/tbot/data/tbot.db",
       "AnalyticsPostgresConnectionString": "",
       
       "MeshtasticNodeId": 123456789,
       "MeshtasticNodeNameShort": "TMSH",
       "MeshtasticNodeNameLong": "TMesh-Bot",
       "MeshtasticPrimaryChannelName": "LongFast",
       "MeshtasticPrimaryChannelPskBase64": "AQ==",
       "MeshtasticSecondayChannels": [
         {
           "Name": "Services",
           "PskBase64": "AQ=="
         }
       ],
       
       "MeshtasticPublicKeyBase64": "paste-public-key-here",
       "MeshtasticPrivateKeyBase64": "paste-private-key-here",
       
       "OutgoingMessageHopLimit": 7,
       "OwnNodeInfoMessageHopLimit": 7,
       "MeshtasticMaxOutgoingMessagesPerMinute": 30,
       "SentTBotNodeInfoEverySeconds": 3600,
       
       "GatewayNodeIds": [123456789, 987654321],
       "DirectGatewayRoutingSeconds": 3600,
       "BridgeDirectMessagesToGateways": true,
       
       "AdminPassword": "your-secure-admin-password",
       
       "ReplyToPublicPingsViaDirectMessage": false,
       "PingWords": ["ping"],
       
       "TimeZone": "UTC",
       "UpgradeDbOnStart": true,
       
       "DefaultMqttPasswordDeriveSecret": "",
       "MqttUserPasswordDeriveSecrets": {},
       
       "Texts": {
         "PingReply": "pong",
         "NotRegisteredDeviceReply": "{nodeName} is not registered with {botName} (Telegram)"
       }
     }
   }
   ```

   **Optional: Enable Analytics Database**
   
   Add PostgreSQL connection string to enable telemetry collection:
   ```json
   "AnalyticsPostgresConnectionString": "Host=postgres;Database=tmesh_analytics;Username=tmesh;Password=your-secure-password"
   ```

   **For TProxy** (`tproxy-config/appsettings.json`):
   ```json
   {
     "TProxy": {
       "MqttAddress": "your-mqtt-broker.com",
       "MqttPort": 8883,
       "MqttUser": "your-mqtt-user",
       "MqttPassword": "your-mqtt-password",
       "MqttUseTls": true,
       "MqttAllowUntrustedCertificates": false,
       "MqttTelegramTopic": "TProxy/prod/telegram/update",
       "MqttStatusTopic": "TBot/prod/status",
       "TelegramWebhookSecret": "your-random-secret-string",
       "DisableTelegramTokenValidation": false
     }
   }
   ```

4. **Create `docker-compose.yml`**

   ```yaml
   version: '3.8'
   
   services:
     # PostgreSQL for analytics (optional)
     postgres:
       image: postgres:15
       container_name: tmesh-postgres
       environment:
         POSTGRES_DB: tmesh_analytics
         POSTGRES_USER: tmesh
         POSTGRES_PASSWORD: your-secure-password
       volumes:
         - postgres-data:/var/lib/postgresql/data
       restart: unless-stopped
       networks:
         - tmesh-network
   
     tbot:
       build:
         context: ./TBot
         dockerfile: Dockerfile
       container_name: tmesh-tbot
       volumes:
         - ./config:/tbot/config:ro
         - ./data:/tbot/data
       environment:
         - TBOT_CONFIG_PATH=/tbot/config
       restart: unless-stopped
       depends_on:
         - postgres
       networks:
         - tmesh-network
   
     tproxy:
       build:
         context: ./TProxy
         dockerfile: Dockerfile
       container_name: tmesh-tproxy
       volumes:
         - ./tproxy-config:/app/config:ro
       ports:
         - "5000:8080"
       restart: unless-stopped
       networks:
         - tmesh-network
   
   volumes:
     postgres-data:
   
   networks:
     tmesh-network:
       driver: bridge
   ```

   **Note**: Remove the postgres service if not using analytics.

5. **Build and start services**

   ```bash
   docker-compose up -d
   ```

6. **Initialize database**

   ```bash
   docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /updatedb
   ```

   If `UpgradeDbOnStart: true`, database migrations run automatically on startup.

7. **Install Telegram webhook**

   ```bash
   docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /installwebhook
   ```

   Verify webhook installation:
   ```bash
   docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /checkinstallwebhook
   ```

8. **Configure your reverse proxy** (nginx example)

   ```nginx
   server {
       listen 443 ssl;
       server_name your-domain.com;
       
       ssl_certificate /path/to/cert.pem;
       ssl_certificate_key /path/to/key.pem;
       
       location /update {
           proxy_pass http://localhost:5000/update;
           proxy_set_header Host $host;
           proxy_set_header X-Real-IP $remote_addr;
           proxy_set_header X-Telegram-Bot-Api-Secret-Token $http_x_telegram_bot_api_secret_token;
       }
       
       location /status {
           proxy_pass http://localhost:5000/status;
           proxy_set_header Host $host;
       }
   }
   ```

### Health Monitoring Endpoints

TProxy provides health check endpoints for monitoring:

- **Bot Health**: `GET /status/bot/health`
  - Returns 200 OK if bot is healthy
  - Returns 503 if bot or gateways are offline
  - Query parameters:
    - `gatewayDeadMinutes`: Minutes before gateway considered dead (default: 60)
    - `gatewayCheckMode`: "all" or "any" (default: "all")

- **Individual Gateway**: `GET /status/gateway/{gatewayNodeId}`
  - Returns 200 OK if specific gateway is healthy
  - Returns 503 if gateway is offline
  - Returns 404 if gateway ID not found

- **Bot Status**: `GET /status/bot`
  - Returns JSON with detailed bot status and gateway information

Example health check:
```bash
curl https://your-domain.com/status/bot/health?gatewayDeadMinutes=30&gatewayCheckMode=any
```

---

## Usage

### Bot Commands

#### Device Commands
- `/add_device [deviceId]` - Register a Meshtastic device (e.g., `/add_device !aabbcc11` or `/add_device` for interactive)
- `/remove_device [deviceId]` - Unregister a device from current chat (e.g., `/remove_device !aabbcc11` or `/remove_device` for interactive)
- `/remove_device_from_all_chats [deviceId]` - Unregister a device from all chats where you registered it

#### Channel Commands
- `/add_channel [name] [key]` - Register a private Meshtastic channel (e.g., `/add_channel MyTeam ZGeGFyhk...=` or `/add_channel` for interactive)
- `/remove_channel [channelId]` - Unregister a channel from current chat (e.g., `/remove_channel 5` or `/remove_channel` for list)
- `/remove_channel_from_all_chats [channelId]` - Unregister a channel from all chats where you registered it

#### Information Commands
- `/status [filter]` - List registered devices and channels (e.g., `/status` or `/status MyDevice`)
- `/position [filter]` - Show device positions on map (e.g., `/position` or `/position MyDevice`)

#### Admin Commands
- `/admin login <password>` - Enter admin mode
- `/admin logout` - Exit admin mode
- `/admin <command>` - Execute admin commands (see Admin Mode below)

#### General Commands
- `/stop` - Cancel ongoing registration/removal process

### Setting Up Your First Device

1. **Add the bot to your Telegram group**
   - Make the bot an administrator (required for message reading)

2. **Ensure your Meshtastic device is configured**
   - Set primary channel to match TMesh (default: `LongFast` with PSK `AQ==`)
   - Enable "OK to MQTT" in LoRa settings
   - Ensure device is connected to the mesh network and has MQTT enabled

3. **Exchange node information**
   - On your Meshtastic device, find the TMesh virtual node in your node list
   - Open the node and tap "Exchange user information"
   - The TMesh node broadcasts its info every hour by default

4. **Register your device**
   
   Quick method (if you know your device ID):
   ```
   /add_device !75bcd15
   ```
   
   Or interactive method:
   ```
   /add_device
   ```
   Then send your device ID when prompted.
   
   You can find your device ID:
   - In Meshtastic app: Settings → Device → Device ID
   - Format: Can be decimal (e.g., `123456789`) or hex (e.g., `!75bcd15` or `#75bcd15`)

5. **Verify with code**
   - Bot sends a 6-digit code to your Meshtastic device
   - You'll receive it as a message on your device screen
   - Reply with the code in Telegram within 5 minutes
   - **Important**: Device public key is pinned during registration to prevent MITM attacks

6. **Start communicating!**
   - Messages sent in the Telegram group appear on registered devices
   - Messages from devices appear in the Telegram group
   - Reply to messages to maintain conversation threading
   - Watch the emoji reactions for delivery status

### Setting Up a Private Channel

Private channels allow multiple Meshtastic devices to share a single Telegram conversation.

1. **Create a private channel on your Meshtastic devices**
   - Use Meshtastic app or CLI to create a new channel
   - Set a unique name (e.g., "SearchTeam", "EventCrew")
   - Note the channel PSK (encryption key)
   - Configure all team devices to use this channel

2. **Register the channel with TMesh**
   
   Quick method (if you have name and key):
   ```
   /add_channel SearchTeam ZGeGFyhkL6uTXb3g4LO3sUOUGyaHqrvU=
   ```
   
   Or interactive method:
   ```
   /add_channel
   ```
   Then provide:
   - Channel name when prompted
   - Channel key (PSK in base64) when prompted

3. **Channel verification**
   - Bot verifies the channel is not a public/well-known channel
   - Bot sends a verification code to the channel
   - Enter the code in Telegram to complete registration

4. **Start team communication!**
   - Messages from Telegram appear on all devices using the channel
   - Messages from any device on the channel appear in Telegram
   - Perfect for group coordination and team operations

**Important Notes:**
- Public channels (like LongFast) cannot be registered
- All devices on the channel will see Telegram messages
- Channel must be configured identically on all devices
- You can get channel info from Meshtastic app: Settings → Channels → [Your Channel] → QR Code

### Using Reply Threading

**Reply in Telegram to Meshtastic message:**
1. Long-press or click reply on any message from a Meshtastic device or channel
2. Type your reply
3. The reply will be sent as a threaded message to the specific device or channel

**Reply in Meshtastic to Telegram message:**
1. Open the message from TMesh in your Meshtastic app
2. Use the reply function in your app
3. Your reply will appear as a threaded reply in Telegram

### Viewing Device Positions

**View all registered device positions:**
```
/position
```

**View specific device position:**
```
/position MyDevice
```

This shows:
- Device location on map
- Horizontal accuracy
- Time since last position update
- Devices with unknown positions listed separately

### Admin Mode

Admin commands require authentication. First login:

```
/admin login <password>
```

Once authenticated, use admin commands:

**Send public announcement to primary channel:**
```
/admin public_text_primary Network maintenance in 1 hour
```

**Send announcement to specific channel:**
```
/admin public_text Services The relay is moving locations
```

**Send direct message to any device:**
```
/admin text !75bcd15 Testing direct message
```

**Query device information:**
```
/admin nodeinfo !75bcd15
```

**Remove device from database:**
```
/admin removenode !75bcd15
```

**Logout from admin mode:**
```
/admin logout
```

### Understanding Delivery Status

Messages use emoji reactions to show delivery status:

| Emoji | Status | Meaning |
|-------|--------|---------|
| ✍️ | Created | Message received, preparing to send |
| 👀 | Queued | Waiting in queue due to rate limiting |
| 🕊️ | Acknowledged | Received by mesh network |
| 👌 | Delivered | Confirmed delivery to target device |
| 👎 | Failed | Delivery failed |
| 🤷 | Unknown | No acknowledgment received after 2 minutes |

For messages sent to multiple devices or channels, you'll see a status message with emoji for each recipient.

**Status Timeline:**
1. Message created → ✍️
2. Queued for sending → 👀
3. Sent to mesh network → waiting for ACK
4. ACK received → 👌 or 🕊️ (depending on source)
5. If no ACK after 2 minutes → 🤷
6. If send fails → 👎

### Message Limitations

- **Maximum message length**: 233 bytes (after PKI overhead for devices, less for channels)
  - English letters: ~1 byte each (≈233 characters for devices)
  - Cyrillic letters: ~2 bytes each (≈116 characters for devices)
  - Emoji: ~4 bytes each (≈58 emoji for devices)
  - Mixed content: Calculate accordingly
  - Channel messages may have slightly less capacity due to encryption overhead

- **Rate limiting**: Maximum 30 messages per minute (configurable)
- **Queue delays**: Shown in status messages when active
- **Gateway routing**: Messages routed through optimal gateway automatically

### Multi-Gateway Operation

When multiple gateways are configured:
- **Automatic Routing**: Messages sent through gateway that last saw the destination device or channel
- **Hop Limit Optimization**: Dynamic hop limits based on gateway relationship
- **Health Monitoring**: Monitor individual gateway health via HTTP endpoints
- **Failover**: Automatic routing through alternate gateways if primary unavailable
- **Direct Gateway**: Routes direct within configured time window (default: 1 hour)

### Ping/Pong System

Configured via `PingWords` and `ReplyToPublicPingsViaDirectMessage`:

**Default behavior** (`ReplyToPublicPingsViaDirectMessage: false`):
- Ping sent on public channel → No automatic response

**Direct message mode** (`ReplyToPublicPingsViaDirectMessage: true`):
- Ping sent on public channel → Pong sent via direct message
- Helps verify connectivity without flooding public channels

Customize ping words and pong response in configuration:
```json
"PingWords": ["ping", "hello"],
"Texts": {
  "PingReply": "pong from TMesh"
}
```

### Tips for Best Performance

- Keep messages concise for faster mesh transmission
- Avoid sending messages in rapid succession (rate limiting applies)
- Monitor delivery status reactions
- Use reply threading to maintain conversation context
- If queue delays are high, wait before sending more messages
- Use `/status` to verify device and channel registration
- Use `/position` to check if devices are online and their locations
- Monitor gateway health via `/status/bot/health` endpoint
- For team operations, consider using private channels instead of individual devices

---

## Configuration Reference

### TBot Configuration Options

#### MQTT Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MqttAddress` | string | MQTT broker hostname or IP | `mqtt.example.com` |
| `MqttPort` | int | MQTT broker port | `8883` (TLS) or `1883` (plain) |
| `MqttUser` | string | MQTT username (empty if none) | `meshtastic` |
| `MqttPassword` | string | MQTT password (empty if none) | `secret123` |
| `MqttUseTls` | bool | Enable TLS/SSL encryption | `true` |
| `MqttAllowUntrustedCertificates` | bool | Allow self-signed certificates | `false` |
| `MqttTelegramTopic` | string | Topic for Telegram updates | `TProxy/prod/telegram/update` |
| `MqttStatusTopic` | string | Topic for bot status updates | `TBot/prod/status` |
| `MqttMeshtasticTopicPrefix` | string | Meshtastic MQTT topic prefix | `msh/US/2/e` |

#### Telegram Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `TelegramApiToken` | string | Bot token from @BotFather | `123456:ABC-DEF...` |
| `TelegramBotUserName` | string | Bot username | `my_mesh_bot` |
| `TelegramWebhookSecret` | string | Random secret for webhook validation | `random-secret-123` |
| `TelegramUpdateWebhookUrl` | string | Public HTTPS URL for webhook | `https://your-domain.com/update` |
| `TelegramBotMaxConnections` | int | Max Telegram webhook connections | `5` |

#### Database Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `SQLiteConnectionString` | string | SQLite database path for primary data | `Data Source=/tbot/data/tbot.db` |
| `AnalyticsPostgresConnectionString` | string | PostgreSQL connection for analytics (optional, leave empty to disable) | `Host=postgres;Database=tmesh_analytics;Username=tmesh;Password=pass` |
| `UpgradeDbOnStart` | bool | Automatically run database migrations on startup | `true` |

#### Meshtastic Node Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MeshtasticNodeId` | long | Virtual node ID (must be unique) | `123456789` |
| `MeshtasticNodeNameShort` | string | Short node name (max 4 chars) | `TMSH` |
| `MeshtasticNodeNameLong` | string | Full node name | `TMesh-Bot` |
| `MeshtasticPublicKeyBase64` | string | Virtual node public key | Generated via `/generatekeys` |
| `MeshtasticPrivateKeyBase64` | string | Virtual node private key | Generated via `/generatekeys` |
| `OutgoingMessageHopLimit` | int | Default hop limit for outgoing messages | `7` |
| `OwnNodeInfoMessageHopLimit` | int | Hop limit for node info broadcasts | `7` |
| `SentTBotNodeInfoEverySeconds` | int | Node info broadcast interval | `3600` (1 hour) |

#### Channel Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MeshtasticPrimaryChannelName` | string | Primary channel name | `LongFast` |
| `MeshtasticPrimaryChannelPskBase64` | string | Channel PSK in base64 | `AQ==` |
| `MeshtasticSecondayChannels` | array | Additional well-known channels for admin commands | See example below |

Example secondary channels:
```json
"MeshtasticSecondayChannels": [
  {
    "Name": "Services",
    "PskBase64": "AQ=="
  },
  {
    "Name": "Admin",
    "PskBase64": "different-key-here=="
  }
]
```

**Note**: Private channels are registered via bot commands, not configuration.

#### Gateway Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `GatewayNodeIds` | long[] | Array of gateway node IDs | `[123456789, 987654321]` |
| `DirectGatewayRoutingSeconds` | int | Time window for direct gateway routing | `3600` |
| `BridgeDirectMessagesToGateways` | bool | Bridge admin commands between gateways | `true` |

#### Rate Limiting
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MeshtasticMaxOutgoingMessagesPerMinute` | int | Rate limit for outgoing messages | `30` |

#### Admin Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `AdminPassword` | string | Password for admin commands | `secure-password-123` |

#### Ping/Pong Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `ReplyToPublicPingsViaDirectMessage` | bool | Reply to public pings via DM | `false` |
| `PingWords` | string[] | Words that trigger ping response | `["ping"]` |

#### MQTT Password Derivation Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `DefaultMqttPasswordDeriveSecret` | string | Default secret for password derivation | `your-secret-key` |
| `MqttUserPasswordDeriveSecrets` | object | Per-user secrets for MQTT password generation | `{"user1": "secret1"}` |

Use `/passwordgen <username>` command to generate MQTT passwords based on these secrets.

#### Localization Settings
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `TimeZone` | string | Time zone for timestamps | `UTC` or `America/New_York` |

#### Custom Text Messages
| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `Texts.PingReply` | string | Response to ping messages | `pong` |
| `Texts.NotRegisteredDeviceReply` | string | Message for unregistered devices | `{nodeName} is not registered...` |

Template variables for `NotRegisteredDeviceReply`:
- `{nodeName}` - Name of the device
- `{botName}` - Telegram bot username

### TProxy Configuration Options

| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MqttAddress` | string | MQTT broker hostname or IP | `mqtt.example.com` |
| `MqttPort` | int | MQTT broker port | `8883` |
| `MqttUser` | string | MQTT username | `meshtastic` |
| `MqttPassword` | string | MQTT password | `secret123` |
| `MqttUseTls` | bool | Enable TLS/SSL encryption | `true` |
| `MqttAllowUntrustedCertificates` | bool | Allow self-signed certificates | `false` |
| `MqttTelegramTopic` | string | Topic to publish Telegram updates | `TProxy/prod/telegram/update` |
| `MqttStatusTopic` | string | Topic to subscribe for bot status | `TBot/prod/status` |
| `TelegramWebhookSecret` | string | Must match TBot secret | `random-secret-123` |
| `DisableTelegramTokenValidation` | bool | Disable token check (NOT recommended) | `false` |

### Environment Variables

You can override configuration using environment variables:

```bash
export TBot__MqttAddress="mqtt.example.com"
export TBot__TelegramApiToken="123456:ABC..."
export TBot__MqttUseTls="true"
export TBot__AdminPassword="secure-pass"
export TBot__AnalyticsPostgresConnectionString="Host=postgres;Database=tmesh"
export TBot__UpgradeDbOnStart="true"
```

Note the double underscore `__` for nested properties.

### Generating a Unique Node ID

Your virtual node needs a unique ID in the Meshtastic network. To generate one:

```bash
# Pick any unused number, for example:
# Use last 8 digits of your phone number
# Or generate random: echo $((RANDOM * RANDOM))
```

**Important**: This ID must not conflict with any real Meshtastic devices on your network.

---

## Troubleshooting

### Device Registration Fails

**Problem**: "Device has not yet been seen by the MQTT node"

**Solution**:
1. Verify device has "OK to MQTT" enabled
2. Check primary channel name and PSK match TMesh config
3. Ensure MQTT module is enabled on your device
4. On your device, find TMesh node in node list and tap "Exchange user information"
5. Wait a few minutes for node info to propagate via MQTT
6. Check TBot logs: `docker logs tmesh-tbot`
7. Verify gateway is connected and forwarding to MQTT

### Channel Registration Fails

**Problem**: "Adding public, well known channels is not allowed"

**Solution**:
- TMesh only allows registration of private channels, not public ones
- Public channels like LongFast, ShortFast, etc. cannot be registered
- Create a custom private channel on your devices
- Use a unique channel name and custom PSK

**Problem**: "Invalid channel key format"

**Solution**:
- Channel key must be base64-encoded
- Key must be 16 or 32 bytes (AES-128 or AES-256)
- Get the key from Meshtastic app: Settings → Channels → [Your Channel] → QR Code
- Copy the PSK value (looks like: `ZGeGFyhkL6uTXb3g4LO3sUOUGyaHqrvU=`)

### No Messages Reaching Meshtastic

**Checklist**:
- [ ] MQTT broker is accessible from TMesh
- [ ] Meshtastic gateway is connected to MQTT
- [ ] Topic prefix matches: check `MqttMeshtasticTopicPrefix`
- [ ] Channel settings match (name and PSK)
- [ ] For channels: All devices have identical channel configuration
- [ ] Gateway node IDs configured in `GatewayNodeIds`
- [ ] Check TBot logs: `docker logs tmesh-tbot`
- [ ] Verify gateway health: `curl https://your-domain.com/status/gateway/GATEWAY_ID`

### Telegram Webhook Not Working

**Checklist**:
- [ ] TProxy is running and accessible
- [ ] HTTPS endpoint is valid and has valid certificate
- [ ] Webhook secret matches in both TBot and TProxy
- [ ] Verify webhook: `/checkinstallwebhook`
- [ ] Check TProxy logs: `docker logs tmesh-tproxy`
- [ ] Test webhook endpoint: `curl https://your-domain.com/update`

### Messages Stuck in Queue

**Cause**: Rate limiting to protect mesh network

**Solutions**:
- Wait for queue to clear (check estimated time in status)
- Reduce message frequency
- Increase `MeshtasticMaxOutgoingMessagesPerMinute` (carefully!)
- Check gateway health - slow gateways increase queue time

### Analytics Database Issues

**Problem**: Analytics not collecting data

**Solutions**:
1. Verify PostgreSQL is running: `docker ps | grep postgres`
2. Check connection string in configuration
3. Ensure database exists and is accessible
4. Run migrations: `docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /updatedb`
5. Check TBot logs for database errors

**Problem**: High database disk usage

**Solutions**:
- Analytics data is time-series and grows over time
- Implement data retention policy (delete old data)
- Use PostgreSQL partitioning for better performance
- Consider disabling analytics if not needed

### TLS/SSL Connection Issues

**Problem**: MQTT connection fails with TLS enabled

**Solutions**:
1. Verify MQTT broker supports TLS on the configured port
2. Check certificate validity
3. For self-signed certificates, set `MqttAllowUntrustedCertificates: true`
4. Check MQTT broker logs for connection attempts
5. Test MQTT connection: `mosquitto_sub -h broker -p 8883 --cafile ca.crt -t "#" -v`

### Gateway Health Issues

**Check gateway status**:
```bash
# Check all gateways
curl https://your-domain.com/status/bot/health

# Check specific gateway
curl https://your-domain.com/status/gateway/123456789

# Get detailed status
curl https://your-domain.com/status/bot
```

**Common issues**:
- Gateway offline: Verify gateway device is powered and connected
- No recent updates: Check MQTT connection from gateway
- All gateways offline: Check MQTT broker availability

### Position Not Updating

**Problem**: `/position` shows "unknown position"

**Solutions**:
1. Enable position broadcasts on Meshtastic device
2. Verify device is sending position messages to MQTT
3. Check that device is registered with TMesh
4. Position updates require device to send location data
5. Check TBot logs for position message processing

### Reply Threading Not Working

**Problem**: Replies not maintaining thread

**Solutions**:
1. Ensure you're using Telegram's reply feature (not just mentioning)
2. Original message must have delivery confirmation
3. Check that device or channel is still registered
4. Reply must be sent within message status cache timeout
5. Check TBot logs for reply correlation

### Admin Commands Not Working

**Problem**: Admin commands return error or no response

**Solutions**:
1. First login with `/admin login <password>`
2. Verify password matches `AdminPassword` in configuration
3. Check command syntax after login
4. Check TBot logs for authentication attempts
5. Verify bot is admin in the Telegram group
6. Use `/admin logout` to exit and try again

### Database Errors

```bash
# Backup current databases
cp data/tbot.db data/tbot.db.backup

# Apply migrations
docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /updatedb
```

Or enable automatic migrations:
```json
"UpgradeDbOnStart": true
```

---

## Security Considerations

### Data Storage

TMesh stores the following data (see [PRIVACY.md](PRIVACY.md) for details):
- Device registrations: Chat ID, Telegram user ID, device ID
- Channel registrations: Chat ID, Telegram user ID, channel ID, channel name, channel key
- Device information: Node ID, pinned public keys, positions with timestamps
- **Analytics (optional)**: Device metrics, position history, channel/air utilization
- Messages are NOT stored permanently
- Temporary verification codes (5-minute expiry)
- Device-gateway associations (for routing optimization)

### Network Security

- All Meshtastic device messages use PKI encryption (X25519)
- All Meshtastic channel messages use AES encryption with PSK
- **Public key pinning** prevents MITM attacks after initial device registration
- **Channel key validation** ensures only authorized users register channels
- Telegram webhooks protected by secret tokens
- MQTT connections support TLS/SSL with certificate validation
- Only devices with "OK to MQTT" flag are accessible
- Admin commands require password authentication
- Rate limiting prevents abuse

### Best Practices

- **Use TLS for MQTT**: Set `MqttUseTls: true` in production
- **Validate certificates**: Keep `MqttAllowUntrustedCertificates: false` unless using self-signed
- **Strong passwords**: Use long, random passwords for admin commands
- **Secure channel keys**: Keep private channel PSKs secret
- **Gateway security**: Physically secure your gateway devices
- **Key protection**: Protect private key in configuration with file permissions
- **Regular updates**: Keep TMesh Docker images updated
- **Monitor access**: Review TBot logs for suspicious activity
- **Limit bot permissions**: Only grant necessary Telegram group permissions
- **Backup databases**: Regular backups of device registrations, channels, and keys
- **Analytics privacy**: Only enable analytics if you need it
- **Secure PostgreSQL**: Use strong passwords and limit network access

### TLS/SSL Configuration

For production deployments with MQTT over TLS:

1. **MQTT Broker Configuration** (Mosquitto example):
   ```
   listener 8883
   certfile /path/to/server.crt
   keyfile /path/to/server.key
   cafile /path/to/ca.crt
   require_certificate false
   ```

2. **TMesh Configuration**:
   ```json
   "MqttPort": 8883,
   "MqttUseTls": true,
   "MqttAllowUntrustedCertificates": false
   ```

3. **For self-signed certificates**:
   ```json
   "MqttAllowUntrustedCertificates": true
   ```

### Public Key Pinning (Devices)

TMesh automatically pins device public keys during registration:
- First registration: Public key is stored and pinned
- Subsequent messages: Validated against pinned key
- Prevents man-in-the-middle attacks
- Key cannot be changed without re-registration
- Protects against compromised MQTT infrastructure

### Channel Key Security

TMesh stores private channel keys in the database:
- Keys are stored in SQLite unencrypted
- Keys validated during channel registration
- Same channel key must be used across all devices
- Keep channel PSKs secret

---

## FAQ

**Q: Can multiple Telegram groups use the same Meshtastic device?**  
A: Yes! A single device can be registered to multiple Telegram chats. Messages from all chats will be sent to the device, and device messages will be broadcast to all registered chats.

**Q: Can multiple Telegram groups use the same private channel?**  
A: Yes! A single private channel can be registered to multiple Telegram chats. Messages from all chats will be sent to all devices on the channel.

**Q: What's the difference between registering a device vs. a channel?**  
A: 
- **Device registration**: One-to-one between Telegram and a specific device. Messages are encrypted with the device's public key.
- **Channel registration**: One-to-many between Telegram and all devices on a private channel. Messages are encrypted with the channel's PSK.

**Q: Can I register public channels like LongFast?**  
A: No. TMesh only allows registration of private channels to prevent spam and abuse. Public channels are accessible to everyone and should not be bridged to private Telegram groups.

**Q: How do I get the channel key (PSK)?**  
A: In Meshtastic app: Settings → Channels → [Your Channel] → Show QR Code → Copy the PSK value

**Q: How does multi-gateway routing work?**  
A: TMesh tracks which gateway last saw each device and automatically routes messages through that gateway for optimal delivery. Hop limits are dynamically adjusted based on the device/channel-gateway relationship. For channels messages are broadcasted via all gateways.

**Q: What happens if I lose the verification code?**  
A: Start the registration process again with `/add_device` or `/add_channel`. You're limited to 5 verification attempts per device per hour.

**Q: Can I use TMesh with private MQTT brokers?**  
A: Absolutely! TMesh works with any MQTT broker that supports QoS 1. TLS/SSL is highly recommended for production.

**Q: Does TMesh work with encrypted channels?**  
A: Yes! TMesh supports private encrypted channels. You provide the channel name and PSK during registration.

**Q: What's the difference between TBot and TProxy?**  
A: TProxy is a lightweight webhook receiver that forwards to MQTT. TBot contains all the logic for bot operations, Meshtastic communication, device/channel management, message handling, analytics collection, and gateway management.

**Q: Can I run multiple TMesh instances?**  
A: Not recommended on the same Meshtastic network - each creates a virtual node. Use one instance with multiple gateway nodes for redundancy.

**Q: How do I backup my registrations?**  
A: Backup the SQLite database file: `cp data/tbot.db data/tbot.db.backup`. The database includes device registrations, channel registrations, pinned public keys, channel keys, and position data. Also backup PostgreSQL if using analytics.

**Q: Why do messages sometimes take a while to deliver?**  
A: TMesh implements rate limiting to prevent mesh network congestion. Check the queue status in bot messages. Multi-gateway setups can improve delivery times.

**Q: How accurate is position tracking?**  
A: Position accuracy depends on your Meshtastic device's GPS. TMesh displays the accuracy radius reported by the device (typically 5-50 meters for GPS).

**Q: Can I use reply threading with multiple devices or channels?**  
A: Yes! When replying in Telegram, the reply is sent only to the device or channel that sent the original message, maintaining proper conversation threading.

**Q: How do I monitor gateway health?**  
A: Use the health check endpoints at `/status/bot/health` and `/status/gateway/{id}`. These can be integrated with monitoring systems like Prometheus or Uptime Kuma.

**Q: What's the 🤷 status?**  
A: "Unknown" status appears when no acknowledgment is received from the mesh network within 2 minutes. The message may still be delivered, but confirmation couldn't be obtained.

**Q: What data does analytics collect?**  
A: If enabled, analytics collects: device ID, timestamp, position, channel utilization, and air utilization. No message content is ever stored. See [PRIVACY.md](PRIVACY.md) for full details.

**Q: Can I disable analytics after enabling it?**  
A: Yes, remove the `AnalyticsPostgresConnectionString` from configuration and restart TMesh. Historical data remains in PostgreSQL but won't grow.

**Q: Do I need PostgreSQL if I don't want analytics?**  
A: No! Analytics is completely optional. Leave `AnalyticsPostgresConnectionString` empty to disable it. TMesh works perfectly fine with just SQLite.

---

## Development

### Building from Source

```bash
# Clone repository
git clone https://github.com/yourusername/TMesh.git
cd TMesh/src/TMesh

# Build TBot
cd TBot
dotnet restore
dotnet build

# Build TProxy
cd ../TProxy
dotnet restore
dotnet build
```

### Running Locally

1. Copy `appsettings.sample.json` to `appsettings.json` in each project
2. Configure with your settings
3. Run from IDE or:

```bash
dotnet run --project TBot
dotnet run --project TProxy
```

### Project Structure

```
TMesh/
├── TBot/                      # Main service
│   ├── Analytics/            # Analytics service and models
│   │   ├── AnalyticsDbContext.cs
│   │   ├── AnalyticsService.cs
│   │   └── Models/
│   │       └── DeviceMetric.cs
│   ├── BotService.cs         # Telegram bot logic
│   ├── MqttService.cs        # MQTT connectivity with TLS
│   ├── MeshtasticService.cs  # Message encryption/queuing
│   ├── RegistrationService.cs # Device & channel management
│   ├── Database/             # EF Core models
│   │   └── Models/           # Device, Channel, Registration models
│   └── Models/               # Data structures
├── TProxy/                    # Webhook proxy
│   ├── Controllers/          # HTTP endpoints
│   │   ├── TelegramController.cs # Webhook handler
│   │   └── StatusController.cs   # Health monitoring
│   └── MqttService.cs        # MQTT publisher with status
└── docker-compose.yml        # Deployment config
```

### Technology Stack

- **Backend**: .NET 8.0, C#
- **Primary Database**: SQLite with Entity Framework Core
- **Analytics Database**: PostgreSQL with Entity Framework Core (optional)
- **MQTT**: MQTTnet library with TLS support
- **Telegram**: Telegram.Bot library
- **Cryptography**: BouncyCastle (X25519 for PKI, AES for channels)
- **Messaging**: Meshtastic Protobufs
- **Time Handling**: NodaTime for analytics timestamps
- **Containerization**: Docker

---

## Contributing

Contributions are welcome! Here's how you can help:

### Reporting Issues

- Use GitHub Issues for bug reports
- Include logs (with sensitive data removed)
- Describe steps to reproduce
- Specify your environment (OS, Docker version, MQTT broker, databases, etc.)
- Include gateway configuration if relevant
- Mention if using device or channel registrations

### Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow existing code style
- Add comments for complex logic
- Update README and PRIVACY.md if adding features
- Test with real Meshtastic devices and channels if possible
- Ensure Docker builds work
- Test multi-gateway scenarios when relevant
- Consider privacy implications of new features

---

## Roadmap

### Completed Features ✅
- [x] Device registration and management
- [x] **Private channel registration and management**
- [x] Message threading and replies between platforms
- [x] TLS/SSL MQTT connections
- [x] Multi-gateway support with intelligent routing
- [x] Position tracking and mapping
- [x] **Analytics database with PostgreSQL support**
- [x] Trace route display
- [x] Admin mode for announcements
- [x] Public key pinning for security
- [x] Health monitoring endpoints
- [x] Ping/pong system
- [x] Multiple channel support
- [x] Unknown status handling (🤷 emoji)
- [x] Customizable text messages
- [x] **MQTT password derivation**
- [x] **Automatic database migrations on startup**
- [x] Remove device/channel from all chats


---

## Acknowledgments

- [Meshtastic Project](https://meshtastic.org/) - For the amazing mesh networking platform
- [MQTTnet](https://github.com/dotnet/MQTTnet) - Excellent .NET MQTT library with TLS support
- [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) - .NET Telegram Bot API library
- [Bouncy Castle](https://www.bouncycastle.org/) - Cryptography library for PKI
- [NodaTime](https://nodatime.org/) - Better time handling for .NET
- All contributors and testers who helped improve TMesh

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

```
MIT License

Copyright (c) 2025 Alexander Shakhov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## Support & Community

- **Issues**: [GitHub Issues](https://github.com/samfromlv/tmesh/issues)
- **Discussions**: [GitHub Discussions](https://github.com/samfromlv/tmesh/discussions)


---

## Disclaimer

TMesh is an independent project and is not officially affiliated with or endorsed by the Meshtastic project. Use at your own risk. Always test thoroughly before deploying in critical scenarios.

Meshtastic networks operate on shared radio frequencies. TMesh implements rate limiting and intelligent routing to be a good neighbor, but users are responsible for ensuring their usage complies with local regulations and community guidelines.

**Security Notice**: TMesh implements public key pinning (for devices) and key validation (for channels) to prevent MITM attacks, but the initial key exchange during registration relies on the security of your MQTT infrastructure. Use TLS/SSL for MQTT in production environments. Keep private channel keys secure.

**Privacy Notice**: If analytics are enabled, device metrics are collected. No message content is ever stored. See [PRIVACY.md](PRIVACY.md) for complete details.

**Health & Safety**: Do not rely on TMesh for emergency communications. Always have backup communication methods available.

---

<div align="center">

**Made with ❤️ for the Meshtastic and Telegram communities**

⭐ Star this repo if you find it useful!

[Report Bug](https://github.com/samfromlv/tmesh/issues) • [Request Feature](https://github.com/samfromlv/tmesh/issues) • [Contribute](CONTRIBUTING.md)

</div>
