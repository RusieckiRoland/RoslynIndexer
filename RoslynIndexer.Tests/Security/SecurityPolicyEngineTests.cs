using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using RoslynIndexer.Core.Security.Configuration;
using RoslynIndexer.Core.Security.Engine;

namespace RoslynIndexer.Core.Tests.Security
{
    [TestClass]
    public class SecurityPolicyEngineTests
    {
        [TestMethod]
        public void ComputeSecurity_InheritanceAndOverride_WorkAsExpected()
        {
            var engine = BuildEngine(@"
{
  ""version"": 1,
  ""path_style"": ""windows"",
  ""classification_labels_universe"": [""wewnetrzne"", ""tajne"", ""delikatne""],
  ""defaults"": {
    ""acl_tags"": [""base""],
    ""user_level"": null,
    ""classification_labels"": null
  },
  ""rules"": [
    {
      ""id"": ""domy-ogrody"",
      ""scope"": {
        ""include"": [""C:\\domy\\ogrody\\""],
        ""exclude"": []
      },
      ""set"": {
        ""acl_tags"": [""kot"", ""plot""]
      },
      ""merge"": {
        ""acl_tags"": ""inherit_add"",
        ""classification_labels"": ""inherit_add"",
        ""user_level"": ""inherit_replace""
      }
    },
    {
      ""id"": ""domy-ogrody-ptaki-exception"",
      ""scope"": {
        ""include"": [""C:\\domy\\ogrody\\ptaki\\""],
        ""exclude"": []
      },
      ""set"": {
        ""acl_tags"": [""swinka""],
        ""user_level"": 20
      },
      ""merge"": {
        ""acl_tags"": ""replace"",
        ""classification_labels"": ""inherit_add"",
        ""user_level"": ""replace""
      }
    }
  ],
  ""regex_tag_extractors"": [],
  ""validation"": {
    ""warn_if_both_classification_and_level"": true,
    ""error_on_unknown_classification_label"": true
  }
}");

            var parentPath = @"C:\domy\ogrody\rosliny\file.cs";
            var parent = engine.ComputeSecurity(parentPath);
            CollectionAssert.AreEqual(new[] { "base", "kot", "plot" }, parent.AclTags.ToArray());
            Assert.IsNull(parent.UserLevel);

            var childPath = @"C:\domy\ogrody\ptaki\wrobel.cs";
            var child = engine.ComputeSecurity(childPath);
            CollectionAssert.AreEqual(new[] { "swinka" }, child.AclTags.ToArray());
            Assert.AreEqual(20, child.UserLevel);
        }

        [TestMethod]
        public void ComputeSecurity_Exclude_RemovesParentRule()
        {
            var engine = BuildEngine(@"
{
  ""version"": 1,
  ""path_style"": ""windows"",
  ""classification_labels_universe"": [""wewnetrzne""],
  ""defaults"": {
    ""acl_tags"": [],
    ""user_level"": null,
    ""classification_labels"": null
  },
  ""rules"": [
    {
      ""id"": ""domy-ogrody"",
      ""scope"": {
        ""include"": [""C:\\domy\\ogrody\\""],
        ""exclude"": [""C:\\domy\\ogrody\\ptaki\\""]
      },
      ""set"": {
        ""acl_tags"": [""kot""]
      },
      ""merge"": {
        ""acl_tags"": ""inherit_add"",
        ""classification_labels"": ""inherit_add"",
        ""user_level"": ""inherit_replace""
      }
    }
  ],
  ""regex_tag_extractors"": [],
  ""validation"": {
    ""warn_if_both_classification_and_level"": true,
    ""error_on_unknown_classification_label"": true
  }
}");

            var excluded = engine.ComputeSecurity(@"C:\domy\ogrody\ptaki\wrobel.cs");
            Assert.IsFalse(excluded.AclTags.Contains("kot"), "Excluded subtree should not inherit parent ACL tag.");
        }

        [TestMethod]
        public void ComputeSecurity_RegexExtractor_AddsAclTagFromPath()
        {
            var engine = BuildEngine(@"
{
  ""version"": 1,
  ""path_style"": ""windows"",
  ""classification_labels_universe"": [],
  ""defaults"": {
    ""acl_tags"": [],
    ""user_level"": null,
    ""classification_labels"": null
  },
  ""rules"": [],
  ""regex_tag_extractors"": [
    {
      ""id"": ""cars-category-as-acl"",
      ""enabled"": true,
      ""applies_to"": {
        ""include"": [""C:\\cars\\""],
        ""exclude"": []
      },
      ""pattern"": ""^[A-Za-z]:\\\\cars\\\\(?<category>[^\\\\]+)\\\\"",
      ""emit"": [
        {
          ""target"": ""acl_tags"",
          ""mode"": ""add"",
          ""value_from_group"": ""category""
        }
      ]
    }
  ],
  ""validation"": {
    ""warn_if_both_classification_and_level"": true,
    ""error_on_unknown_classification_label"": true
  }
}");

            var result = engine.ComputeSecurity(@"C:\cars\sport\jan\file.cs");
            CollectionAssert.AreEqual(new[] { "sport" }, result.AclTags.ToArray());
        }

        [TestMethod]
        public void Build_UnknownClassificationLabel_Throws()
        {
            var root = JObject.Parse(@"
{
  ""version"": 1,
  ""path_style"": ""windows"",
  ""classification_labels_universe"": [""wewnetrzne""],
  ""defaults"": {
    ""acl_tags"": [],
    ""user_level"": null,
    ""classification_labels"": null
  },
  ""rules"": [
    {
      ""id"": ""bad-label"",
      ""scope"": {
        ""include"": [""C:\\repo\\""],
        ""exclude"": []
      },
      ""set"": {
        ""classification_labels"": [""tajne""]
      },
      ""merge"": {
        ""acl_tags"": ""inherit_add"",
        ""classification_labels"": ""inherit_add"",
        ""user_level"": ""inherit_replace""
      }
    }
  ],
  ""regex_tag_extractors"": [],
  ""validation"": {
    ""warn_if_both_classification_and_level"": true,
    ""error_on_unknown_classification_label"": true
  }
}");

            Assert.ThrowsException<InvalidOperationException>(() => SecurityConfigFactory.Build(root));
        }

        [TestMethod]
        public void ComputeSecurity_BothClassificationAndLevel_YieldsWarning()
        {
            var engine = BuildEngine(@"
{
  ""version"": 1,
  ""path_style"": ""windows"",
  ""classification_labels_universe"": [""wewnetrzne""],
  ""defaults"": {
    ""acl_tags"": [],
    ""user_level"": null,
    ""classification_labels"": [""wewnetrzne""]
  },
  ""rules"": [
    {
      ""id"": ""level-in-subtree"",
      ""scope"": {
        ""include"": [""C:\\repo\\sub\\""],
        ""exclude"": []
      },
      ""set"": {
        ""user_level"": 7
      },
      ""merge"": {
        ""acl_tags"": ""inherit_add"",
        ""classification_labels"": ""inherit_add"",
        ""user_level"": ""replace""
      }
    }
  ],
  ""regex_tag_extractors"": [],
  ""validation"": {
    ""warn_if_both_classification_and_level"": true,
    ""error_on_unknown_classification_label"": true
  }
}");

            var result = engine.ComputeSecurity(@"C:\repo\sub\file.cs");
            Assert.IsNotNull(result.ClassificationLabels);
            Assert.AreEqual(7, result.UserLevel);
            Assert.IsTrue(result.Warnings.Count > 0, "Expected warning when both labels and user_level are set.");
        }

        private static SecurityPolicyEngine BuildEngine(string json)
        {
            var root = JObject.Parse(json);
            var built = SecurityConfigFactory.Build(root);
            return new SecurityPolicyEngine(built.Config);
        }
    }
}
