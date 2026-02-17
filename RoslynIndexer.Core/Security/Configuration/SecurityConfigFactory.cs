using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RoslynIndexer.Core.Security.Configuration
{
    /// <summary>
    /// Builds validated configuration from JSON and fails fast on invalid config.
    /// </summary>
    public static class SecurityConfigFactory
    {
        public static SecurityConfigBuildResult Build(JObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var config = SecurityConfigParser.Parse(root);
            var report = SecurityConfigValidator.Validate(config);

            if (!report.IsValid)
            {
                var combined = string.Join(" | ", report.Errors.ToArray());
                throw new InvalidOperationException("Security config is invalid: " + combined);
            }

            return new SecurityConfigBuildResult(config, report.Warnings.ToArray());
        }
    }
}
