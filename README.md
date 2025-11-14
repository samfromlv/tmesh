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

- **🔐 End-to-End Encryption**: Uses Meshtastic PKI for secure device-to-device communication
- **📱 Telegram Integration**: Full bot integration with webhook support
- **⚡ Smart Queue Management**: Rate-limiting and prioritization prevent mesh network overload
- **📊 Delivery Tracking**: Real-time message status updates via Telegram reactions
- **🔄 Bidirectional Sync**: Messages flow seamlessly in both directions
- **🐳 Docker Ready**: Easy deployment with Docker containers

---

## Features

### For Users
- Register multiple Meshtastic devices to Telegram group chats
- Send messages from Telegram that appear on your Meshtastic devices
- Receive Meshtastic messages in Telegram with sender identification
- Real-time delivery status with emoji reactions:
  - ✍️ Message created
  - 👀 Queued for delivery
  - 🕊️ Acknowledged by mesh network
  - 👌 Delivered to device
  - 👎 Delivery failed
- Device verification via secure 6-digit codes
- Support for multiple chat registrations per device

### For Network Health
- Configurable message rate limiting (default: 30 messages/minute)
- Server-side message queuing prevents mesh flooding
- Smart hop limit management for optimal routing
- Message deduplication
- Priority-based message handling (High/Normal/Low)

---

## How It Works

### Architecture

TMesh consists of two main components:

1. **TBot** (Main Service)
   - Manages Telegram bot operations
   - Handles MQTT connectivity to Meshtastic network
   - Processes message encryption/decryption
   - Maintains SQLite database for registrations and device info
   - Implements message queue and rate limiting

2. **TProxy** (Webhook Proxy)
   - Receives Telegram webhook updates
   - Publishes updates to MQTT for TBot consumption
   - Validates webhook security tokens

```
┌─────────────┐          ┌──────────┐          ┌─────────────┐
│  Telegram   │◄────────►│  TProxy  │          │   TBot      │
│   Users     │          │(Webhook) │          │(Main Logic) │
└─────────────┘          └──────────┘          └─────────────┘
                               │                      │
                               └──────────┬──────────┘
                                         │
                                    ┌────▼────┐
                                    │  MQTT   │
                                    │ Broker  │
                                    └────┬────┘
                                         │
                               ┌─────────▼─────────┐
                               │   Meshtastic      │
                               │   Mesh Network    │
                               │  (Virtual Node)   │
                               └───────────────────┘
```

### Communication Flow

**Telegram → Meshtastic:**
1. User sends message in Telegram group
2. Telegram sends webhook to TProxy
3. TProxy publishes to MQTT
4. TBot receives message, looks up registered devices
5. Message queued with rate limiting
6. Message encrypted with device's public key (PKI)
7. Encrypted packet sent to MQTT → Meshtastic mesh
8. Delivery status updates sent back to Telegram as reactions

**Meshtastic → Telegram:**
1. Device sends encrypted message to virtual node via mesh
2. MQTT broker forwards to TBot
3. TBot decrypts using private key
4. Looks up device registrations
5. Sends formatted message to registered Telegram chats
6. Sends ACK back to Meshtastic device

### Security Model

- Each Meshtastic device has a unique PKI key pair
- TMesh virtual node has its own key pair
- All messages encrypted end-to-end using X25519 elliptic curve
- Device verification via one-time codes sent through mesh network
- Webhook security tokens validate Telegram requests
- Only devices with "OK to MQTT" enabled are accessible

---

## Prerequisites

### Infrastructure Requirements

- **MQTT Broker** (e.g., Mosquitto, HiveMQ, EMQX)
  - Must be accessible from both TMesh and your Meshtastic MQTT gateway
  - Support for QoS 1 (at least once delivery)
  - Recommended: Persistent sessions enabled

- **Telegram Bot**
  - Create via [@BotFather](https://t.me/botfather)
  - Note your bot API token
  - Bot must be added as admin to target groups

- **Meshtastic Gateway**
  - At least one Meshtastic device with MQTT enabled
  - "OK to MQTT" setting enabled in LoRa configuration
  - Connected to the same MQTT broker
  - Primary channel must match TMesh configuration

- **Public Webhook Endpoint** (for TProxy)
  - HTTPS endpoint accessible by Telegram servers
  - Can use: ngrok, Cloudflare Tunnel, reverse proxy, or cloud hosting

### Software Requirements

- Docker and Docker Compose (recommended)
  
  **OR**
  
- .NET 8.0 Runtime
- SQLite support

### Meshtastic Configuration

Your Meshtastic devices must have:
- **Primary Channel Name**: `LongFast` (or custom - must match config)
- **Primary Channel PSK**: `AQ==` (default) or your custom PSK in base64
- **LoRa Settings**: "OK to MQTT" must be enabled
- **MQTT Module**: Enabled and configured to your broker

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
       "MqttPort": 1883,
       "MqttUser": "your-mqtt-user",
       "MqttPassword": "your-mqtt-password",
       "MqttTelegramTopic": "TProxy/prod/telegram/update",
       "MqttMeshtasticTopicPrefix": "msh/US/2/e",
       
       "TelegramApiToken": "123456789:ABCdefGHIjklMNOpqrsTUVwxyz",
       "TelegramWebhookSecret": "your-random-secret-string",
       "TelegramUpdateWebhookUrl": "https://your-domain.com/update",
       "TelegramBotMaxConnections": 5,
       
       "SQLiteConnectionString": "Data Source=/tbot/data/tbot.db",
       
       "MeshtasticNodeId": 123456789,
       "MeshtasticNodeNameShort": "TMSH",
       "MeshtasticNodeNameLong": "TMesh-Bot",
       "MeshtasticPrimaryChannelName": "LongFast",
       "MeshtasticPrimaryChannelPskBase64": "AQ==",
       
       "MeshtasticPublicKeyBase64": "paste-public-key-here",
       "MeshtasticPrivateKeyBase64": "paste-private-key-here",
       
       "OutgoingMessageHopLimit": 7,
       "OwnNodeInfoMessageHopLimit": 7,
       "MeshtasticMaxOutgoingMessagesPerMinute": 30,
       "SentTBotNodeInfoEverySeconds": 3600
     }
   }
   ```

   **For TProxy** (`tproxy-config/appsettings.json`):
   ```json
   {
     "TProxy": {
       "MqttAddress": "your-mqtt-broker.com",
       "MqttPort": 1883,
       "MqttUser": "your-mqtt-user",
       "MqttPassword": "your-mqtt-password",
       "MqttTelegramTopic": "TProxy/prod/telegram/update",
       "TelegramWebhookSecret": "your-random-secret-string",
       "DisableTelegramTokenValidation": false
     }
   }
   ```

4. **Create `docker-compose.yml`**

   ```yaml
   version: '3.8'
   
   services:
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
         - "5000:8080"  # Adjust as needed
       restart: unless-stopped
       networks:
         - tmesh-network
   
   networks:
     tmesh-network:
       driver: bridge
   ```

5. **Build and start services**

   ```bash
   docker-compose up -d
   ```

6. **Initialize database**

   ```bash
   docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /updatedb
   ```

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
   }
   ```

### Alternative: Manual Installation (Without Docker)

1. **Build the projects**

   ```bash
   cd TMesh/TBot
   dotnet publish -c Release -o ./publish
   
   cd ../TProxy
   dotnet publish -c Release -o ./publish
   ```

2. **Configure appsettings.json** in each publish directory

3. **Generate keys**

   ```bash
   cd TBot/publish
   dotnet TBot.dll /generatekeys
   ```

4. **Update database**

   ```bash
   dotnet TBot.dll /updatedb
   ```

5. **Install webhook**

   ```bash
   dotnet TBot.dll /installwebhook
   ```

6. **Run the services**

   ```bash
   # Terminal 1 - TBot
   dotnet TBot.dll
   
   # Terminal 2 - TProxy
   cd ../TProxy/publish
   dotnet TProxy.dll
   ```

---

## Usage

### Setting Up Your First Device

1. **Add the bot to your Telegram group**
   - Make the bot an administrator (required for message reading)

2. **Ensure your Meshtastic device is configured**
   - Set primary channel to match TMesh (default: `LongFast` with PSK `AQ==`)
   - Enable "OK to MQTT" in LoRa settings
   - Ensure device is connected to the mesh network

3. **Exchange node information**
   - On your Meshtastic device, find the TMesh virtual node in your node list
   - Open the node and tap "Exchange user information"
   - The TMesh node broadcasts its info every hour by default

4. **Register your device**
   
   In your Telegram group:
   ```
   /add
   ```
   
   The bot will ask for your device ID. You can find this:
   - In Meshtastic app: Settings → Device → Device ID
   - Format: Can be decimal (e.g., `123456789`) or hex (e.g., `!75bcd15` or `#75bcd15`)

5. **Verify with code**
   - Bot sends a 6-digit code to your Meshtastic device
   - You'll receive it as a message on your device screen
   - Reply with the code in Telegram within 5 minutes

6. **Start communicating!**
   - Messages sent in the Telegram group appear on registered devices
   - Messages from devices appear in the Telegram group with sender name
   - Watch the emoji reactions for delivery status

### Bot Commands

- `/add` - Register a new Meshtastic device to this chat
- `/remove` - Unregister a device (coming soon)
- `/status` - List all registered devices in this chat
- `/stop` - Cancel ongoing registration process

### Understanding Delivery Status

Messages use emoji reactions to show delivery status:

| Emoji | Status | Meaning |
|-------|--------|---------|
| ✍️ | Created | Message received, preparing to send |
| 👀 | Queued | Waiting in queue due to rate limiting |
| 🕊️ | Acknowledged | Received by mesh network |
| 👌 | Delivered | Confirmed delivery to target device |
| 👎 | Failed | Delivery failed |

For messages sent to multiple devices, you'll see a status message with emoji for each device.

### Message Limitations

- **Maximum message length**: 233 bytes
  - English letters: ~1 byte each (≈233 characters)
  - Cyrillic letters: ~2 bytes each (≈116 characters)
  - Emoji: ~4 bytes each (≈58 emoji)
  - Mixed content: Calculate accordingly

- **Rate limiting**: Maximum 30 messages per minute (configurable)
- **Queue delays**: Shown in status messages when active

### Tips for Best Performance

- Keep messages concise for faster mesh transmission
- Avoid sending messages in rapid succession
- Monitor delivery status reactions
- If queue delays are high, wait before sending more messages
- Use `/status` to verify device registration

---

## Configuration Reference

### TBot Configuration Options

| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MqttAddress` | string | MQTT broker hostname or IP | `mqtt.example.com` |
| `MqttPort` | int | MQTT broker port | `1883` |
| `MqttUser` | string | MQTT username (empty if none) | `meshtastic` |
| `MqttPassword` | string | MQTT password (empty if none) | `secret123` |
| `MqttTelegramTopic` | string | Topic for Telegram updates | `TProxy/prod/telegram/update` |
| `MqttMeshtasticTopicPrefix` | string | Meshtastic MQTT topic prefix | `msh/US/2/e` |
| `TelegramApiToken` | string | Bot token from @BotFather | `123456:ABC-DEF...` |
| `TelegramWebhookSecret` | string | Random secret for webhook validation | `random-secret-123` |
| `TelegramUpdateWebhookUrl` | string | Public HTTPS URL for webhook | `https://your-domain.com/update` |
| `TelegramBotMaxConnections` | int | Max Telegram webhook connections | `5` |
| `SQLiteConnectionString` | string | SQLite database path | `Data Source=/tbot/data/tbot.db` |
| `OutgoingMessageHopLimit` | int | Hop limit for outgoing messages | `7` |
| `OwnNodeInfoMessageHopLimit` | int | Hop limit for node info broadcasts | `7` |
| `MeshtasticNodeId` | int | Virtual node ID (must be unique) | `123456789` |
| `MeshtasticNodeNameShort` | string | Short node name (max 4 chars) | `TMSH` |
| `MeshtasticNodeNameLong` | string | Full node name | `TMesh-Bot` |
| `MeshtasticPrimaryChannelName` | string | Primary channel name | `LongFast` |
| `MeshtasticPrimaryChannelPskBase64` | string | Channel PSK in base64 | `AQ==` |
| `MeshtasticPublicKeyBase64` | string | Virtual node public key | Generated via `/generatekeys` |
| `MeshtasticPrivateKeyBase64` | string | Virtual node private key | Generated via `/generatekeys` |
| `MeshtasticMaxOutgoingMessagesPerMinute` | int | Rate limit for outgoing messages | `30` |
| `SentTBotNodeInfoEverySeconds` | int | Node info broadcast interval | `3600` (1 hour) |

### TProxy Configuration Options

| Setting | Type | Description | Example |
|---------|------|-------------|---------|
| `MqttAddress` | string | MQTT broker hostname or IP | `mqtt.example.com` |
| `MqttPort` | int | MQTT broker port | `1883` |
| `MqttUser` | string | MQTT username | `meshtastic` |
| `MqttPassword` | string | MQTT password | `secret123` |
| `MqttTelegramTopic` | string | Topic to publish Telegram updates | `TProxy/prod/telegram/update` |
| `TelegramWebhookSecret` | string | Must match TBot secret | `random-secret-123` |
| `DisableTelegramTokenValidation` | bool | Disable token check (NOT recommended) | `false` |

### Environment Variables

You can override configuration using environment variables:

```bash
export TBot__MqttAddress="mqtt.example.com"
export TBot__TelegramApiToken="123456:ABC..."
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
3. On your device, find TMesh node in node list and tap "Exchange user information"
4. Wait a few minutes for node info to propagate
5. Try registration again

### No Messages Reaching Meshtastic

**Checklist**:
- [ ] MQTT broker is accessible from TMesh
- [ ] Meshtastic gateway is connected to MQTT
- [ ] Topic prefix matches: check `MqttMeshtasticTopicPrefix`
- [ ] Channel settings match (name and PSK)
- [ ] Check TBot logs: `docker logs tmesh-tbot`

### Telegram Webhook Not Working

**Checklist**:
- [ ] TProxy is running and accessible
- [ ] HTTPS endpoint is valid and has valid certificate
- [ ] Webhook secret matches in both TBot and TProxy
- [ ] Verify webhook: `/checkinstallwebhook`
- [ ] Check TProxy logs: `docker logs tmesh-tproxy`

### Messages Stuck in Queue

**Cause**: Rate limiting to protect mesh network

**Solutions**:
- Wait for queue to clear (check estimated time in status)
- Reduce message frequency
- Increase `MeshtasticMaxOutgoingMessagesPerMinute` (carefully!)

### Database Errors

```bash
# Backup current database
cp data/tbot.db data/tbot.db.backup

# Apply migrations
docker exec tmesh-tbot dotnet /tbot/app/TBot.dll /updatedb
```

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
│   ├── BotService.cs         # Telegram bot logic
│   ├── MqttService.cs        # MQTT connectivity
│   ├── MeshtasticService.cs  # Message encryption/queuing
│   ├── RegistrationService.cs # Device management
│   ├── Database/             # EF Core models
│   └── Models/               # Data structures
├── TProxy/                    # Webhook proxy
│   └── Controllers/          # HTTP endpoints
└── docker-compose.yml        # Deployment config
```

### Technology Stack

- **Backend**: .NET 8.0, C#
- **Database**: SQLite with Entity Framework Core
- **MQTT**: MQTTnet library
- **Telegram**: Telegram.Bot library
- **Cryptography**: BouncyCastle (X25519 for PKI)
- **Messaging**: Meshtastic Protobufs
- **Containerization**: Docker

---

## Security Considerations

### Data Storage

TMesh stores the following data (see [PRIVACY.md](PRIVACY.md) for details):
- Device registrations: Chat ID, Telegram user ID, username
- Device information: Node ID, public keys from node info broadcasts
- Messages are NOT stored permanently
- Temporary verification codes (5-minute expiry)

### Network Security

- All Meshtastic messages use PKI encryption (X25519)
- Telegram webhooks protected by secret tokens
- MQTT credentials should use TLS/SSL in production
- Only devices with "OK to MQTT" flag are accessible

### Best Practices

- Use strong MQTT credentials
- Enable TLS for MQTT in production
- Keep webhook secret confidential
- Regularly update Docker images
- Monitor logs for suspicious activity
- Limit bot admin permissions to necessary chats

---

## Contributing

Contributions are welcome! Here's how you can help:

### Reporting Issues

- Use GitHub Issues for bug reports
- Include logs (with sensitive data removed)
- Describe steps to reproduce
- Specify your environment (OS, Docker version, etc.)

### Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow existing code style
- Add comments for complex logic
- Update README if adding features
- Test with real Meshtastic devices if possible
- Ensure Docker builds work

---

## Roadmap

- [ ] Device removal functionality (`/remove` command)
- [ ] Web dashboard for monitoring
- [ ] Support for multiple Telegram bots
- [ ] Message history/logging options
- [ ] Admin controls per chat
- [ ] Location sharing support
- [ ] File attachment bridging
- [ ] Multi-language support
- [ ] Metrics and monitoring integration
- [ ] Automated testing suite

---

## FAQ

**Q: Can multiple Telegram groups use the same Meshtastic device?**  
A: Yes! A single device can be registered to multiple Telegram chats. Messages from all chats will be sent to the device, and device messages will be broadcast to all registered chats.

**Q: What happens if I lose the verification code?**  
A: Start the registration process again with `/add`. You're limited to 5 verification attempts per device per hour.

**Q: Can I use TMesh with private MQTT brokers?**  
A: Absolutely! TMesh works with any MQTT broker. Just configure credentials in `appsettings.json`.

**Q: Does TMesh work with encrypted channels?**  
A: TMesh requires the primary channel to be configured (default `LongFast`). Secondary encrypted channels are not currently bridged.

**Q: What's the difference between TBot and TProxy?**  
A: TProxy is a simple webhook receiver that forwards to MQTT. TBot contains all the logic for bot operations, Meshtastic communication, and message handling. They can run on the same or different servers.

**Q: Can I run multiple TMesh instances?**  
A: Not recommended on the same Meshtastic network - each creates a virtual node. Use one instance with multiple bot tokens if needed.

**Q: How do I backup my registrations?**  
A: Backup the SQLite database file: `cp data/tbot.db data/tbot.db.backup`

**Q: Why do messages sometimes take a while to deliver?**  
A: TMesh implements rate limiting to prevent mesh network congestion. Check the queue status in the bot messages.

---

## Acknowledgments

- [Meshtastic Project](https://meshtastic.org/) - For the amazing mesh networking platform
- [MQTTnet](https://github.com/dotnet/MQTTnet) - Excellent .NET MQTT library
- [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) - .NET Telegram Bot API library
- [Bouncy Castle](https://www.bouncycastle.org/) - Cryptography library
- All contributors and testers

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

- **Issues**: [GitHub Issues](https://github.com/yourusername/TMesh/issues)
- **Discussions**: [GitHub Discussions](https://github.com/yourusername/TMesh/discussions)
- **Meshtastic Forum**: [meshtastic.discourse.group](https://meshtastic.discourse.group)

---

## Disclaimer

TMesh is an independent project and is not officially affiliated with or endorsed by the Meshtastic project. Use at your own risk. Always test thoroughly before deploying in critical scenarios.

Meshtastic networks operate on shared radio frequencies. TMesh implements rate limiting to be a good neighbor, but users are responsible for ensuring their usage complies with local regulations and community guidelines.

---

<div align="center">

**Made with ❤️ for the Meshtastic and Telegram communities**

⭐ Star this repo if you find it useful!

[Report Bug](https://github.com/yourusername/TMesh/issues) • [Request Feature](https://github.com/yourusername/TMesh/issues) • [Contribute](CONTRIBUTING.md)

</div>
