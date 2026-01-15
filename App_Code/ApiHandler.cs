using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace AshxApi
{
    public class ApiHandler : IHttpHandler
    {
        private static readonly object SettingsLock = new object();
        private static Settings CachedSettings;
        private static DateTime LastSettingsReadUtc = DateTime.MinValue;
        private static readonly TimeSpan SettingsCacheDuration = TimeSpan.FromSeconds(10);

        public bool IsReusable => true;

        public void ProcessRequest(HttpContext context)
        {
            if (context == null)
            {
                return;
            }

            context.Response.ContentType = "application/json";

            try
            {
                var route = ParseRoute(context.Request.Path);
                if (route == null)
                {
                    WriteError(context, 400, "Invalid route. Expected /api/{sender}/{receiver}/{endpoint}.");
                    return;
                }

                var settings = LoadSettings(context);
                var connection = settings.Connections.FirstOrDefault(item =>
                    string.Equals(item.Sender, route.Sender, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Receiver, route.Receiver, StringComparison.OrdinalIgnoreCase));

                if (connection == null)
                {
                    WriteError(context, 404, "Connection not found for the supplied sender/receiver.");
                    return;
                }

                var fileData = ReadIncomingFile(context);
                if (fileData == null)
                {
                    WriteError(context, 400, "No file or request body provided.");
                    return;
                }

                var outputDirectory = Path.Combine(
                    connection.BaseOutputPath,
                    route.Sender,
                    route.Receiver,
                    route.Endpoint);

                Directory.CreateDirectory(outputDirectory);

                var outputPath = BuildOutputPath(outputDirectory, fileData.FileName);
                WriteXmlPayload(outputPath, route, fileData);

                WriteSuccess(context, outputPath);
            }
            catch (Exception ex)
            {
                WriteError(context, 500, "Unexpected error: " + ex.Message);
            }
        }

        private static RouteInfo ParseRoute(string requestPath)
        {
            if (string.IsNullOrWhiteSpace(requestPath))
            {
                return null;
            }

            var apiIndex = requestPath.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            if (apiIndex < 0)
            {
                return null;
            }

            var remainder = requestPath.Substring(apiIndex + 5).Trim('/');
            var segments = remainder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length != 3)
            {
                return null;
            }

            return new RouteInfo
            {
                Sender = segments[0],
                Receiver = segments[1],
                Endpoint = segments[2]
            };
        }

        private static Settings LoadSettings(HttpContext context)
        {
            if (CachedSettings != null && DateTime.UtcNow - LastSettingsReadUtc < SettingsCacheDuration)
            {
                return CachedSettings;
            }

            lock (SettingsLock)
            {
                if (CachedSettings != null && DateTime.UtcNow - LastSettingsReadUtc < SettingsCacheDuration)
                {
                    return CachedSettings;
                }

                var settingsPath = context.Server.MapPath("~/appsettings.json");
                if (!File.Exists(settingsPath))
                {
                    CachedSettings = new Settings();
                    LastSettingsReadUtc = DateTime.UtcNow;
                    return CachedSettings;
                }

                var json = File.ReadAllText(settingsPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var loaded = serializer.Deserialize<Settings>(json) ?? new Settings();
                loaded.Normalize();

                CachedSettings = loaded;
                LastSettingsReadUtc = DateTime.UtcNow;
                return CachedSettings;
            }
        }

        private static FilePayload ReadIncomingFile(HttpContext context)
        {
            if (context.Request.Files.Count > 0)
            {
                var postedFile = context.Request.Files[0];
                using (var memory = new MemoryStream())
                {
                    postedFile.InputStream.CopyTo(memory);
                    return new FilePayload
                    {
                        FileName = Path.GetFileName(postedFile.FileName),
                        ContentType = postedFile.ContentType,
                        Content = memory.ToArray()
                    };
                }
            }

            using (var memory = new MemoryStream())
            {
                context.Request.InputStream.CopyTo(memory);
                var data = memory.ToArray();
                if (data.Length == 0)
                {
                    return null;
                }

                return new FilePayload
                {
                    FileName = "payload.bin",
                    ContentType = context.Request.ContentType,
                    Content = data
                };
            }
        }

        private static string BuildOutputPath(string outputDirectory, string fileName)
        {
            var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "payload";
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
            var outputFileName = string.Format(CultureInfo.InvariantCulture, "{0}_{1}.xml", safeName, timestamp);
            return Path.Combine(outputDirectory, outputFileName);
        }

        private static string SanitizeFileName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var cleaned = new string(input.Where(ch => !invalidChars.Contains(ch)).ToArray());
            return cleaned.Trim();
        }

        private static void WriteXmlPayload(string outputPath, RouteInfo route, FilePayload fileData)
        {
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("payload",
                    new XElement("sender", route.Sender),
                    new XElement("receiver", route.Receiver),
                    new XElement("endpoint", route.Endpoint),
                    new XElement("receivedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                    new XElement("fileName", fileData.FileName ?? string.Empty),
                    new XElement("contentType", fileData.ContentType ?? string.Empty),
                    new XElement("contentBase64", Convert.ToBase64String(fileData.Content))
                ));

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true
            };

            using (var writer = XmlWriter.Create(outputPath, settings))
            {
                document.Save(writer);
            }
        }

        private static void WriteSuccess(HttpContext context, string outputPath)
        {
            context.Response.StatusCode = 200;
            context.Response.Write("{\"status\":\"ok\",\"outputPath\":\"" + EscapeJson(outputPath) + "\"}");
        }

        private static void WriteError(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            context.Response.Write("{\"status\":\"error\",\"message\":\"" + EscapeJson(message) + "\"}");
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private sealed class RouteInfo
        {
            public string Sender { get; set; }
            public string Receiver { get; set; }
            public string Endpoint { get; set; }
        }

        private sealed class FilePayload
        {
            public string FileName { get; set; }
            public string ContentType { get; set; }
            public byte[] Content { get; set; }
        }

        private sealed class Settings
        {
            public List<Connection> Connections { get; set; } = new List<Connection>();

            public void Normalize()
            {
                if (Connections == null)
                {
                    Connections = new List<Connection>();
                    return;
                }

                foreach (var connection in Connections)
                {
                    connection.Normalize();
                }
            }
        }

        private sealed class Connection
        {
            public string Sender { get; set; }
            public string Receiver { get; set; }
            public string BaseOutputPath { get; set; }

            public void Normalize()
            {
                Sender = Sender ?? string.Empty;
                Receiver = Receiver ?? string.Empty;
                BaseOutputPath = BaseOutputPath ?? string.Empty;
            }
        }
    }
}