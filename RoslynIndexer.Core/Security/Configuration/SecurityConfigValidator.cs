using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoslynIndexer.Core.Security.Configuration
{
    /// <summary>
    /// Validates typed security configuration and reports errors/warnings.
    /// </summary>
    public static class SecurityConfigValidator
    {
        public static SecurityConfigValidationReport Validate(SecurityConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var report = new SecurityConfigValidationReport();

            if (config.Version <= 0)
                report.AddError("security.version must be greater than zero.");

            if (!IsSupportedPathStyle(config.PathStyle))
                report.AddError($"security.path_style '{config.PathStyle}' is not supported. Allowed: windows, unix, posix.");

            var universe = config.ClassificationLabelsUniverse ?? Array.Empty<string>();
            var universeSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var label in universe)
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    report.AddError("security.classification_labels_universe cannot contain empty labels.");
                    continue;
                }

                if (!universeSet.Add(label))
                    report.AddError($"security.classification_labels_universe contains duplicate label '{label}'.");
            }

            if (config.Validation.ErrorOnUnknownClassificationLabel)
            {
                ValidateClassificationSubset(
                    report,
                    location: "defaults.classification_labels",
                    labels: config.Defaults.ClassificationLabels,
                    universe: universeSet);

                foreach (var rule in config.Rules)
                {
                    ValidateClassificationSubset(
                        report,
                        location: $"rules[{rule.Id}].set.classification_labels",
                        labels: rule.Set.ClassificationLabels,
                        universe: universeSet);
                }
            }

            if (config.Validation.WarnIfBothClassificationAndLevel)
            {
                if (config.Defaults.ClassificationLabels != null && config.Defaults.UserLevel.HasValue)
                {
                    report.AddWarning(
                        "defaults define both classification_labels and user_level. This is allowed but can be ambiguous.");
                }

                foreach (var rule in config.Rules)
                {
                    if (rule.Set.HasClassificationLabels && rule.Set.HasUserLevel)
                    {
                        report.AddWarning(
                            $"rule '{rule.Id}' sets both classification_labels and user_level. This is allowed but can be ambiguous.");
                    }
                }
            }

            for (int i = 0; i < config.Rules.Count; i++)
            {
                var rule = config.Rules[i];
                if (rule.Scope.Include.Count == 0)
                    report.AddError($"rule '{rule.Id}' must define at least one scope.include path.");
            }

            for (int i = 0; i < config.RegexTagExtractors.Count; i++)
            {
                var extractor = config.RegexTagExtractors[i];
                if (!extractor.Enabled)
                    continue;

                if (string.IsNullOrWhiteSpace(extractor.Pattern))
                {
                    report.AddError($"regex extractor '{extractor.Id}' must define a non-empty pattern.");
                }
                else
                {
                    try
                    {
                        _ = new Regex(extractor.Pattern, RegexOptions.CultureInvariant);
                    }
                    catch (Exception ex)
                    {
                        report.AddError($"regex extractor '{extractor.Id}' has invalid pattern: {ex.Message}");
                    }
                }

                if (extractor.Emit.Count == 0)
                    report.AddError($"regex extractor '{extractor.Id}' must define at least one emit rule.");

                for (int j = 0; j < extractor.Emit.Count; j++)
                {
                    var emit = extractor.Emit[j];

                    if (!string.Equals(emit.Target, "acl_tags", StringComparison.OrdinalIgnoreCase))
                    {
                        report.AddError(
                            $"regex extractor '{extractor.Id}' emit[{j}] target '{emit.Target}' is not supported. Only 'acl_tags' is allowed.");
                    }

                    if (!string.Equals(emit.Mode, "add", StringComparison.OrdinalIgnoreCase))
                    {
                        report.AddError(
                            $"regex extractor '{extractor.Id}' emit[{j}] mode '{emit.Mode}' is not supported. Only 'add' is allowed.");
                    }

                    var hasLiteral = !string.IsNullOrWhiteSpace(emit.ValueLiteral);
                    var hasGroup = !string.IsNullOrWhiteSpace(emit.ValueFromGroupName) || emit.ValueFromGroupIndex.HasValue;
                    if (!hasLiteral && !hasGroup)
                    {
                        report.AddError(
                            $"regex extractor '{extractor.Id}' emit[{j}] must define either value or value_from_group.");
                    }
                }
            }

            return report;
        }

        private static bool IsSupportedPathStyle(string pathStyle)
        {
            return string.Equals(pathStyle, "windows", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(pathStyle, "unix", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(pathStyle, "posix", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateClassificationSubset(
            SecurityConfigValidationReport report,
            string location,
            IReadOnlyList<string>? labels,
            HashSet<string> universe)
        {
            if (labels == null)
                return;

            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    report.AddError($"{location} contains an empty label.");
                    continue;
                }

                if (!universe.Contains(label))
                    report.AddError($"{location} contains unknown label '{label}'.");
            }
        }
    }
}
