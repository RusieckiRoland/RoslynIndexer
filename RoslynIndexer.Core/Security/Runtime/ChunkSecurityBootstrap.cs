using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RoslynIndexer.Core.Security.Configuration;
using RoslynIndexer.Core.Security.Engine;

namespace RoslynIndexer.Core.Security.Runtime
{
    public sealed class ChunkSecurityBootstrapResult
    {
        public static readonly ChunkSecurityBootstrapResult Disabled = new ChunkSecurityBootstrapResult(
            engine: null,
            warnings: Array.Empty<string>(),
            errors: Array.Empty<string>());

        public SecurityPolicyEngine? Engine { get; }
        public IReadOnlyList<string> Warnings { get; }
        public IReadOnlyList<string> Errors { get; }
        public bool IsEnabled => Engine != null;
        public bool IsValid => Errors.Count == 0;

        public ChunkSecurityBootstrapResult(
            SecurityPolicyEngine? engine,
            IReadOnlyList<string> warnings,
            IReadOnlyList<string> errors)
        {
            Engine = engine;
            Warnings = warnings ?? Array.Empty<string>();
            Errors = errors ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Loads security policy from config sections or an external security.json file.
    /// </summary>
    public static class ChunkSecurityBootstrap
    {
        public static ChunkSecurityBootstrapResult TryCreate(JObject? rootConfig, string? configBaseDir)
        {
            if (rootConfig == null)
                return ChunkSecurityBootstrapResult.Disabled;

            try
            {
                var securityJson = ResolveSecurityJson(rootConfig, configBaseDir);
                if (securityJson == null)
                    return ChunkSecurityBootstrapResult.Disabled;

                var built = SecurityConfigFactory.Build(securityJson);
                var engine = new SecurityPolicyEngine(built.Config);
                return new ChunkSecurityBootstrapResult(
                    engine: engine,
                    warnings: built.Warnings,
                    errors: Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return new ChunkSecurityBootstrapResult(
                    engine: null,
                    warnings: Array.Empty<string>(),
                    errors: new[] { ex.Message });
            }
        }

        private static JObject? ResolveSecurityJson(JObject rootConfig, string? configBaseDir)
        {
            var securitySectionToken = rootConfig["security"];
            if (securitySectionToken != null)
            {
                if (!(securitySectionToken is JObject securitySection))
                    throw new InvalidOperationException("'security' section must be a JSON object.");

                var sectionPath = ReadString(securitySection["configPath"]);
                if (!string.IsNullOrWhiteSpace(sectionPath))
                    return LoadJsonFromPath(sectionPath, configBaseDir);

                if (LooksLikeSecurityConfigRoot(securitySection) || HasSecurityPolicyFields(securitySection))
                    return securitySection;

                // Ignore unrelated "security" sections when they do not look like policy config.
                return null;
            }

            var rootPath = ReadString(rootConfig.SelectToken("paths.securityConfig", errorWhenNoMatch: false))
                           ?? ReadString(rootConfig["securityConfig"]);
            if (!string.IsNullOrWhiteSpace(rootPath))
                return LoadJsonFromPath(rootPath, configBaseDir);

            if (LooksLikeSecurityConfigRoot(rootConfig))
                return rootConfig;

            return null;
        }

        private static JObject LoadJsonFromPath(string rawPath, string? configBaseDir)
        {
            var fullPath = ResolvePath(rawPath, configBaseDir);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Security config file not found.", fullPath);

            var text = File.ReadAllText(fullPath);
            using (var sr = new StringReader(text))
            using (var reader = new JsonTextReader(sr))
            {
                var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
                var token = JToken.ReadFrom(reader, settings);
                if (!(token is JObject obj))
                    throw new InvalidOperationException("Security config root must be a JSON object.");

                return obj;
            }
        }

        private static bool LooksLikeSecurityConfigRoot(JObject rootConfig)
        {
            return rootConfig["classification_labels_universe"] is JArray
                   && rootConfig["rules"] is JArray
                   && rootConfig["defaults"] is JObject;
        }

        private static bool HasSecurityPolicyFields(JObject rootConfig)
        {
            return rootConfig["rules"] != null
                   || rootConfig["defaults"] != null
                   || rootConfig["regex_tag_extractors"] != null
                   || rootConfig["classification_labels_universe"] != null
                   || rootConfig["validation"] != null
                   || rootConfig["path_style"] != null;
        }

        private static string ResolvePath(string rawPath, string? baseDir)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                throw new ArgumentException("Path is empty.", nameof(rawPath));

            if (Path.IsPathRooted(rawPath))
                return Path.GetFullPath(rawPath);

            if (!string.IsNullOrWhiteSpace(baseDir))
                return Path.GetFullPath(Path.Combine(baseDir, rawPath));

            return Path.GetFullPath(rawPath);
        }

        private static string? ReadString(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.String)
                return ((string?)token)?.Trim();

            return token.ToString().Trim();
        }
    }
}
