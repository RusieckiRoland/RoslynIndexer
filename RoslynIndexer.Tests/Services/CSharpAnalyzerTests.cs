using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Services;

namespace RoslynIndexer.Core.Tests.Services
{
    [TestClass]
    public class CSharpAnalyzerTests
    {
        private static AdhocWorkspace NewWorkspace()
        {
            var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
            return new AdhocWorkspace(host);
        }

        [TestMethod]
        public async Task AnalyzeAsync_SimpleSolution_CountsProjectsDocsClassesMethods()
        {
            using var ws = NewWorkspace();

            // Project P1 with one doc and two methods
            var p1 = ws.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P1", "P1", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview)));
            ws.AddDocument(p1.Id, "A.cs", SourceText.From("public class A { public void M(){} public int X()=>42; }"));

            // Project P2 with one doc and one method
            var p2 = ws.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P2", "P2", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview)));
            ws.AddDocument(p2.Id, "B.cs", SourceText.From("public class B { public void N(){} }"));

            var analyzer = new CSharpAnalyzer();
            var result = await analyzer.AnalyzeAsync(ws.CurrentSolution, CancellationToken.None);

            Assert.AreEqual(2, result.ProjectCount);
            Assert.AreEqual(2, result.DocumentCount);
            Assert.AreEqual(2, result.ClassCount);
            Assert.AreEqual(3, result.MethodCount);

            Assert.AreEqual(1, result.PerProjectDocuments["P1"]);
            Assert.AreEqual(1, result.PerProjectDocuments["P2"]);
        }
    }
}
