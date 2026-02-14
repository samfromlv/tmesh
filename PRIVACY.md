# Privacy Policy for TMesh

**Last Updated: February 15, 2026**

## Overview

TMesh is an open-source bridge between Meshtastic mesh networks and Telegram groups. This privacy policy explains what data TMesh collects, stores, and processes when you use the service. This policy applies to self-hosted instances of TMesh.

## Data Controller

TMesh is self-hosted software. The operator of each TMesh instance is the data controller responsible for the data processed by their installation. If you're using someone else's TMesh instance, please contact the instance operator for their specific privacy practices.

---

## Data Collection and Storage

### 1. Device Registration Data

When you register a Meshtastic device to a Telegram chat, TMesh stores:

| Data Type | Purpose | Storage Duration |
|-----------|---------|------------------|
| **Telegram Chat ID** | Identify which Telegram group/chat to send messages to | Until device is unregistered or database is cleared |
| **Telegram User ID** | Track registrations and manage verification code limits | Until device is unregistered or database is cleared |
| **Meshtastic Device ID** | Identify which Meshtastic device to communicate with | Until device is unregistered or database is cleared |
| **Registration Timestamp** | Track when the device was registered | Until device is unregistered or database is cleared |

**Storage Location**: SQLite database file (`tbot.db`)  
**Database Table**: `DeviceRegistrations`

### 2. Channel Registration Data

**NEW FEATURE**: TMesh now supports registering private Meshtastic channels (not just individual devices).

When you register a private channel to a Telegram chat, TMesh stores:

| Data Type | Purpose | Storage Duration |
|-----------|---------|------------------|
| **Telegram Chat ID** | Identify which Telegram group/chat to send messages to | Until channel is unregistered or database is cleared |
| **Telegram User ID** | Track who registered the channel and manage permissions | Until channel is unregistered or database is cleared |
| **Channel ID** (internal) | Unique identifier for the channel registration | Until channel is unregistered or database is cleared |
| **Channel Name** | Name of the private Meshtastic channel | Until channel is unregistered or database is cleared |
| **Channel Encryption Key (PSK)** | AES encryption key for channel messages | Until channel is unregistered or database is cleared |
| **Channel XOR Hash** | Hash for channel identification | Until channel is unregistered or database is cleared |
| **Registration Timestamp** | Track when the channel was registered | Until channel is unregistered or database is cleared |

**Storage Location**: SQLite database file (`tbot.db`)  
**Database Tables**: `Channels`, `ChannelRegistrations`

**Important Security Notes**:
- Channel encryption keys (PSKs) are stored in the database to enable message encryption/decryption
- Keys are necessary for TMesh to communicate with the private channel
- Protect your database file with appropriate file system permissions
- Regular backups should be encrypted at rest
- Anyone with access to the database can see channel keys

### 3. Meshtastic Device Information

TMesh automatically collects device information from Meshtastic node info broadcasts:

| Data Type | Purpose | Storage Duration |
|-----------|---------|------------------|
| **Device Node ID** | Unique identifier for Meshtastic devices | Updated when new node info received |
| **Node Name** | Display name of the Meshtastic device | Updated when new node info received |
| **Public Key (32 bytes)** | Enable PKI encryption for secure messaging and MITM prevention via key pinning | Pinned on first registration, permanent |
| **Last Update Timestamp** | Track when node info was last seen | Updated when new node info received |
| **Last Position (Latitude/Longitude)** | Track device location for position display and mapping | Updated when position broadcast received |
| **Position Accuracy** | GPS accuracy in meters | Updated with position data |
| **Position Timestamp** | When location was last updated | Updated with position data |
| **Gateway Association** | Which gateway last saw the device for routing optimization | Updated on message receipt |

**Storage Location**: SQLite database file (`tbot.db`)  
**Database Table**: `Devices`

**Important Notes**: 
- Only devices with "OK to MQTT" setting enabled broadcast their information to MQTT. TMesh cannot see or collect information from devices that have this setting disabled.
- **Public keys are pinned** on first registration and cannot be changed without re-registration. This prevents man-in-the-middle attacks.
- Position data is only collected if devices broadcast their location and is used for the `/position` command.
- Gateway associations are used for intelligent message routing in multi-gateway setups.

### 4. Analytics & Telemetry Data (OPTIONAL)

**NEW FEATURE**: TMesh can optionally collect network telemetry data in a separate PostgreSQL database.

**This feature is DISABLED by default** and must be explicitly enabled by configuring a PostgreSQL connection string.

When analytics are enabled, TMesh collects:

| Data Type | Purpose | Storage Duration |
|-----------|---------|------------------|
| **Device ID** | Link metrics to specific device | Indefinite (until manually deleted) |
| **Timestamp** | When the metric was recorded | Indefinite (until manually deleted) |
| **Device Position** (Latitude, Longitude) | Historical position tracking | Indefinite (until manually deleted) |
| **Position Update Time** | When position was last updated on device | Indefinite (until manually deleted) |
| **Position Accuracy** | GPS accuracy in meters | Indefinite (until manually deleted) |
| **Channel Utilization** | Percentage of channel usage | Indefinite (until manually deleted) |
| **Air Utilization** | Percentage of air time usage | Indefinite (until manually deleted) |

**Storage Location**: PostgreSQL database (separate from primary database)  
**Database Table**: `DeviceMetrics`  
**Indexing**: Indexed by device ID and timestamp for efficient queries

**What is NOT collected in analytics**:
- ❌ Message content
- ❌ Sender or recipient information
- ❌ Channel keys or encryption information
- ❌ Telegram user IDs or chat IDs
- ❌ Node names or identifying information beyond device ID
- ❌ Any personally identifiable information

**Privacy Considerations**:
- Analytics data is time-series telemetry for network optimization
- Device ID is included but no other identifying information
- Position history is maintained (latest position only in primary database)
- Data grows over time - implement retention policy as needed
- Can be completely disabled by not configuring PostgreSQL connection
- If enabled, consider GDPR compliance for position data
- Operator should document data retention and deletion policies

**Use Cases for Analytics**:
- Monitor mesh network health and coverage patterns
- Track device connectivity and reliability
- Analyze network congestion via channel/air utilization
- Identify optimal gateway placement
- Debug connectivity issues with historical data
- Optimize hop limits and routing decisions

**Disabling Analytics**:
- Leave `AnalyticsPostgresConnectionString` empty in configuration
- No PostgreSQL database needed if analytics disabled
- Historical data remains in PostgreSQL if previously enabled but won't grow

### 5. Temporary Data (In-Memory Only)

The following data is stored temporarily in memory and is NOT persisted to disk:

| Data Type | Purpose | Retention |
|-----------|---------|-----------|
| **Verification Codes** | Secure device and channel registration process | 5 minutes or until used |
| **Chat States** | Track registration workflow progress | 10 minutes of inactivity |
| **Message Queue** | Rate limiting and delivery management | Until message is sent |
| **Message Status** | Track delivery confirmation and provide status updates | A few minutes after delivery |
| **Device Public Key Cache** | Performance optimization | 1 hour |
| **Channel Key Cache** | Performance optimization for channel encryption | 1 hour |

This temporary data is **lost when the TMesh service restarts** and is never written to persistent storage.

---

## Data NOT Collected or Stored

TMesh explicitly does **NOT** collect or permanently store:

- ❌ **Message content** (Telegram or Meshtastic messages) - messages are processed in real-time only
- ❌ **Message history or logs** - no conversation archives
- ❌ **Historical position data** (unless analytics enabled) - only latest position per device in primary database
- ❌ **Channel membership lists** - no tracking of which devices are on which channels
- ❌ **Telegram phone numbers**
- ❌ **Telegram usernames**
- ❌ **Personal information** beyond user ID and chat ID
- ❌ **IP addresses or connection logs**
- ❌ **Cookies or tracking identifiers**
- ❌ **Device private keys** (only public keys stored)
- ❌ **MQTT credentials in database** (stored in configuration only)
- ❌ **Admin passwords in database** (stored in configuration only)
- ❌ **Trace route paths** (processed in real-time only)
- ❌ **Health monitoring data** beyond current status
- ❌ **Analytics message content** (if analytics enabled) - only metrics, never messages

---

## How Data is Used

### Device Registration Data
- **Purpose**: Enable bidirectional message routing between Telegram chats and Meshtastic devices
- **Processing**: When a message arrives from Telegram, TMesh looks up registered devices and sends encrypted messages to them. When a message arrives from a Meshtastic device, TMesh looks up registered chats and forwards the message.
- **Access**: Only accessible by the TMesh instance operator and the TMesh application itself

### Channel Registration Data
- **Purpose**: Enable bidirectional message routing between Telegram chats and Meshtastic private channels
- **Processing**: 
  - When a message arrives from Telegram, TMesh looks up registered channels and encrypts messages with channel PSK
  - Messages sent to all devices using that private channel
  - When a message arrives from a channel, TMesh decrypts using channel key and forwards to registered chats
- **Access**: Only accessible by the TMesh instance operator and the TMesh application itself
- **Security**: Channel keys (PSKs) must be protected - anyone with database access can decrypt channel messages

### Device Public Keys
- **Purpose**: Encrypt messages end-to-end using Meshtastic PKI (X25519 elliptic curve cryptography)
- **Processing**: Used to encrypt messages before sending to devices and to verify message authenticity
- **Security**: Public keys are, by design, public information broadcast on the Meshtastic network
- **Key Pinning**: Keys are pinned on first registration and validated on all subsequent messages to prevent MITM attacks

### Channel Encryption Keys
- **Purpose**: Encrypt and decrypt messages for private Meshtastic channels using AES encryption
- **Processing**: Used to encrypt outgoing messages to channel and decrypt incoming messages from channel
- **Security**: Keys are cryptographic secrets that must be protected
- **Storage**: Stored encrypted in SQLite database with appropriate file permissions
- **Risk**: Compromise of database means compromise of channel communications

### Device Position Data
- **Purpose**: Display device locations on maps and track device movement
- **Processing**: Automatically captured from Meshtastic position broadcasts when "OK to MQTT" is enabled
- **Usage**: Used by `/position` command to show device locations on Telegram maps
- **Access**: Only accessible to Telegram chats where the device is registered
- **Retention**: Latest position stored indefinitely in primary database, historical positions stored in analytics database if enabled

### Analytics & Telemetry Data
- **Purpose**: Monitor network health, optimize routing, analyze connectivity patterns
- **Processing**: 
  - Collected when analytics enabled via PostgreSQL connection string
  - Device metrics recorded with each position update or telemetry message
  - Time-series data stored with device ID and timestamp
  - No message content or identifying information beyond device ID
- **Usage**: 
  - Query historical device connectivity
  - Analyze channel and air utilization trends
  - Monitor network congestion patterns
  - Optimize gateway placement and hop limits
- **Access**: Only accessible by TMesh instance operator via PostgreSQL database
- **Privacy**: No personally identifiable information or message content
- **Retention**: Data stored indefinitely until operator implements deletion policy

### Gateway Association Data
- **Purpose**: Optimize message routing in multi-gateway deployments
- **Processing**: Tracks which gateway last saw each device or channel to route messages efficiently
- **Usage**: Automatically selects best gateway for message delivery and calculates dynamic hop limits
- **Retention**: Updated continuously as devices communicate through different gateways

### Temporary Verification Codes
- **Purpose**: Secure the device and channel registration process to ensure only the legitimate owner can register
- **Processing**: Generated randomly, sent to device/channel via encrypted mesh message, verified against user input in Telegram
- **Expiry**: 5 minutes, then automatically deleted from memory

---

## Data Sharing

TMesh does **NOT** share your data with third parties. However, please note:

### MQTT Broker
- All encrypted Meshtastic messages pass through your configured MQTT broker
- The MQTT broker operator may have access to encrypted message payloads (but cannot decrypt them without private keys or channel keys)
- MQTT credentials are configured by you and stored in TMesh configuration
- **Channel messages**: MQTT broker sees encrypted channel traffic but cannot decrypt without PSK

### Telegram
- Telegram receives messages sent by TMesh according to Telegram's Bot API
- Subject to [Telegram's Privacy Policy](https://telegram.org/privacy)
- TMesh only sends messages you explicitly configure it to send

### Meshtastic Network
- Encrypted messages are broadcast over the Meshtastic mesh network
- Anyone with a Meshtastic device can see encrypted packets (but cannot decrypt without the private key or channel key)
- **Device messages**: Encrypted with device public key (PKI)
- **Channel messages**: Encrypted with channel PSK
- Subject to Meshtastic network architecture and radio propagation

### PostgreSQL Database (Analytics)
- If analytics enabled, telemetry data stored in PostgreSQL
- Database typically on same server/network as TMesh
- Operator responsible for securing PostgreSQL access
- No data shared outside operator's infrastructure

---

## Data Security

### Encryption in Transit
- **Telegram ↔ TMesh**: HTTPS for webhook communication
- **TMesh ↔ MQTT Broker**: TLS/SSL encryption (recommended for production)
- **TMesh ↔ Meshtastic (Devices)**: PKI encryption using X25519 curve25519
- **TMesh ↔ Meshtastic (Channels)**: AES encryption using channel PSK

### Encryption at Rest
- SQLite database is stored on disk unencrypted by default
- **Channel encryption keys stored in database** - protect database file
- PostgreSQL analytics database - operator should enable encryption at rest
- Operators can implement disk encryption at the OS or container level
- Device public keys stored (not private keys)
- No sensitive authentication credentials stored in database (only config)

### Access Control
- Only the TMesh application has access to the databases
- Docker volumes can be configured with appropriate permissions
- Webhook endpoint protected by secret token validation
- Admin commands protected by password authentication
- Health monitoring endpoints are public but reveal no sensitive data
- **Database file permissions critical** - contains channel encryption keys

### Private Key Protection
- TMesh virtual node's private key is stored in configuration file only
- Configuration should be protected with appropriate file permissions (read-only for TMesh user)
- Private key never transmitted or logged
- Device private keys never stored or accessed by TMesh
- **Channel private keys (PSKs) stored in database** - different security model than device PKI

### Public Key Pinning (Devices)
- Device public keys are pinned on first registration
- Subsequent messages validated against pinned key
- Prevents man-in-the-middle attacks even if MQTT infrastructure compromised
- Key cannot be changed without device re-registration
- Pinning occurs automatically during verification code exchange

### Channel Key Security
- Channel encryption keys (PSKs) stored in SQLite database
- Keys validated during channel registration
- Same channel key must be used across all devices on channel
- **Anyone with database access can decrypt channel messages**
- Operators should:
  - Set restrictive file permissions on database
  - Encrypt backups at rest
  - Rotate channel keys periodically for sensitive operations
  - Use OS-level disk encryption
  - Limit access to database file

### TLS/SSL for MQTT
- Optional but strongly recommended for production
- Supports certificate validation or self-signed certificates
- Configurable via `MqttUseTls` and `MqttAllowUntrustedCertificates`
- Protects MQTT credentials and message metadata in transit
- Does not affect end-to-end message encryption (separate PKI/AES layer)

### Analytics Database Security
- PostgreSQL should use strong authentication
- Network access should be restricted (localhost or private network)
- Enable PostgreSQL TLS for remote connections
- Regular security updates for PostgreSQL
- Consider data encryption at rest
- Implement backup encryption

---

## Data Retention and Deletion

### Automatic Deletion
- Temporary verification codes: 5 minutes
- In-memory caches: 1-10 minutes or until restart
- Message queue: Cleared after sending
- Chat states: 10 minutes of inactivity

### Manual Deletion

**To remove a device registration:**
```bash
# Use /remove_device command in Telegram
/remove_device !75bcd15

# Or use /remove_device_from_all_chats to remove from all groups
/remove_device_from_all_chats !75bcd15

# Or manually delete from database
sqlite3 /path/to/tbot.db
DELETE FROM DeviceRegistrations WHERE DeviceId = YOUR_DEVICE_ID;
```

**To remove a channel registration:**
```bash
# Use /remove_channel command in Telegram
/remove_channel 5

# Or use /remove_channel_from_all_chats to remove from all groups
/remove_channel_from_all_chats 5

# Or manually delete from database
sqlite3 /path/to/tbot.db
DELETE FROM ChannelRegistrations WHERE ChannelId = YOUR_CHANNEL_ID;
DELETE FROM Channels WHERE Id = YOUR_CHANNEL_ID;
```

**To remove analytics data:**
```sql
-- Delete analytics for specific device
DELETE FROM DeviceMetrics WHERE DeviceId = YOUR_DEVICE_ID;

-- Delete analytics older than specific date
DELETE FROM DeviceMetrics WHERE Timestamp < '2025-01-01';

-- Delete all analytics data
TRUNCATE TABLE DeviceMetrics;
```

**To remove all data:**
```bash
# Stop TMesh
docker-compose down

# Delete primary database
rm data/tbot.db

# Delete analytics database (if using Docker)
docker volume rm tmesh_postgres-data

# Restart TMesh
docker-compose up -d
```

### Database Backup
- If you backup TMesh databases, ensure backups are stored securely
- **Backups contain channel encryption keys** - encrypt backups at rest
- Follow your data retention policies
- Analytics database may be large - consider retention policies
- Test restore procedures periodically

### Data Retention Policies
Operators should establish policies for:
- **Primary database**: Device and channel registrations, positions
- **Analytics database**: Time-series telemetry data
- Recommended retention periods:
  - Active registrations: Until unregistered
  - Device info: Until device removed or not seen for 90+ days
  - Analytics data: 30-90 days depending on use case
- Consider GDPR compliance if operating in EU
- Document policies for users

---

## GDPR Compliance (for EU Users)

If you operate TMesh in the EU or process data of EU citizens, you should be aware:

### Right to Access
Users can request to see what data TMesh stores about them by contacting the instance operator. This includes:
- Device registrations
- Channel registrations (including channel keys if they registered them)
- Analytics data (if enabled)
- Position history

### Right to Rectification
- Device registrations can be removed and re-added to update stored information
- Channel registrations can be removed and re-added with updated keys
- Position data updates automatically from device broadcasts

### Right to Erasure
- Users can request deletion of their registration data
- Use `/remove_device` or `/remove_channel` commands
- Contact operator for complete data deletion including analytics
- Operator should provide procedure for data deletion requests

### Right to Data Portability
- Registration data can be exported from SQLite database using standard SQL queries
- Analytics data can be exported from PostgreSQL
- Operators should provide export functionality on request

### Legal Basis for Processing
- Processing is necessary for the performance of contract (providing the bridging service)
- User consent obtained through active registration process
- Legitimate interest for analytics (network optimization)
- **Explicit consent required for analytics data collection**

### Special Categories of Data
- **Location data**: Considered sensitive under GDPR
- Only collected when device broadcasts position
- Stored both in primary DB (latest) and analytics DB (history) if enabled
- Users should be informed about position tracking
- Consider data minimization - only collect if needed

### Data Protection by Design
- Analytics disabled by default
- Minimal data collection principle
- Encryption in transit and at rest (operator's responsibility)
- Key pinning prevents MITM attacks
- No message content storage

---

## Meshtastic Network Considerations

### Public Nature of Mesh Networks
- Meshtastic is a radio mesh network where messages are broadcast over public airwaves
- Anyone with a Meshtastic device on the same channel can see encrypted message packets
- **Device messages**: TMesh uses PKI encryption, metadata (sender, receiver, packet size) visible
- **Channel messages**: Encrypted with channel PSK, anyone with PSK can decrypt
- "OK to MQTT" setting controls whether your device info is shared via MQTT

### Node Info Broadcasts
- Meshtastic devices periodically broadcast node information including device ID, name, and public key
- TMesh listens for these broadcasts and stores this information
- **To opt out**: Disable "OK to MQTT" in your device's LoRa settings - your device will not appear in TMesh's database

### Private Channel Privacy
- Private channels use shared encryption keys (PSK)
- All devices on channel can decrypt all messages
- Anyone who obtains the PSK can decrypt historical messages
- TMesh stores channel PSK to facilitate bridging
- Consider channel key rotation for sensitive operations
- Public channels (LongFast, etc.) cannot be registered to prevent spam

---

## Children's Privacy

TMesh does not specifically target or knowingly collect data from children under 13 (or applicable age in your jurisdiction). Parents/guardians should supervise children's use of the service.

- No age verification performed
- Operators should implement age-appropriate policies
- Consider restricting channel registration for minors
- Position tracking may be sensitive for children

---

## Changes to This Privacy Policy

This privacy policy may be updated periodically. Changes will be documented in the GitHub repository with version history. Continued use of TMesh after changes constitutes acceptance of the updated policy.

**Version History:**
- **2026-02-15**: Added private channel registration, analytics database, enhanced security details
- **2025-11-14**: Initial privacy policy for TMesh v1.0

---

## Data Protection for Self-Hosters

If you're hosting your own TMesh instance, you are responsible for:

- Securing the server/container environment
- Implementing appropriate access controls
- Protecting channel encryption keys in database
- Backing up data according to your needs (encrypt backups!)
- Complying with applicable privacy laws in your jurisdiction
- Informing your users about your instance's privacy practices
- Responding to user data requests
- Securing analytics database if enabled
- Implementing data retention policies
- Documenting what data you collect and why

### Recommended Practices
- Use TLS/SSL for MQTT connections
- Enable OS-level disk encryption (protects channel keys!)
- Restrict access to configuration files with private keys
- **Set restrictive permissions on SQLite database** (600 or 640)
- Regularly update TMesh to get security fixes
- Monitor logs for suspicious activity (but don't log message content)
- Implement backup procedures for databases (encrypt backups!)
- Use PostgreSQL authentication and network restrictions
- Document data retention and deletion procedures
- Inform users about analytics collection if enabled
- Consider GDPR implications of position data
- Implement data minimization principles
- Regular security audits of database access

### Security Checklist
- [ ] SQLite database file permissions set to 600
- [ ] Configuration file permissions set to 600
- [ ] MQTT connections use TLS/SSL
- [ ] PostgreSQL (if used) has strong password
- [ ] PostgreSQL (if used) restricts network access
- [ ] Database backups encrypted at rest
- [ ] OS-level disk encryption enabled
- [ ] Docker volumes have appropriate permissions
- [ ] Regular TMesh updates applied
- [ ] Monitoring for suspicious database access
- [ ] Data retention policy documented
- [ ] User privacy policy published
- [ ] GDPR compliance assessed (if applicable)

---

## Third-Party Services

TMesh relies on external services you configure:

- **MQTT Broker**: Subject to your MQTT provider's privacy policy
  - Sees encrypted message traffic
  - Has access to MQTT credentials
  - May log connection data
- **Telegram Bot API**: Subject to [Telegram's Privacy Policy](https://telegram.org/privacy)
  - Processes messages sent to/from Telegram
  - Subject to Telegram's data retention
- **Meshtastic Network**: Peer-to-peer mesh network with its own characteristics
  - Broadcasts encrypted packets over radio
  - Anyone can receive packets (but not decrypt without keys)
- **PostgreSQL** (if analytics enabled): 
  - Database software on your infrastructure
  - You control data retention and access
  - Subject to your security practices

---

## Contact Information

For privacy concerns related to a specific TMesh instance, contact the instance operator.

For questions about TMesh software itself:
- **GitHub Issues**: [TMesh Repository](https://github.com/samfromlv/tmesh/issues)
- **Discussions**: [GitHub Discussions](https://github.com/samfromlv/tmesh/discussions)
- **Community**: [Telegram Group](https://t.me/meshtastic_spb)

---

## Open Source Transparency

TMesh is open source software. You can:
- Review the source code to verify data handling: [GitHub Repository](https://github.com/samfromlv/tmesh)
- Audit what data is collected and how it's stored
- Verify encryption implementations
- Check database schemas and queries
- Review analytics data collection (if enabled)
- Modify the software to suit your privacy requirements
- Host your own instance with full control

---

## Consent

By using TMesh and registering a device or channel:
- You consent to the data collection and processing described in this policy
- You confirm you have authority to register the Meshtastic device or channel
- You understand messages will be forwarded between Telegram and Meshtastic
- You accept that the operator of your TMesh instance has access to registration data
- **For channels**: You understand channel encryption keys are stored in database
- **For analytics**: You consent to telemetry collection if analytics are enabled
- You understand position data may be tracked and stored
- You can withdraw consent by unregistering your device/channel or requesting deletion

You can withdraw consent by:
- Unregistering devices with `/remove_device` or `/remove_device_from_all_chats`
- Unregistering channels with `/remove_channel` or `/remove_channel_from_all_chats`
- Requesting complete data deletion from operator
- Disabling "OK to MQTT" on your Meshtastic device

---

## Privacy Summary

**What TMesh Collects:**
- ✅ Device and channel registration data (chat ID, user ID)
- ✅ Device information (node ID, public key, position)
- ✅ Channel information (name, encryption key, XOR hash)
- ✅ Latest device position (latitude, longitude, accuracy)
- ✅ Gateway associations for routing
- ✅ Analytics telemetry (optional, disabled by default)

**What TMesh Does NOT Collect:**
- ❌ Message content (never stored)
- ❌ Message history or logs
- ❌ Telegram phone numbers or usernames
- ❌ Personal information beyond user/chat IDs
- ❌ Historical positions (unless analytics enabled)
- ❌ Channel membership lists
- ❌ IP addresses or detailed connection logs

**Security Measures:**
- 🔒 PKI encryption for device messages (X25519)
- 🔒 AES encryption for channel messages (PSK)
- 🔒 Public key pinning prevents MITM attacks
- 🔒 TLS/SSL support for MQTT
- 🔒 Webhook security tokens
- 🔒 Admin password protection
- ⚠️  Channel keys stored in database (encrypt at rest!)

**Your Rights:**
- 📋 View your registration data
- ✏️  Update your registrations
- 🗑️  Delete your data (remove devices/channels)
- 📦 Export your data (on request)
- ⛔ Opt out (disable "OK to MQTT", unregister)

**Operator Responsibilities:**
- 🔐 Secure database access (especially channel keys)
- 💾 Implement data retention policies
- 📊 Document analytics collection (if enabled)
- 🔄 Regular security updates
- 📜 Inform users of practices
- 🇪🇺 GDPR compliance (if applicable)

---

**This privacy policy applies to the TMesh software. Individual instance operators may have additional privacy practices or requirements. Always check with your instance operator for their specific policies, especially regarding analytics collection and data retention.**

**If analytics are enabled on your instance, historical position data and telemetry metrics are collected. Contact your operator for details on analytics data retention and deletion procedures.**
