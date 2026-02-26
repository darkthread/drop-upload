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
if (!Directory.Exists(dataPath))
{
    Directory.CreateDirectory(dataPath);
}

// 讀取過期時間設定（秒）
var expireSeconds = app.Configuration.GetValue<int>("FileCleanup:ExpireSeconds", 60);

// SSE 連線管理
var sseClients = new ConcurrentBag<HttpResponse>();

async Task BroadcastSseMessage(string eventType, string data)
{
    var message = $"event: {eventType}\ndata: {data}\n\n";
    var messageBytes = Encoding.UTF8.GetBytes(message);

    var toRemove = new List<HttpResponse>();

    foreach (var client in sseClients)
    {
        try
        {
            await client.Body.WriteAsync(messageBytes);
            await client.Body.FlushAsync();
        }
        catch
        {
            toRemove.Add(client);
        }
    }

    // 移除斷線的客戶端
    foreach (var client in toRemove)
    {
        sseClients.TryTake(out _);
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

    sseClients.Add(context.Response);

    // 發送連線成功訊息
    var connectMessage = Encoding.UTF8.GetBytes("event: connected\ndata: {\"status\":\"connected\"}\n\n");
    await context.Response.Body.WriteAsync(connectMessage);
    await context.Response.Body.FlushAsync();

    // 建立一個 linked cancellation token，當連線中斷或 App 停止時觸發
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted,
        lifetime.ApplicationStopping);

    // 連線中斷或 App 停止時，從列表中移除客戶端
    linkedCts.Token.Register(() => sseClients.TryTake(out _));

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

app.MapPost("/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Invalid content type" });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();

    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "No file uploaded" });
    }

    var safeFileName = Path.GetFileName(file.FileName);

    var filePath = Path.Combine(dataPath, safeFileName);

    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    // 廣播檔案上傳通知
    await BroadcastSseMessage("fileUploaded", $"{{\"fileName\":\"{safeFileName}\"}}");

    logger.Info($"[{request.HttpContext.GetClientIp()}] File uploaded: {safeFileName} ({file.Length} bytes)");

    return Results.Ok(new
    {
        success = true,
        fileName = safeFileName,
        size = file.Length
    });
});

app.MapPost("/upload-chunk", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Invalid content type" });

    var form = await request.ReadFormAsync();
    var chunkData = form.Files.FirstOrDefault();
    var fileName = Path.GetFileName(form["fileName"].ToString());
    var uploadId = form["uploadId"].ToString();

    if (!int.TryParse(form["chunkIndex"], out var chunkIndex) ||
        !int.TryParse(form["totalChunks"], out var totalChunks))
        return Results.BadRequest(new { error = "Invalid chunk parameters" });

    if (chunkData == null || string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(uploadId))
        return Results.BadRequest(new { error = "Missing required fields" });

    var chunkDir = Path.Combine(dataPath, "_chunks", uploadId);
    Directory.CreateDirectory(chunkDir);

    var chunkPath = Path.Combine(chunkDir, $"{chunkIndex:D6}");
    using (var stream = new FileStream(chunkPath, FileMode.Create))
        await chunkData.CopyToAsync(stream);

    // 全部 Chunk 收到後合併
    if (Directory.GetFiles(chunkDir).Length == totalChunks)
    {
        var finalPath = Path.Combine(dataPath, fileName);
        using (var finalStream = new FileStream(finalPath, FileMode.Create))
        {
            for (int i = 0; i < totalChunks; i++)
            {
                var cp = Path.Combine(chunkDir, $"{i:D6}");
                using var cs = new FileStream(cp, FileMode.Open);
                await cs.CopyToAsync(finalStream);
            }
        }
        Directory.Delete(chunkDir, true);

        var fileSize = new FileInfo(finalPath).Length;
        await BroadcastSseMessage("fileUploaded", $"{{\"fileName\":\"{fileName}\"}}");
        logger.Info($"[{request.HttpContext.GetClientIp()}] File uploaded (chunked): {fileName} ({fileSize} bytes)");

        return Results.Ok(new { success = true, fileName, size = fileSize, complete = true });
    }

    return Results.Ok(new { success = true, chunkIndex, complete = false });
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



