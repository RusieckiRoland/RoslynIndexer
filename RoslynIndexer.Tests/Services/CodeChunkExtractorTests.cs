using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Services;

namespace RoslynIndexer.Core.Tests.Services
{
    [TestClass]
    public class CodeChunkExtractorTests
    {
        private static AdhocWorkspace NewWorkspace()
        {
            var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
            return new AdhocWorkspace(host);
        }

        private static Solution AddProjectWithDocument(AdhocWorkspace ws, string projectName, string docName, string code, string filePath)
        {
            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview));

            var solution = ws.CurrentSolution.AddProject(projectInfo);
            var docId = DocumentId.CreateNewId(projectId);
            var docInfo = DocumentInfo.Create(
                docId,
                docName,
                filePath: filePath,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create())));

            solution = solution.AddDocument(docInfo);
            ws.TryApplyChanges(solution);
            return ws.CurrentSolution;
        }

        [TestMethod]
        public async Task ExtractAsync_TypeWithManyProperties_GeneratesTypeRollup_NoPropertyChunks()
        {
            using var ws = NewWorkspace();
            var props = string.Join(" ", Enumerable.Range(0, 25).Select(i => $"public int P{i} {{ get; set; }}"));
            var code = $"namespace N {{ public class Poco {{ {props} }} }}";

            var filePath = Path.Combine(Path.GetTempPath(), "Poco.cs");
            var solution = AddProjectWithDocument(ws, "P1", "Poco.cs", code, filePath);

            var extractor = new CodeChunkExtractor();
            var result = await extractor.ExtractAsync(solution, repoRoot: Path.GetTempPath(), branchName: "b", headSha: "h", CancellationToken.None);

            var rollups = result.chunks.Where(c => c.ChunkKind == "type_rollup").ToList();
            Assert.AreEqual(1, rollups.Count, "Expected exactly one TYPE_ROLLUP chunk.");
            Assert.IsTrue(rollups[0].Text.Contains("P0"), "TYPE_ROLLUP should include properties.");

            Assert.IsFalse(result.chunks.Any(c => c.MemberKind == "property"), "No property chunks should be emitted for small properties.");
        }

        [TestMethod]
        public async Task ExtractAsync_LargeMethod_IsSplitIntoParts()
        {
            using var ws = NewWorkspace();
            var body = string.Join("\n", Enumerable.Range(0, 800).Select(_ => "x++;"));
            var code = "namespace N { public class Big { public void M() { int x = 0;\n" + body + "\n } } }";

            var filePath = Path.Combine(Path.GetTempPath(), "Big.cs");
            var solution = AddProjectWithDocument(ws, "P1", "Big.cs", code, filePath);

            var extractor = new CodeChunkExtractor();
            var result = await extractor.ExtractAsync(solution, repoRoot: Path.GetTempPath(), branchName: "b", headSha: "h", CancellationToken.None);

            var methodChunks = result.chunks.Where(c => c.ChunkKind == "method" && c.Member == "M").ToList();
            Assert.IsTrue(methodChunks.Count > 1, "Expected the large method to be split into multiple chunks.");

            var partCount = methodChunks[0].PartCount;
            Assert.IsTrue(partCount == methodChunks.Count, "PartCount should match number of method chunks.");
            CollectionAssert.AreEquivalent(Enumerable.Range(1, partCount).ToList(), methodChunks.Select(c => c.PartIndex).OrderBy(i => i).ToList());
        }

        [TestMethod]
        public async Task ExtractAsync_RecordsStructsInterfacesAndNestedTypes_HaveStableTypeFqn()
        {
            using var ws = NewWorkspace();
            var code = @"
namespace N {
    public record R(int A);
    public struct S { public int X; }
    public interface I { void M(); }
    public class Outer { public class Inner { public int P { get; set; } } }
}";

            var filePath = Path.Combine(Path.GetTempPath(), "Types.cs");
            var solution = AddProjectWithDocument(ws, "P1", "Types.cs", code, filePath);

            var extractor = new CodeChunkExtractor();
            var result = await extractor.ExtractAsync(solution, repoRoot: Path.GetTempPath(), branchName: "b", headSha: "h", CancellationToken.None);

            var typeFqns = result.chunks.Where(c => c.ChunkKind == "type_rollup").Select(c => c.TypeFqn).ToHashSet();
            Assert.IsTrue(typeFqns.Contains("N.R"));
            Assert.IsTrue(typeFqns.Contains("N.S"));
            Assert.IsTrue(typeFqns.Contains("N.I"));
            Assert.IsTrue(typeFqns.Contains("N.Outer"));
            Assert.IsTrue(typeFqns.Contains("N.Outer.Inner"));
        }
    }
}
