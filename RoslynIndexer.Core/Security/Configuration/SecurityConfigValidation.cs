using System;
using System.Collections.Generic;

namespace RoslynIndexer.Core.Security.Configuration
{
    public sealed class SecurityConfigValidationReport
    {
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _warnings = new List<string>();

        public IReadOnlyList<string> Errors => _errors;
        public IReadOnlyList<string> Warnings => _warnings;
        public bool IsValid => _errors.Count == 0;

        public void AddError(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _errors.Add(message);
        }

        public void AddWarning(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _warnings.Add(message);
        }
    }

    public sealed class SecurityConfigBuildResult
    {
        public SecurityConfig Config { get; }
        public IReadOnlyList<string> Warnings { get; }

        public SecurityConfigBuildResult(SecurityConfig config, IReadOnlyList<string> warnings)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Warnings = warnings ?? Array.Empty<string>();
        }
    }
}
