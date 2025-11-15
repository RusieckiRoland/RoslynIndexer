using System;

namespace RoslynIndexer.Tests.Common
{
    // Simple test logger controlled by a single flag.
    internal static class TestLog
    {
        /// <summary>
        /// When true, log messages are written to the test output (Console).
        /// When false, all logging is suppressed.
        /// </summary>
        public static bool Enabled { get; set; }

        static TestLog()
        {
            var env = Environment.GetEnvironmentVariable("RI_TEST_VERBOSE");
            if (!string.IsNullOrEmpty(env) &&
                (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)))
            {
                Enabled = true;
            }
            else
            {
                Enabled = false;
            }
        }

        public static void Info(string message)
        {
            if (!Enabled) return;
            Console.WriteLine("[TEST][INFO] " + message);
        }

        public static void Warn(string message)
        {
            if (!Enabled) return;
            Console.WriteLine("[TEST][WARN] " + message);
        }

        public static void Error(string message)
        {
            if (!Enabled) return;
            Console.WriteLine("[TEST][ERR ] " + message);
        }
    }
}
