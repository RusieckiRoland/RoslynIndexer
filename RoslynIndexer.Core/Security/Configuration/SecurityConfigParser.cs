using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RoslynIndexer.Core.Security.Configuration
{
    /// <summary>
    /// Parses raw JSON into typed security configuration.
    /// </summary>
    public static class SecurityConfigParser
    {
        public static SecurityConfig Parse(JObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var version = root.Value<int?>("version") ?? 1;
            var pathStyle = ReadString(root, "path_style") ?? "windows";
            var universe = ReadStringList(root["classification_labels_universe"]);
            var defaults = ParseDefaults(root["defaults"] as JObject);
            var rules = ParseRules(root["rules"] as JArray);
            var extractors = ParseRegexExtractors(root["regex_tag_extractors"] as JArray);
            var validation = ParseValidation(root["validation"] as JObject);

            return new SecurityConfig(
                version: version,
                pathStyle: pathStyle,
                classificationLabelsUniverse: universe,
                defaults: defaults,
                rules: rules,
                regexTagExtractors: extractors,
                validation: validation);
        }

        private static SecurityDefaults ParseDefaults(JObject? defaults)
        {
            if (defaults == null)
                return SecurityDefaults.Empty;

            var aclTags = ReadStringList(defaults["acl_tags"]);
            var labels = ReadStringListOrNull(defaults["classification_labels"]);
            var userLevel = ReadNullableInt(defaults["user_level"]);

            return new SecurityDefaults(
                aclTags: aclTags,
                classificationLabels: labels,
                userLevel: userLevel);
        }

        private static IReadOnlyList<SecurityRule> ParseRules(JArray? rules)
        {
            if (rules == null || rules.Count == 0)
                return Array.Empty<SecurityRule>();

            var list = new List<SecurityRule>(rules.Count);
            for (int i = 0; i < rules.Count; i++)
            {
                var obj = rules[i] as JObject;
                if (obj == null)
                    continue;

                var id = ReadString(obj, "id") ?? $"rule-{i + 1}";
                var scope = ParseScope(obj["scope"] as JObject);
                var set = ParseRuleSet(obj["set"] as JObject);
                var merge = ParseMergeOptions(obj["merge"] as JObject, $"rules[{i}]");

                list.Add(new SecurityRule(
                    id: id,
                    scope: scope,
                    set: set,
                    merge: merge,
                    order: i));
            }

            return list;
        }

        private static SecurityRuleSet ParseRuleSet(JObject? setObj)
        {
            if (setObj == null)
                return SecurityRuleSet.Empty;

            bool hasAclTags = setObj.Property("acl_tags") != null;
            bool hasClassificationLabels = setObj.Property("classification_labels") != null;
            bool hasUserLevel = setObj.Property("user_level") != null;

            var aclTags = hasAclTags
                ? ReadStringList(setObj["acl_tags"])
                : Array.Empty<string>();

            var labels = hasClassificationLabels
                ? ReadStringListOrNull(setObj["classification_labels"])
                : null;

            var userLevel = hasUserLevel
                ? ReadNullableInt(setObj["user_level"])
                : null;

            return new SecurityRuleSet(
                hasAclTags: hasAclTags,
                aclTags: aclTags,
                hasClassificationLabels: hasClassificationLabels,
                classificationLabels: labels,
                hasUserLevel: hasUserLevel,
                userLevel: userLevel);
        }

        private static SecurityMergeOptions ParseMergeOptions(JObject? mergeObj, string location)
        {
            if (mergeObj == null)
                return SecurityMergeOptions.Default;

            var aclTags = ParseMergeMode(
                mergeObj["acl_tags"],
                SecurityMergeOptions.Default.AclTags,
                $"{location}.merge.acl_tags");

            var classificationLabels = ParseMergeMode(
                mergeObj["classification_labels"],
                SecurityMergeOptions.Default.ClassificationLabels,
                $"{location}.merge.classification_labels");

            var userLevel = ParseMergeMode(
                mergeObj["user_level"],
                SecurityMergeOptions.Default.UserLevel,
                $"{location}.merge.user_level");

            return new SecurityMergeOptions(
                aclTags: aclTags,
                classificationLabels: classificationLabels,
                userLevel: userLevel);
        }

        private static IReadOnlyList<SecurityRegexTagExtractor> ParseRegexExtractors(JArray? extractors)
        {
            if (extractors == null || extractors.Count == 0)
                return Array.Empty<SecurityRegexTagExtractor>();

            var list = new List<SecurityRegexTagExtractor>(extractors.Count);
            for (int i = 0; i < extractors.Count; i++)
            {
                var obj = extractors[i] as JObject;
                if (obj == null)
                    continue;

                var id = ReadString(obj, "id") ?? $"regex-extractor-{i + 1}";
                var enabled = obj.Value<bool?>("enabled") ?? true;
                var appliesTo = ParseScope(obj["applies_to"] as JObject);
                var pattern = ReadString(obj, "pattern") ?? string.Empty;
                var emit = ParseRegexEmitRules(obj["emit"] as JArray, $"regex_tag_extractors[{i}]");

                list.Add(new SecurityRegexTagExtractor(
                    id: id,
                    enabled: enabled,
                    appliesTo: appliesTo,
                    pattern: pattern,
                    emit: emit,
                    order: i));
            }

            return list;
        }

        private static IReadOnlyList<SecurityRegexEmit> ParseRegexEmitRules(JArray? emitArr, string location)
        {
            if (emitArr == null || emitArr.Count == 0)
                return Array.Empty<SecurityRegexEmit>();

            var list = new List<SecurityRegexEmit>(emitArr.Count);
            for (int i = 0; i < emitArr.Count; i++)
            {
                var obj = emitArr[i] as JObject;
                if (obj == null)
                    continue;

                var target = ReadString(obj, "target") ?? string.Empty;
                var mode = ReadString(obj, "mode") ?? string.Empty;
                var valueLiteral = ReadString(obj, "value");

                string? groupName = null;
                int? groupIndex = null;

                var groupToken = obj["value_from_group"];
                if (groupToken != null && groupToken.Type != JTokenType.Null)
                {
                    if (groupToken.Type == JTokenType.Integer)
                    {
                        groupIndex = groupToken.Value<int>();
                    }
                    else if (groupToken.Type == JTokenType.String)
                    {
                        var raw = ((string?)groupToken)?.Trim();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            if (int.TryParse(raw, out var parsed))
                                groupIndex = parsed;
                            else
                                groupName = raw;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"{location}.emit[{i}].value_from_group must be string or integer.");
                    }
                }

                list.Add(new SecurityRegexEmit(
                    target: target,
                    mode: mode,
                    valueFromGroupName: groupName,
                    valueFromGroupIndex: groupIndex,
                    valueLiteral: valueLiteral));
            }

            return list;
        }

        private static SecurityValidationOptions ParseValidation(JObject? validationObj)
        {
            if (validationObj == null)
                return SecurityValidationOptions.Default;

            var warnIfBoth = validationObj.Value<bool?>("warn_if_both_classification_and_level")
                             ?? SecurityValidationOptions.Default.WarnIfBothClassificationAndLevel;

            var errorOnUnknown = validationObj.Value<bool?>("error_on_unknown_classification_label")
                                 ?? SecurityValidationOptions.Default.ErrorOnUnknownClassificationLabel;

            return new SecurityValidationOptions(
                warnIfBothClassificationAndLevel: warnIfBoth,
                errorOnUnknownClassificationLabel: errorOnUnknown);
        }

        private static SecurityScope ParseScope(JObject? scopeObj)
        {
            if (scopeObj == null)
                return SecurityScope.Empty;

            return new SecurityScope(
                include: ReadStringList(scopeObj["include"]),
                exclude: ReadStringList(scopeObj["exclude"]));
        }

        private static SecurityMergeMode ParseMergeMode(JToken? token, SecurityMergeMode fallback, string location)
        {
            if (token == null || token.Type == JTokenType.Null)
                return fallback;

            var raw = token.Type == JTokenType.String
                ? ((string?)token)?.Trim()
                : token.ToString().Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            switch (raw.ToLowerInvariant())
            {
                case "inherit_add":
                    return SecurityMergeMode.InheritAdd;
                case "replace":
                    return SecurityMergeMode.Replace;
                case "inherit_replace":
                    return SecurityMergeMode.InheritReplace;
                default:
                    throw new InvalidOperationException(
                        $"{location} has invalid value '{raw}'. Allowed: inherit_add, replace, inherit_replace.");
            }
        }

        private static string? ReadString(JObject obj, string propertyName)
        {
            if (obj == null)
                return null;

            var token = obj[propertyName];
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.String)
                return ((string?)token)?.Trim();

            return token.ToString().Trim();
        }

        private static int? ReadNullableInt(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Integer)
                return token.Value<int>();

            if (token.Type == JTokenType.String)
            {
                var raw = ((string?)token)?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    return null;

                if (int.TryParse(raw, out var parsed))
                    return parsed;
            }

            return null;
        }

        private static IReadOnlyList<string>? ReadStringListOrNull(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            return ReadStringList(token);
        }

        private static IReadOnlyList<string> ReadStringList(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return Array.Empty<string>();

            if (token.Type == JTokenType.String)
            {
                var text = ((string?)token)?.Trim();
                return string.IsNullOrWhiteSpace(text)
                    ? Array.Empty<string>()
                    : new[] { text };
            }

            if (token is JArray arr)
            {
                var list = arr
                    .Where(t => t != null && t.Type == JTokenType.String)
                    .Select(t => ((string?)t)?.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToList();

                return list;
            }

            return Array.Empty<string>();
        }
    }
}
