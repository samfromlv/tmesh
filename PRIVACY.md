# Privacy Policy for TMesh

**Last Updated: November 14, 2025**

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

### 2. Meshtastic Device Information

TMesh automatically collects device information from Meshtastic node info broadcasts:

| Data Type | Purpose | Storage Duration |
|-----------|---------|------------------|
| **Device Node ID** | Unique identifier for Meshtastic devices | Updated when new node info received |
| **Node Name** | Display name of the Meshtastic device | Updated when new node info received |
| **Public Key (32 bytes)** | Enable PKI encryption for secure messaging | Updated when new node info received |
| **Last Update Timestamp** | Track when node info was last seen | Updated when new node info received |

**Storage Location**: SQLite database file (`tbot.db`)  
**Database Table**: `Devices`

**Important**: Only devices with "OK to MQTT" setting enabled broadcast their information to MQTT. TMesh cannot see or collect information from devices that have this setting disabled.

### 3. Temporary Data (In-Memory Only)

The following data is stored temporarily in memory and is NOT persisted to disk:

| Data Type | Purpose | Retention |
|-----------|---------|-----------|
| **Verification Codes** | Secure device registration process | 5 minutes or until used |
| **Chat States** | Track registration workflow progress | 10 minutes of inactivity |
| **Message Queue** | Rate limiting and delivery management | Until message is sent |
| **Message Status** | Track delivery confirmation and provide status updates | A few minutes after delivery |
| **Device Public Key Cache** | Performance optimization | 1 hour |

This temporary data is **lost when the TMesh service restarts** and is never written to persistent storage.

---

## Data NOT Collected or Stored

TMesh explicitly does **NOT** collect or permanently store:

- ❌ Message content (Telegram or Meshtastic messages)
- ❌ Message history or logs
- ❌ Meshtastic location data
- ❌ Telegram phone numbers
- ❌ Telegram usernames
- ❌ Personal information beyond user ID and chat ID
- ❌ IP addresses or connection logs
- ❌ Analytics or usage statistics
- ❌ Cookies or tracking identifiers

---

## How Data is Used

### Device Registration Data
- **Purpose**: Enable bidirectional message routing between Telegram chats and Meshtastic devices
- **Processing**: When a message arrives from Telegram, TMesh looks up registered devices and sends encrypted messages to them. When a message arrives from a Meshtastic device, TMesh looks up registered chats and forwards the message.
- **Access**: Only accessible by the TMesh instance operator and the TMesh application itself

### Device Public Keys
- **Purpose**: Encrypt messages end-to-end using Meshtastic PKI (X25519 elliptic curve cryptography)
- **Processing**: Used to encrypt messages before sending to devices and to verify message authenticity
- **Security**: Public keys are, by design, public information broadcast on the Meshtastic network

### Temporary Verification Codes
- **Purpose**: Secure the device registration process to ensure only the legitimate device owner can register
- **Processing**: Generated randomly, sent to device via encrypted mesh message, verified against user input in Telegram
- **Expiry**: 5 minutes, then automatically deleted from memory

---

## Data Sharing

TMesh does **NOT** share your data with third parties. However, please note:

### MQTT Broker
- All encrypted Meshtastic messages pass through your configured MQTT broker
- The MQTT broker operator may have access to encrypted message payloads (but cannot decrypt them without private keys)
- MQTT credentials are configured by you and stored in TMesh configuration

### Telegram
- Telegram receives messages sent by TMesh according to Telegram's Bot API
- Subject to [Telegram's Privacy Policy](https://telegram.org/privacy)
- TMesh only sends messages you explicitly configure it to send

### Meshtastic Network
- Encrypted messages are broadcast over the Meshtastic mesh network
- Anyone with a Meshtastic device can see encrypted packets (but cannot decrypt without the private key)
- Subject to Meshtastic network architecture and radio propagation

---

## Data Security

### Encryption in Transit
- **Telegram ↔ TMesh**: HTTPS for webhook communication
- **TMesh ↔ MQTT Broker**: Can be configured to use TLS/SSL
- **TMesh ↔ Meshtastic**: PKI encryption using X25519 curve25519

### Encryption at Rest
- SQLite database is stored on disk unencrypted by default
- Operators can implement disk encryption at the OS or container level
- No sensitive cryptographic secrets stored in database (only public keys)

### Access Control
- Only the TMesh application has access to the database
- Docker volumes can be configured with appropriate permissions
- Webhook endpoint protected by secret token validation

### Private Key Protection
- TMesh virtual node's private key is stored in configuration file
- Configuration should be protected with appropriate file permissions
- Private key never transmitted or logged

---

## Data Retention and Deletion

### Automatic Deletion
- Temporary verification codes: 5 minutes
- In-memory caches: 1-10 minutes or until restart
- Message queue: Cleared after sending

### Manual Deletion

**To remove a device registration:**
```bash
# Use /remove command (when implemented)
# Or manually delete from database
sqlite3 /path/to/tbot.db
DELETE FROM DeviceRegistrations WHERE DeviceId = YOUR_DEVICE_ID;
```

**To remove all data:**
```bash
# Stop TMesh
docker-compose down

# Delete database
rm data/tbot.db

# Restart TMesh
docker-compose up -d
```

### Database Backup
If you backup the TMesh database, ensure backups are stored securely and follow your data retention policies.

---

## GDPR Compliance (for EU Users)

If you operate TMesh in the EU or process data of EU citizens, you should be aware:

### Right to Access
Users can request to see what data TMesh stores about them by contacting the instance operator.

### Right to Rectification
Device registrations can be removed and re-added to update stored information.

### Right to Erasure
Users can request deletion of their registration data. Use the `/remove` command (when implemented) or contact the operator.

### Right to Data Portability
Registration data can be exported from the SQLite database using standard SQL queries.

### Legal Basis for Processing
- Processing is necessary for the performance of contract (providing the bridging service)
- User consent obtained through active registration process

---

## Meshtastic Network Considerations

### Public Nature of Mesh Networks
- Meshtastic is a radio mesh network where messages are broadcast over public airwaves
- Anyone with a Meshtastic device on the same channel can see encrypted message packets
- TMesh uses PKI encryption, but metadata (sender, receiver, packet size) is visible on the network
- "OK to MQTT" setting controls whether your device info is shared via MQTT

### Node Info Broadcasts
- Meshtastic devices periodically broadcast node information including device ID, name, and public key
- TMesh listens for these broadcasts and stores this information
- **To opt out**: Disable "OK to MQTT" in your device's LoRa settings - your device will not appear in TMesh's database

---

## Children's Privacy

TMesh does not specifically target or knowingly collect data from children under 13 (or applicable age in your jurisdiction). Parents/guardians should supervise children's use of the service.

---

## Changes to This Privacy Policy

This privacy policy may be updated periodically. Changes will be documented in the GitHub repository with version history. Continued use of TMesh after changes constitutes acceptance of the updated policy.

---

## Data Protection for Self-Hosters

If you're hosting your own TMesh instance, you are responsible for:

- Securing the server/container environment
- Implementing appropriate access controls
- Backing up data according to your needs
- Complying with applicable privacy laws in your jurisdiction
- Informing your users about your instance's privacy practices
- Responding to user data requests

### Recommended Practices
- Use TLS/SSL for MQTT connections
- Enable OS-level disk encryption
- Restrict access to configuration files with private keys
- Regularly update TMesh to get security fixes
- Monitor logs for suspicious activity (but don't log message content)
- Implement backup procedures for the database

---

## Third-Party Services

TMesh relies on external services you configure:

- **MQTT Broker**: Subject to your MQTT provider's privacy policy
- **Telegram Bot API**: Subject to [Telegram's Privacy Policy](https://telegram.org/privacy)
- **Meshtastic Network**: Peer-to-peer mesh network with its own characteristics

---

## Contact Information

For privacy concerns related to a specific TMesh instance, contact the instance operator.

For questions about TMesh software itself:
- **GitHub Issues**: [TMesh Repository](https://github.com/yourusername/TMesh/issues)
- **Email**: privacy@yourdomain.com (for instance operators to customize)

---

## Open Source Transparency

TMesh is open source software. You can:
- Review the source code to verify data handling: [GitHub Repository](https://github.com/yourusername/TMesh)
- Audit what data is collected and how it's stored
- Modify the software to suit your privacy requirements
- Host your own instance with full control

---

## Consent

By using TMesh and registering a device:
- You consent to the data collection and processing described in this policy
- You confirm you have authority to register the Meshtastic device
- You understand messages will be forwarded between Telegram and Meshtastic
- You accept that the operator of your TMesh instance has access to registration data

You can withdraw consent by unregistering your device or requesting deletion of your data.

---

**This privacy policy applies to the TMesh software. Individual instance operators may have additional privacy practices or requirements.**
