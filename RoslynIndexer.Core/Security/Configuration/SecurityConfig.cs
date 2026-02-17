using System;
using System.Collections.Generic;

namespace RoslynIndexer.Core.Security.Configuration
{
    public enum SecurityMergeMode
    {
        InheritAdd,
        Replace,
        InheritReplace
    }

    public sealed class SecurityValidationOptions
    {
        public static readonly SecurityValidationOptions Default = new SecurityValidationOptions(
            warnIfBothClassificationAndLevel: true,
            errorOnUnknownClassificationLabel: true);

        public bool WarnIfBothClassificationAndLevel { get; }
        public bool ErrorOnUnknownClassificationLabel { get; }

        public SecurityValidationOptions(bool warnIfBothClassificationAndLevel, bool errorOnUnknownClassificationLabel)
        {
            WarnIfBothClassificationAndLevel = warnIfBothClassificationAndLevel;
            ErrorOnUnknownClassificationLabel = errorOnUnknownClassificationLabel;
        }
    }

    public sealed class SecurityDefaults
    {
        public static readonly SecurityDefaults Empty = new SecurityDefaults(
            aclTags: Array.Empty<string>(),
            classificationLabels: null,
            userLevel: null);

        public IReadOnlyList<string> AclTags { get; }
        public IReadOnlyList<string>? ClassificationLabels { get; }
        public int? UserLevel { get; }

        public SecurityDefaults(
            IReadOnlyList<string> aclTags,
            IReadOnlyList<string>? classificationLabels,
            int? userLevel)
        {
            AclTags = aclTags ?? Array.Empty<string>();
            ClassificationLabels = classificationLabels;
            UserLevel = userLevel;
        }
    }

    public sealed class SecurityScope
    {
        public static readonly SecurityScope Empty = new SecurityScope(
            include: Array.Empty<string>(),
            exclude: Array.Empty<string>());

        public IReadOnlyList<string> Include { get; }
        public IReadOnlyList<string> Exclude { get; }

        public SecurityScope(IReadOnlyList<string> include, IReadOnlyList<string> exclude)
        {
            Include = include ?? Array.Empty<string>();
            Exclude = exclude ?? Array.Empty<string>();
        }
    }

    public sealed class SecurityRuleSet
    {
        public static readonly SecurityRuleSet Empty = new SecurityRuleSet(
            hasAclTags: false,
            aclTags: Array.Empty<string>(),
            hasClassificationLabels: false,
            classificationLabels: null,
            hasUserLevel: false,
            userLevel: null);

        public bool HasAclTags { get; }
        public IReadOnlyList<string> AclTags { get; }

        public bool HasClassificationLabels { get; }
        public IReadOnlyList<string>? ClassificationLabels { get; }

        public bool HasUserLevel { get; }
        public int? UserLevel { get; }

        public SecurityRuleSet(
            bool hasAclTags,
            IReadOnlyList<string> aclTags,
            bool hasClassificationLabels,
            IReadOnlyList<string>? classificationLabels,
            bool hasUserLevel,
            int? userLevel)
        {
            HasAclTags = hasAclTags;
            AclTags = aclTags ?? Array.Empty<string>();
            HasClassificationLabels = hasClassificationLabels;
            ClassificationLabels = classificationLabels;
            HasUserLevel = hasUserLevel;
            UserLevel = userLevel;
        }
    }

    public sealed class SecurityMergeOptions
    {
        public static readonly SecurityMergeOptions Default = new SecurityMergeOptions(
            aclTags: SecurityMergeMode.InheritAdd,
            classificationLabels: SecurityMergeMode.InheritAdd,
            userLevel: SecurityMergeMode.InheritReplace);

        public SecurityMergeMode AclTags { get; }
        public SecurityMergeMode ClassificationLabels { get; }
        public SecurityMergeMode UserLevel { get; }

        public SecurityMergeOptions(
            SecurityMergeMode aclTags,
            SecurityMergeMode classificationLabels,
            SecurityMergeMode userLevel)
        {
            AclTags = aclTags;
            ClassificationLabels = classificationLabels;
            UserLevel = userLevel;
        }
    }

    public sealed class SecurityRule
    {
        public string Id { get; }
        public SecurityScope Scope { get; }
        public SecurityRuleSet Set { get; }
        public SecurityMergeOptions Merge { get; }
        public int Order { get; }

        public SecurityRule(string id, SecurityScope scope, SecurityRuleSet set, SecurityMergeOptions merge, int order)
        {
            Id = id ?? string.Empty;
            Scope = scope ?? SecurityScope.Empty;
            Set = set ?? SecurityRuleSet.Empty;
            Merge = merge ?? SecurityMergeOptions.Default;
            Order = order;
        }
    }

    public sealed class SecurityRegexEmit
    {
        public string Target { get; }
        public string Mode { get; }
        public string? ValueFromGroupName { get; }
        public int? ValueFromGroupIndex { get; }
        public string? ValueLiteral { get; }

        public SecurityRegexEmit(
            string target,
            string mode,
            string? valueFromGroupName,
            int? valueFromGroupIndex,
            string? valueLiteral)
        {
            Target = target ?? string.Empty;
            Mode = mode ?? string.Empty;
            ValueFromGroupName = valueFromGroupName;
            ValueFromGroupIndex = valueFromGroupIndex;
            ValueLiteral = valueLiteral;
        }
    }

    public sealed class SecurityRegexTagExtractor
    {
        public string Id { get; }
        public bool Enabled { get; }
        public SecurityScope AppliesTo { get; }
        public string Pattern { get; }
        public IReadOnlyList<SecurityRegexEmit> Emit { get; }
        public int Order { get; }

        public SecurityRegexTagExtractor(
            string id,
            bool enabled,
            SecurityScope appliesTo,
            string pattern,
            IReadOnlyList<SecurityRegexEmit> emit,
            int order)
        {
            Id = id ?? string.Empty;
            Enabled = enabled;
            AppliesTo = appliesTo ?? SecurityScope.Empty;
            Pattern = pattern ?? string.Empty;
            Emit = emit ?? Array.Empty<SecurityRegexEmit>();
            Order = order;
        }
    }

    public sealed class SecurityConfig
    {
        public int Version { get; }
        public string PathStyle { get; }
        public IReadOnlyList<string> ClassificationLabelsUniverse { get; }
        public SecurityDefaults Defaults { get; }
        public IReadOnlyList<SecurityRule> Rules { get; }
        public IReadOnlyList<SecurityRegexTagExtractor> RegexTagExtractors { get; }
        public SecurityValidationOptions Validation { get; }

        public SecurityConfig(
            int version,
            string pathStyle,
            IReadOnlyList<string> classificationLabelsUniverse,
            SecurityDefaults defaults,
            IReadOnlyList<SecurityRule> rules,
            IReadOnlyList<SecurityRegexTagExtractor> regexTagExtractors,
            SecurityValidationOptions validation)
        {
            Version = version;
            PathStyle = string.IsNullOrWhiteSpace(pathStyle) ? "windows" : pathStyle;
            ClassificationLabelsUniverse = classificationLabelsUniverse ?? Array.Empty<string>();
            Defaults = defaults ?? SecurityDefaults.Empty;
            Rules = rules ?? Array.Empty<SecurityRule>();
            RegexTagExtractors = regexTagExtractors ?? Array.Empty<SecurityRegexTagExtractor>();
            Validation = validation ?? SecurityValidationOptions.Default;
        }
    }
}
