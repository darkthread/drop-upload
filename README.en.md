# Drop to Upload for Temporary Exchange

> 🌐 [繁體中文版本](README.md)

A lightweight temporary file exchange site built on ASP.NET Core Minimal API — supports drag-and-drop upload and automatic expiry deletion. Developed with .NET 10, runs on Windows, macOS, and Linux, and supports Docker.

---

## Configuration

Edit `appsettings.json`:

```json
{
  "AccessKeys": [
    { "Name": "username", "Key": "your-secret-key", "ClientIp": "*" }
  ],
  "FileCleanup": {
    "ExpireSeconds": 30
  }
}
```

### AccessKeys

| Field | Description |
|-------|-------------|
| `Name` | Display name for identification (appears in logs only) |
| `Key` | The key string entered by the user to connect |
| `ClientIp` | Restrict by source IP. Use `"*"` for no restriction; separate multiple IPs with `,` |

Multiple keys can be configured, each with its own IP restriction.

### FileCleanup

| Field | Description | Default |
|-------|-------------|---------|
| `ExpireSeconds` | Seconds after upload before a file is automatically deleted | `30` |

---

## Usage

### 1. Enter Access Key

Configure valid Access Keys in `appsettings.json` on the server side. On first visit or after the cookie expires, an input dialog will appear. Enter the `Key` value and click **Connect**.

Once authenticated, the key is stored as an `HttpOnly` cookie (valid for 30 days) — no re-entry needed on page refresh.

### 2. Upload a File

- **Drag and drop**: Drag a file onto the drop zone in the centre of the page and release.
- **Click to select**: Click the drop zone and choose a file via the file picker dialog.

Large files are automatically split into 1 MB chunks. Real-time upload progress is shown as a percentage.

### 3. Download a File

Click the file name in the file list to download it.

### 4. Delete a File

Click the **✖** button next to the file name to delete it manually.

### 5. Auto-delete Countdown

A countdown is shown next to each file:

| Colour | Meaning |
|--------|---------|
| Green | Plenty of time remaining |
| Yellow | Less than 60 seconds remaining |
| Red (blinking) | Less than 30 seconds remaining |
| Grey "Expiring" | Expired, awaiting background cleanup |

### 6. Language Switch

A **中文 / English** toggle is available in the top-right corner. All UI text updates instantly.

---

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/auth` | Set the Access Key cookie |
| `GET` | `/settings` | Retrieve settings such as `expireSeconds` |
| `GET` | `/files` | List all uploaded files |
| `POST` | `/upload-chunk` | Upload a single chunk |
| `GET` | `/download/?f={filename}` | Download a specific file |
| `POST` | `/delete/?f={filename}` | Delete a specific file |
| `GET` | `/events` | Server-Sent Events stream |

> All endpoints except `/auth` and static assets require Access Key authentication via the `X-Access-Key` cookie.

---

## SSE Events

| Event | Triggered When |
|-------|----------------|
| `connected` | Client successfully establishes the SSE connection |
| `fileUploaded` | A new file upload completes |
| `fileDeleted` | A file is manually deleted |
| `filesCleanedUp` | The background task removes expired files |

The client retries automatically on disconnection, up to 5 times.

---

## Logging

Logging is handled by [NLog](https://nlog-project.org/). Configuration file: `nlog.config`.  
Log files are written to the `logs/` directory.
