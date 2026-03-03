using System.Collections.Concurrent;
using System.Text;
using NLog;
using NLog.Web;
using DropUpload;
using Microsoft.AspNetCore.Mvc;

var logger = LogManager.Setup().LoadConfigurationFromFile("nlog.config").GetLogger("FileHub");

var builder = WebApplication.CreateBuilder(args);
// builder.Logging.ClearProviders();
// builder.Host.UseNLog();
var app = builder.Build();

app.UseFileServer();

// 加入 Access Key 驗證中介軟體
app.UseMiddleware<AccessKeyMiddleware>();

var dataPath = Path.Combine(app.Environment.ContentRootPath, "Data");
var tempPath = Path.Combine(dataPath, "temp");
if (!Directory.Exists(dataPath))
{
    Directory.CreateDirectory(dataPath);
}
Directory.CreateDirectory(tempPath);

// 讀取過期時間設定（秒）
var expireSeconds = app.Configuration.GetValue<int>("FileCleanup:ExpireSeconds", 60);

// SSE 連線管理
var sseClients = new ConcurrentDictionary<HttpResponse, byte>();

async Task BroadcastSseMessage(string eventType, string data)
{
    var message = $"event: {eventType}\ndata: {data}\n\n";
    var messageBytes = Encoding.UTF8.GetBytes(message);

    var toRemove = new List<HttpResponse>();

    foreach (var client in sseClients.Keys)
    {
        try
        {
            await client.Body.WriteAsync(messageBytes);
            await client.Body.FlushAsync();
        }
        catch
        {
            logger.Warn($"Failed to send SSE message to client {client.HttpContext.Connection.RemoteIpAddress}, marking for removal");
            toRemove.Add(client);
        }
    }

    // 移除斷線的客戶端
    foreach (var client in toRemove)
    {
        sseClients.TryRemove(client, out _);
    }
}

app.MapGet("/settings", () =>
{
    return Results.Ok(new
    {
        expireSeconds,
        appBaseUrl = app.Configuration["app:BaseUrl"] ?? ""
    });
});

app.MapPost("/auth", async (HttpContext context) =>
{
    var lang = MultLangResources.GetLang(context);
    if (!context.Request.HasFormContentType)
    {
        return Results.BadRequest(new { error = MultLangResources.GetText(TextId.InvalidRequest, lang) });
    }

    var form = await context.Request.ReadFormAsync();
    var accessKey = form["key"].ToString();

    if (string.IsNullOrEmpty(accessKey))
    {
        return Results.BadRequest(new { error = MultLangResources.GetText(TextId.MissingKeyParam, lang) });
    }

    // 設定 Cookie
    context.Response.Cookies.Append("X-Access-Key", accessKey, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Expires = DateTimeOffset.UtcNow.AddDays(30)
    });

    logger.Info("X-Access-Key cookie set, source IP: {RemoteIp}", context.Connection.RemoteIpAddress);
    return Results.Ok(new { success = true, message = MultLangResources.GetText(TextId.AccessKeySet, MultLangResources.GetLang(context)) });
});

app.MapGet("/events", async (HttpContext context, IHostApplicationLifetime lifetime) =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    sseClients.TryAdd(context.Response, 0);

    // 發送連線成功訊息
    var connectMessage = Encoding.UTF8.GetBytes("event: connected\ndata: {\"status\":\"connected\"}\n\n");
    await context.Response.Body.WriteAsync(connectMessage);
    await context.Response.Body.FlushAsync();

    // 建立一個 linked cancellation token，當連線中斷或 App 停止時觸發
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted,
        lifetime.ApplicationStopping);

    // 連線中斷或 App 停止時，從列表中移除客戶端
    linkedCts.Token.Register(() => sseClients.TryRemove(context.Response, out _));

    // 連線中斷或 App 停止時，結束執行
    var tcs = new TaskCompletionSource();
    linkedCts.Token.Register(() => tcs.TrySetResult());
    await tcs.Task;
});

app.MapGet("/files", () =>
{
    var files = Directory.GetFiles(dataPath)
        .Select(f => new
        {
            name = Path.GetFileName(f),
            size = new FileInfo(f).Length,
            modified = new FileInfo(f).LastWriteTime
        })
        .ToArray();
    return Results.Ok(new { files });
});

app.MapPost("/upload-chunk", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Invalid content type" });

    var form = await request.ReadFormAsync();
    var chunkData = form.Files.FirstOrDefault();
    var fileName = Path.GetFileName(form["fileName"].ToString());
    var uploadId = Path.GetFileName(form["uploadId"].ToString());

    if (!long.TryParse(form["pos"], out var pos) ||
        !long.TryParse(form["size"], out var totalSize))
        return Results.BadRequest(new { error = "Invalid pos/size parameters" });

    if (chunkData == null || string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(uploadId))
        return Results.BadRequest(new { error = "Missing required fields" });

    var tempFile = Path.Combine(tempPath, uploadId + ".tmp");
    var finalPath = Path.Combine(dataPath, fileName);

    // 驗證目前長度與 pos 相符，然後在其後方繼續寫入
    using (var stream = new FileStream(tempFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
    {
        if (stream.Length != pos)
            return Results.BadRequest(new { error = $"Position mismatch: expected {pos}, got {stream.Length}" });

        stream.Seek(0, SeekOrigin.End);
        await chunkData.CopyToAsync(stream);
    }

    bool complete = pos + chunkData.Length >= totalSize;

    // 全部完成，複製到正式位置
    if (complete)
    {
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Copy(tempFile, finalPath);
        File.Delete(tempFile);

        var fileSize = new FileInfo(finalPath).Length;
        await BroadcastSseMessage("fileUploaded", $"{{\"fileName\":\"{fileName}\"}}");
        logger.Info($"[{request.HttpContext.GetClientIp()}] File uploaded (chunked): {fileName} ({fileSize} bytes)");

        return Results.Ok(new { success = true, fileName, size = fileSize, complete = true });
    }

    return Results.Ok(new { success = true, complete = false });
});

app.MapGet("/download/", ([FromQuery(Name = "f")] string filename, HttpRequest request) =>
{
    var safeFileName = Path.GetFileName(filename); // Ensure only the file name is used to prevent path traversal attacks
    var filePath = Path.Combine(dataPath, safeFileName);
    if (!File.Exists(filePath))
    {
        return Results.NotFound(new { error = MultLangResources.GetText(TextId.FileNotFound, MultLangResources.GetLang(request.HttpContext)) });
    }

    var fileBytes = File.ReadAllBytes(filePath);
    var contentType = "application/octet-stream";

    logger.Info($"[{request.HttpContext.GetClientIp()}] File downloaded: {safeFileName} ({fileBytes.Length} bytes)");

    return Results.File(fileBytes, contentType, safeFileName);
});

app.MapPost("/delete/", async ([FromQuery(Name = "f")] string filename, HttpRequest request) =>
{
    var safeFileName = Path.GetFileName(filename); // 確保只使用檔名，避免路徑穿越攻擊
    var filePath = Path.Combine(dataPath, safeFileName);

    if (!File.Exists(filePath))
    {
        return Results.NotFound(new { error = MultLangResources.GetText(TextId.FileNotFound, MultLangResources.GetLang(request.HttpContext)) });
    }

    try
    {
        File.Delete(filePath);

        // 廣播檔案刪除通知
        await BroadcastSseMessage("fileDeleted", $"{{\"fileName\":\"{safeFileName}\"}}");

        logger.Info($"[{request.HttpContext.GetClientIp()}] File deleted: {safeFileName}");
        return Results.Ok(new { success = true, fileName = safeFileName });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

// Background task to periodically clean up old files
var cleanupTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
_ = Task.Run(async () =>
{
    while (await cleanupTimer.WaitForNextTickAsync())
    {
        try
        {
            if (Directory.Exists(dataPath))
            {
                var files = Directory.GetFiles(dataPath);
                var cutoffTime = DateTime.Now.AddSeconds(-expireSeconds);
                var deletedCount = 0;

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTime < cutoffTime)
                    {
                        try
                        {
                            File.Delete(file);
                            deletedCount++;
                            logger.Info("Expired file deleted: {FileName}", Path.GetFileName(file));
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Failed to delete file: {FileName}", Path.GetFileName(file));
                        }
                    }
                }

                if (deletedCount > 0)
                {
                    logger.Info("Deleted {DeletedCount} expired files", deletedCount);
                    await BroadcastSseMessage("filesCleanedUp", $"{{\"count\":{deletedCount}}}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Cleanup task encountered an error");
        }
    }
});

app.Run();



