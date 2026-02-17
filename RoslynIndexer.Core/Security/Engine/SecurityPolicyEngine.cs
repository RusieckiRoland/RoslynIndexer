using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RoslynIndexer.Core.Security.Configuration;

namespace RoslynIndexer.Core.Security.Engine
{
    /// <summary>
    /// Computes effective security metadata for a path using hierarchical folder rules.
    /// </summary>
    public sealed class SecurityPolicyEngine
    {
        private readonly SecurityConfig _config;
        private readonly char _separator;
        private readonly StringComparison _pathComparison;
        private readonly List<CompiledRule> _compiledRules;
        private readonly List<CompiledExtractor> _compiledExtractors;

        public SecurityPolicyEngine(SecurityConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            var isWindows = string.Equals(_config.PathStyle, "windows", StringComparison.OrdinalIgnoreCase);
            _separator = isWindows ? '\\' : '/';
            _pathComparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var regexOptions = isWindows
                ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
                : RegexOptions.CultureInvariant;

            _compiledRules = _config.Rules
                .Select(r => new CompiledRule(r, CompileScope(r.Scope)))
                .ToList();

            _compiledExtractors = _config.RegexTagExtractors
                .Where(e => e.Enabled)
                .Select(e => new CompiledExtractor(
                    extractor: e,
                    scope: CompileScope(e.AppliesTo),
                    regex: new Regex(e.Pattern, regexOptions)))
                .OrderBy(e => e.Extractor.Order)
                .ToList();
        }

        public SecurityComputationResult ComputeSecurity(string path)
        {
            var normalizedPath = NormalizePath(path);
            var pathWithSeparator = EnsureTrailingSeparator(normalizedPath);

            var aclTags = CreateDistinctList(_config.Defaults.AclTags);
            List<string>? labels = _config.Defaults.ClassificationLabels != null
                ? CreateDistinctList(_config.Defaults.ClassificationLabels)
                : null;
            int? userLevel = _config.Defaults.UserLevel;

            var matchedRules = MatchRules(pathWithSeparator);
            foreach (var matched in matchedRules)
            {
                var rule = matched.Rule.Rule;
                ApplyAclTags(rule, ref aclTags);
                ApplyClassificationLabels(rule, ref labels);
                ApplyUserLevel(rule, ref userLevel);
            }

            foreach (var extractor in _compiledExtractors)
            {
                if (!TryMatchScope(extractor.Scope, pathWithSeparator, out _))
                    continue;

                var match = extractor.Regex.Match(normalizedPath);
                if (!match.Success)
                    continue;

                foreach (var emit in extractor.Extractor.Emit)
                {
                    if (!string.Equals(emit.Target, "acl_tags", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.Equals(emit.Mode, "add", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var value = ResolveEmitValue(match, emit);
                    if (!string.IsNullOrWhiteSpace(value))
                        AddDistinct(aclTags, value.Trim());
                }
            }

            var warnings = new List<string>();
            if (_config.Validation.WarnIfBothClassificationAndLevel
                && labels != null
                && labels.Count > 0
                && userLevel.HasValue)
            {
                warnings.Add(
                    $"Both classification_labels and user_level are set for path '{normalizedPath}'.");
            }

            return new SecurityComputationResult(
                classificationLabels: labels == null ? null : labels.ToArray(),
                userLevel: userLevel,
                aclTags: aclTags.ToArray(),
                warnings: warnings.ToArray());
        }

        private List<MatchedRule> MatchRules(string pathWithSeparator)
        {
            var matched = new List<MatchedRule>();

            foreach (var rule in _compiledRules)
            {
                if (!TryMatchScope(rule.Scope, pathWithSeparator, out var depth))
                    continue;

                matched.Add(new MatchedRule(rule, depth));
            }

            matched.Sort((a, b) =>
            {
                var byDepth = a.Depth.CompareTo(b.Depth);
                if (byDepth != 0) return byDepth;
                return a.Rule.Rule.Order.CompareTo(b.Rule.Rule.Order);
            });

            return matched;
        }

        private void ApplyAclTags(SecurityRule rule, ref List<string> aclTags)
        {
            if (!rule.Set.HasAclTags)
                return;

            if (rule.Merge.AclTags == SecurityMergeMode.Replace)
            {
                aclTags = CreateDistinctList(rule.Set.AclTags);
                return;
            }

            AddDistinct(aclTags, rule.Set.AclTags);
        }

        private void ApplyClassificationLabels(SecurityRule rule, ref List<string>? labels)
        {
            if (!rule.Set.HasClassificationLabels)
                return;

            if (rule.Merge.ClassificationLabels == SecurityMergeMode.Replace)
            {
                labels = rule.Set.ClassificationLabels != null
                    ? CreateDistinctList(rule.Set.ClassificationLabels)
                    : null;
                return;
            }

            if (rule.Set.ClassificationLabels == null)
                return;

            if (labels == null)
                labels = new List<string>();

            AddDistinct(labels, rule.Set.ClassificationLabels);
        }

        private static void ApplyUserLevel(SecurityRule rule, ref int? userLevel)
        {
            if (!rule.Set.HasUserLevel)
                return;

            userLevel = rule.Set.UserLevel;
        }

        private static string? ResolveEmitValue(Match match, SecurityRegexEmit emit)
        {
            if (!string.IsNullOrWhiteSpace(emit.ValueLiteral))
                return emit.ValueLiteral;

            if (emit.ValueFromGroupIndex.HasValue)
            {
                var index = emit.ValueFromGroupIndex.Value;
                if (index >= 0 && index < match.Groups.Count)
                {
                    var group = match.Groups[index];
                    if (group.Success)
                        return group.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(emit.ValueFromGroupName))
            {
                var group = match.Groups[emit.ValueFromGroupName];
                if (group != null && group.Success)
                    return group.Value;
            }

            return null;
        }

        private bool TryMatchScope(CompiledScope scope, string pathWithSeparator, out int depth)
        {
            depth = -1;
            if (scope.IncludePrefixes.Count == 0)
                return false;

            for (int i = 0; i < scope.IncludePrefixes.Count; i++)
            {
                var include = scope.IncludePrefixes[i];
                if (IsPrefixMatch(pathWithSeparator, include))
                {
                    if (include.Length > depth)
                        depth = include.Length;
                }
            }

            if (depth < 0)
                return false;

            for (int i = 0; i < scope.ExcludePrefixes.Count; i++)
            {
                var exclude = scope.ExcludePrefixes[i];
                if (IsPrefixMatch(pathWithSeparator, exclude))
                    return false;
            }

            return true;
        }

        private bool IsPrefixMatch(string pathWithSeparator, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return false;

            return pathWithSeparator.StartsWith(prefix, _pathComparison);
        }

        private CompiledScope CompileScope(SecurityScope scope)
        {
            var include = scope.Include.Select(NormalizePrefix).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var exclude = scope.Exclude.Select(NormalizePrefix).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            return new CompiledScope(include, exclude);
        }

        private string NormalizePrefix(string value)
        {
            var normalized = NormalizePath(value);
            return EnsureTrailingSeparator(normalized);
        }

        private string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim()
                .Replace('\\', _separator)
                .Replace('/', _separator);

            return normalized;
        }

        private string EnsureTrailingSeparator(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            if (value[value.Length - 1] == _separator)
                return value;

            return value + _separator;
        }

        private static List<string> CreateDistinctList(IEnumerable<string> values)
        {
            var list = new List<string>();
            AddDistinct(list, values);
            return list;
        }

        private static void AddDistinct(List<string> target, IEnumerable<string> values)
        {
            foreach (var value in values)
                AddDistinct(target, value);
        }

        private static void AddDistinct(List<string> target, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            for (int i = 0; i < target.Count; i++)
            {
                if (string.Equals(target[i], value, StringComparison.Ordinal))
                    return;
            }

            target.Add(value);
        }

        private sealed class MatchedRule
        {
            public CompiledRule Rule { get; }
            public int Depth { get; }

            public MatchedRule(CompiledRule rule, int depth)
            {
                Rule = rule;
                Depth = depth;
            }
        }

        private sealed class CompiledRule
        {
            public SecurityRule Rule { get; }
            public CompiledScope Scope { get; }

            public CompiledRule(SecurityRule rule, CompiledScope scope)
            {
                Rule = rule;
                Scope = scope;
            }
        }

        private sealed class CompiledExtractor
        {
            public SecurityRegexTagExtractor Extractor { get; }
            public CompiledScope Scope { get; }
            public Regex Regex { get; }

            public CompiledExtractor(SecurityRegexTagExtractor extractor, CompiledScope scope, Regex regex)
            {
                Extractor = extractor;
                Scope = scope;
                Regex = regex;
            }
        }

        private sealed class CompiledScope
        {
            public IReadOnlyList<string> IncludePrefixes { get; }
            public IReadOnlyList<string> ExcludePrefixes { get; }

            public CompiledScope(IReadOnlyList<string> includePrefixes, IReadOnlyList<string> excludePrefixes)
            {
                IncludePrefixes = includePrefixes;
                ExcludePrefixes = excludePrefixes;
            }
        }
    }
}
