using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Diagnostics;

namespace RoslynIndexer.Core.Tests.Diagnostics
{
    [TestClass]
    public class HeartbeatProgressReporterTests
    {
        [TestMethod]
        public async Task WritesHeartbeat_TracksPhaseAndProgress_AndStopsOnDispose()
        {
            var sw = new StringWriter();
            using var hb = new HeartbeatProgressReporter(sw, initialPhase: "Boot", periodMs: 250);

            // Give it time to emit at least one line with initial phase
            await Task.Delay(350);
            var text = sw.ToString();
            StringAssert.Contains(text, "Boot");

            // Change phase & set progress
            hb.SetPhase("Indexing");
            hb.Report(("Indexing", 2, 4));

            await Task.Delay(350);
            text = sw.ToString();
            StringAssert.Contains(text, "Indexing");
            StringAssert.Contains(text, "[2/4]"); // progress block
            StringAssert.Matches(text, new Regex(@"\(\s*(?:\d{1,3}%|-)\)")); // percentage or dash present (inside parentheses)// percentage or dash present

            // Capture length, dispose, and ensure no more writes happen
            var before = text.Length;
            hb.Dispose();
            await Task.Delay(600); // > 2 periods
            var after = sw.ToString().Length;
            Assert.AreEqual(before, after, "No new heartbeat lines expected after Dispose()");
        }
    }
}
