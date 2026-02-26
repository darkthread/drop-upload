using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DropUpload
{
    public static class ExtMethods
    {
        public static string GetClientIp(this HttpContext context)
        {
            // 嘗試從 X-Forwarded-For 標頭獲取原始客戶端 IP（如果有代理）
            var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(xForwardedFor))
            {
                // X-Forwarded-For 可能包含多個 IP，取第一個
                return xForwardedFor.Split(',').First().Trim();
            }

            // 如果沒有 X-Forwarded-For，則使用 RemoteIpAddress
            return context.Connection.RemoteIpAddress?.ToString() ?? "?";
        }
    }
}