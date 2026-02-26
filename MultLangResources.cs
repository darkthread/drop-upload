using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DropUpload
{
    public enum TextId
    {
        AccessKeyRequired,
        InvalidAccessKey,
        FileUploaded,
        FileDeleted,
        FileNotFound,
        ServerError,
        InvalidRequest,
        MissingKeyParam,
        AccessKeySet
    }

    public class MultLangResources
    {
        private static readonly Dictionary<TextId, Dictionary<string, string>> _resources = new()
        {
            { TextId.AccessKeyRequired,  new() { { "zh", "需要 Access Key" },       { "en", "Access Key required" } } },
            { TextId.InvalidAccessKey,   new() { { "zh", "無效的 Access Key" },      { "en", "Invalid Access Key" } } },
            { TextId.FileUploaded,       new() { { "zh", "檔案已上傳" },             { "en", "File uploaded" } } },
            { TextId.FileDeleted,        new() { { "zh", "檔案已刪除" },             { "en", "File deleted" } } },
            { TextId.FileNotFound,       new() { { "zh", "找不到檔案" },             { "en", "File not found" } } },
            { TextId.ServerError,        new() { { "zh", "伺服器錯誤" },             { "en", "Server error" } } },
            { TextId.InvalidRequest,     new() { { "zh", "無效的請求格式" },          { "en", "Invalid request format" } } },
            { TextId.MissingKeyParam,    new() { { "zh", "缺少 key 參數" },          { "en", "Missing key parameter" } } },
            { TextId.AccessKeySet,       new() { { "zh", "Access Key 已設定" },      { "en", "Access Key set" } } }
        };

        public static string GetLang(HttpContext context)
        {
            var accept = context.Request.Headers.AcceptLanguage.ToString();
            return accept.Contains("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        }

        public static string GetText(TextId textId, string lang)
        {
            if (_resources.TryGetValue(textId, out var translations) && translations.TryGetValue(lang, out var text))
                return text;
            return string.Empty;
        }
    }
}