using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RoslynIndexer.Core.Sql
{
    /// <summary>
    /// Typed view over optional "dbGraph" section of config.json.
    /// Currently we only care about "entityBaseTypes".
    /// </summary>
    public sealed class DbGraphConfig
    {
        /// <summary>
        /// Shared empty instance when no dbGraph section / no base types are configured.
        /// </summary>
        public static readonly DbGraphConfig Empty = new DbGraphConfig(
            entityBaseTypes: Array.Empty<string>()
        );

        /// <summary>
        /// Fully-qualified base types that mark a POCO as an entity.
        /// Example: "Nop.Core.BaseEntity".
        /// </summary>
        public IReadOnlyList<string> EntityBaseTypes { get; }

        /// <summary>
        /// True when config contains at least one entity base type.
        /// </summary>
        public bool HasEntityBaseTypes => EntityBaseTypes.Count > 0;

        private DbGraphConfig(IReadOnlyList<string> entityBaseTypes)
        {
            EntityBaseTypes = entityBaseTypes;
        }

        /// <summary>
        /// Reads the "dbGraph" section from the root JSON object.
        /// Missing section or invalid structure results in DbGraphConfig.Empty.
        /// </summary>
        public static DbGraphConfig FromJson(JObject? root)
        {
            if (root is null)
                return Empty;

            var dbGraph = root["dbGraph"] as JObject;
            if (dbGraph is null)
                return Empty;

            var list = new List<string>();

            if (dbGraph["entityBaseTypes"] is JArray arr)
            {
                foreach (var token in arr)
                {
                    // Accept only non-empty strings, ignore everything else.
                    var value = token.Type == JTokenType.String
                        ? (string?)token
                        : token.ToString();

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    list.Add(value);
                }
            }

            if (list.Count == 0)
                return Empty;

            return new DbGraphConfig(list);
        }
    }
}
