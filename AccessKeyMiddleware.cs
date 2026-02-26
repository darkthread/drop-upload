namespace DropUpload;

public class AccessKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AccessKeyMiddleware> _logger;
    private readonly List<AccessKeyConfig> _accessKeys;

    public AccessKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AccessKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _accessKeys = configuration.GetSection("AccessKeys").Get<List<AccessKeyConfig>>() ?? new List<AccessKeyConfig>();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 略過靜態檔案、首頁和 auth 端點
        var path = context.Request.Path.Value?.ToLower() ?? "";
        if (path.StartsWith("/css") || path.StartsWith("/js") || path == "/" || path == "/auth")
        {
            await _next(context);
            return;
        }

        // 檢查 Cookie 中的 X-Access-Key
        if (!context.Request.Cookies.TryGetValue("X-Access-Key", out var accessKey) || string.IsNullOrEmpty(accessKey))
        {
            _logger.LogWarning("Access denied: missing X-Access-Key cookie, source IP: {RemoteIp}", context.GetClientIp());
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = MultLangResources.GetText(TextId.AccessKeyRequired, MultLangResources.GetLang(context)) });
            return;
        }

        // 驗證 Access Key
        var clientIp = context.GetClientIp();
        var validKey = _accessKeys.FirstOrDefault(k => 
            k.Key == accessKey && 
            (k.ClientIp == "*" || k.ClientIp.Split(',').Contains(clientIp)));

        if (validKey == null)
        {
            _logger.LogWarning("Access denied: invalid Access Key, source IP: {RemoteIp}", clientIp);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = MultLangResources.GetText(TextId.InvalidAccessKey, MultLangResources.GetLang(context)) });
            return;
        }

        _logger.LogDebug("Access Key validation successful: {KeyName}, source IP: {RemoteIp}", validKey.Name, clientIp);
        await _next(context);
    }
}

public class AccessKeyConfig
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string ClientIp { get; set; } = "*";
}
