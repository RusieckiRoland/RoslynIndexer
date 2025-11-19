using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using RoslynIndexer.Core.Sql;

namespace RoslynIndexer.Tests.Sql
{
    [TestClass]
    public class DbGraphConfigTests
    {
        // When root JSON is null, we should get Empty config
        [TestMethod]
        public void FromJson_NullRoot_ReturnsEmpty()
        {
            var cfg = DbGraphConfig.FromJson(null);

            Assert.AreSame(DbGraphConfig.Empty, cfg);
            Assert.IsFalse(cfg.HasEntityBaseTypes);
            Assert.AreEqual(0, cfg.EntityBaseTypes.Count);
        }

        // When there is no "dbGraph" section, we should get Empty config
        [TestMethod]
        public void FromJson_NoDbGraphSection_ReturnsEmpty()
        {
            var root = JObject.Parse(@"{ ""someOtherSection"": { } }");

            var cfg = DbGraphConfig.FromJson(root);

            Assert.AreSame(DbGraphConfig.Empty, cfg);
            Assert.IsFalse(cfg.HasEntityBaseTypes);
            Assert.AreEqual(0, cfg.EntityBaseTypes.Count);
        }

        // When "entityBaseTypes" is an empty array, we should get Empty config
        [TestMethod]
        public void FromJson_EmptyEntityBaseTypes_ReturnsEmpty()
        {
            var root = JObject.Parse(@"
            {
                ""dbGraph"": {
                    ""entityBaseTypes"": [ ]
                }
            }");

            var cfg = DbGraphConfig.FromJson(root);

            Assert.AreSame(DbGraphConfig.Empty, cfg);
            Assert.IsFalse(cfg.HasEntityBaseTypes);
            Assert.AreEqual(0, cfg.EntityBaseTypes.Count);
        }

        // When entityBaseTypes contains non-empty strings, they should be parsed and trimmed,
        // empty / whitespace-only entries should be ignored.
        [TestMethod]
        public void FromJson_ValidEntityBaseTypes_ParsesNonEmptyValues()
        {
            var root = JObject.Parse(@"
            {
                ""dbGraph"": {
                    ""entityBaseTypes"": [
                        ""Nop.Core.BaseEntity"",
                        ""  "",
                        ""MyProject.Domain.EntityBase"",
                        """"
                    ]
                }
            }");

            var cfg = DbGraphConfig.FromJson(root);

            Assert.IsTrue(cfg.HasEntityBaseTypes);
            Assert.AreEqual(2, cfg.EntityBaseTypes.Count);
            Assert.AreEqual("Nop.Core.BaseEntity", cfg.EntityBaseTypes[0]);
            Assert.AreEqual("MyProject.Domain.EntityBase", cfg.EntityBaseTypes[1]);
        }
    }
}
