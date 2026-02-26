# 拖檔上傳，暫存交換 / Drop to Upload for Temporary Exchange

> 🌐 [English version](README.en.md)

基於 ASP.NET Core Minimal API 輕量級暫時檔案交換網站 — 支援拖放檔案上傳、自動過期刪除。使用 .NET 10 開發，可在 Windows、macOS、Linux 編譯及運行，支援 Docker 容器執行。

---

## 設定 Configuration

編輯 `appsettings.json`：

```json
{
  "AccessKeys": [
    { "Name": "使用者名稱", "Key": "自訂金鑰字串", "ClientIp": "*" }
  ],
  "FileCleanup": {
    "ExpireSeconds": 30
  }
}
```

### AccessKeys

| 欄位 | 說明 |
|------|------|
| `Name` | 識別用名稱（僅出現於日誌） |
| `Key` | 連線時輸入的金鑰字串 |
| `ClientIp` | 限制來源 IP，`"*"` 表示不限制，多 IP 時使用 , 分隔 |

可設定多組金鑰，每組可指定不同的來源 IP 限制。

### FileCleanup

| 欄位 | 說明 | 預設值 |
|------|------|--------|
| `ExpireSeconds` | 檔案上傳後幾秒自動刪除 | `30` |

---

## 使用方式 Usage

### 1. 輸入 Access Key

先在伺服器端 appsettings.json 設好可用 Access Key，首次開啟或 Cookie 過期後，頁面會彈出輸入框，請輸入 `appsettings.json` 中設定的 `Key` 值後點擊「連線 / Connect」。

認證成功後，金鑰會以 `HttpOnly Cookie` 儲存（有效期 30 天），之後重新整理頁面不需再次輸入。

### 2. 上傳檔案

- **拖放**：將檔案拖曳至頁面中央的拖放區後放開。
- **點擊選取**：點擊拖放區，透過檔案選擇對話框選取檔案。

大型檔案會自動分塊（每塊 1 MB）上傳，頁面顯示即時進度百分比。

### 3. 下載檔案

點擊檔案清單中的檔案名稱即可下載。

### 4. 刪除檔案

點擊檔案名稱右側的 **✖** 按鈕手動刪除。

### 5. 自動刪除倒數

每個檔案旁顯示剩餘時間倒數：

| 顏色 | 意義 |
|------|------|
| 綠色 | 時間充裕 |
| 黃色 | 剩餘不足 60 秒 |
| 紅色閃爍 | 剩餘不足 30 秒 |
| 灰色「即將刪除 / Expiring」 | 已到期，等待背景清理 |

### 6. 語言切換

頁面右上角提供 **中文 / English** 切換，所有介面文字即時更新。

---

## API 端點 Endpoints

| 方法 | 路徑 | 說明 |
|------|------|------|
| `POST` | `/auth` | 設定 Access Key Cookie |
| `GET` | `/settings` | 取得 `expireSeconds` 等設定 |
| `GET` | `/files` | 列出所有已上傳檔案 |
| `POST` | `/upload-chunk` | 上傳單一分塊 |
| `GET` | `/download/?f={filename}` | 下載指定檔案 |
| `POST` | `/delete/?f={filename}` | 刪除指定檔案 |
| `GET` | `/events` | Server-Sent Events 即時通知 |

> 除 `/auth` 和靜態資源外，所有端點須通過 Access Key 驗證（`X-Access-Key` Cookie）。

---

## 即時通知 SSE Events

| 事件名稱 | 觸發時機 |
|----------|----------|
| `connected` | 客戶端成功建立 SSE 連線 |
| `fileUploaded` | 有新檔案上傳完成 |
| `fileDeleted` | 有檔案被手動刪除 |
| `filesCleanedUp` | 背景任務清除過期檔案 |

連線中斷時前端會自動重試，最多重試 5 次。

---

## 日誌 Logging

日誌由 [NLog](https://nlog-project.org/) 輸出，設定檔為 `nlog.config`。  
日誌檔存放於 `logs/` 目錄。
