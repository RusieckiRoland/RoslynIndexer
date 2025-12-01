// RoslynIndexer.Core/Sql/LegacySqlIndexer.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RoslynIndexer.Core.Logging;
using RoslynIndexer.Core.Sql.EfMigrations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RoslynIndexer.Core.Models; // SqlArtifact

namespace RoslynIndexer.Core.Sql
{
    /// <summary>
    /// Exact legacy-compatible SQL/EF indexer.
    /// Writes: sql_bundle/graph/{nodes.csv,edges.csv,graph.json}, docs/bodies/*, manifest.json
    /// </summary>
    public static class LegacySqlIndexer
    {
        // ========================================================
        //  SQL indexing (legacy)
        // ========================================================
        private static readonly SqlScriptGenerator SqlGen =
            new Sql150ScriptGenerator(new SqlScriptGeneratorOptions { IncludeSemicolons = true });

        // Global dbGraph settings injected once from the main config.json (CLI layer).
        public static DbGraphConfig GlobalDbGraphConfig { get; set; } = DbGraphConfig.Empty;

        // Global migrations roots (optional) injected from the main config.json (CLI / Program layer).
        // When empty, AppendEfMigrationEdgesAndNodes falls back to codeRoots (legacy behaviour).
        public static string[] GlobalEfMigrationRoots { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Optional list of additional "hot" method names used to detect inline SQL
        /// inside C# invocation expressions (e.g. custom Query/Execute wrappers).
        ///
        /// These values come from config.json (`inlineSql.extraHotMethods`) and are
        /// merged with the built-in hot methods inside InlineSqlScanner.
        ///
        /// The array is global because the inline-SQL scanner is invoked from the
        /// SQL/EF graph builder and needs a shared, pre-initialized configuration.
        /// </summary>
        public static string[] GlobalInlineSqlHotMethods { get; set; } = Array.Empty<string>();

        // Global inline-SQL roots (optional) injected from the main config.json (CLI / Program layer).
        // When empty, inline-SQL stage is skipped.
        public static string[] GlobalInlineSqlRoots { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Public entry point — identical signature and behavior as in the legacy tool.
        /// </summary>
        public static int Start(string outputDir, string sqlProjectRoot, string efRoot = "")
        {
            try
            {
                // Optional: load dbGraph config when LegacySqlIndexer is used directly
                // (e.g. in EntityBaseTypesTests). In CLI mode Program.Net9 already
                // sets GlobalDbGraphConfig from the main config.json – a separate
                // config.json in outputDir simply nie istnieje, więc niczego tu nie nadpiszemy.
                try
                {
                    var configPath = Path.Combine(outputDir, "config.json");
                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var root = JObject.Parse(json);
                            GlobalDbGraphConfig = DbGraphConfig.FromJson(root);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort: brak lub zły config.json w outputDir nie może
                    // zabić całego procesu – po prostu zostajemy przy Empty.
                    ConsoleLog.Warn("[dbGraph] Failed to load dbGraph config from outputDir: " + ex.Message);
                }

                return RunBuild(outputDir, sqlProjectRoot, efRoot);
            }
            catch (Exception ex)
            {
                ConsoleLog.Error(ex.ToString());
                return 1;
            }
        }

        private static string AppendDirSep(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            if (dir.EndsWith(Path.DirectorySeparatorChar.ToString())) return dir;
            return dir + Path.DirectorySeparatorChar;
        }

        private static void AppendEfEdgesAndNodes(
                  IEnumerable<string> codeRoots,
                  string outDir,
                  ConcurrentDictionary<string, NodeRow> nodes,
                  List<EdgeRow> edges)
        {
            ConsoleLog.Info("EF stage: scanning C# files (mappings, DbSet, raw SQL, POCO entities).");

            // Ensure docs/bodies directory exists – POCO bodies will be written here.
            var docsDir = Path.Combine(outDir, "docs");
            var bodiesDir = Path.Combine(docsDir, "bodies");
            Directory.CreateDirectory(docsDir);
            Directory.CreateDirectory(bodiesDir);

            // We keep a per-run registry of ENTITY bodies so that each entityKey
            // gets at most one body file and one JSONL entry.
            var pocoBodies = new Dictionary<string, EntityBodyArtifact>(StringComparer.OrdinalIgnoreCase);

            var csFiles = codeRoots
                .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
                .ToArray();

            ConsoleLog.Info($"C# files found: {csFiles.Length}");

            var trees = new List<SyntaxTree>();
            foreach (var f in csFiles)
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f));

            // 1) entity -> (schema, table) from EF mappings (if any)
            var entityMap = ExtractEntityMappings(trees);
            ConsoleLog.Info($"[EF] entityMap entries: {entityMap.Count}");

            // 1a) TABLE nodes from previous SQL stage (if any)
            var tableNodes = nodes.Values
                .Where(n => string.Equals(n.Kind, "TABLE", StringComparison.OrdinalIgnoreCase))
                .ToList();
            ConsoleLog.Info($"[EF] TABLE nodes available before EF stage: {tableNodes.Count}");

            // 2) dbGraph settings come ONLY from GlobalDbGraphConfig (set by CLI from main config.json)
            var dbGraphCfg = GlobalDbGraphConfig ?? DbGraphConfig.Empty;

            if (dbGraphCfg.HasEntityBaseTypes)
                ConsoleLog.Info($"[dbGraph] entityBaseTypes: {string.Join(", ", dbGraphCfg.EntityBaseTypes)}");
            else
                ConsoleLog.Info("[dbGraph] entityBaseTypes: <none> (POCO detection off)");

            // 2a) Collect entity names that appear in DbSet<T>/IDbSet<T>.
            var dbSetEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 3) DbSet<T> / IDbSet<T> -> DBSET node + MapsTo TABLE using convention + optional entityMap overrides.
            foreach (var t in trees)
            {
                var root = t.GetRoot();
                foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                {
                    var typeText = prop.Type.ToString();
                    if (!typeText.StartsWith("DbSet<") && !typeText.StartsWith("IDbSet<"))
                        continue;

                    var parts = typeText.Split('<', '>');
                    var typeArg = parts.Length > 1 ? parts[1].Trim() : typeText.Trim();
                    if (string.IsNullOrWhiteSpace(typeArg))
                        continue;

                    var entityName = typeArg.Split('.').Last();
                    if (string.IsNullOrWhiteSpace(entityName))
                        continue;

                    dbSetEntityNames.Add(entityName);

                    // Default EF convention: dbo.EntityName
                    string schema = "dbo";
                    string tableName = entityName;

                    // Optional override from entityMap.
                    if (entityMap.TryGetValue(typeArg, out var map))
                    {
                        schema = map.Item1;
                        tableName = map.Item2;
                    }
                    else if (entityMap.TryGetValue(entityName, out map))
                    {
                        schema = map.Item1;
                        tableName = map.Item2;
                    }
                    else
                    {
                        var foundKey = entityMap.Keys.FirstOrDefault(k =>
                            string.Equals(k, entityName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(k.Split('.').Last(), entityName, StringComparison.OrdinalIgnoreCase));

                        if (foundKey != null)
                        {
                            var m = entityMap[foundKey];
                            schema = m.Item1;
                            tableName = m.Item2;
                        }
                    }

                    var clsDecl = prop.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                    var cls = clsDecl != null ? clsDecl.Identifier.Text : "UnknownClass";
                    var from = "csharp:" + cls + "." + prop.Identifier.Text + "|DBSET";
                    var toKey = schema + "." + tableName + "|TABLE";

                    edges.Add(new EdgeRow(
                        from,
                        toKey,
                        "MapsTo",
                        "TABLE",
                        t.FilePath,
                        null));

                    nodes.TryAdd(
                        from,
                        new NodeRow(from, "DBSET", prop.Identifier.Text, "csharp", t.FilePath, null, "code", null));
                }
            }

            // 4) Entity detection from DbSet<T> and/or configured base types.
            //    - DbSet<T> always marks T as an entity (EF convention).
            //    - dbGraphCfg.EntityBaseTypes can additionally mark POCOs as entities (even without DbSet).
            if (dbSetEntityNames.Count > 0 || dbGraphCfg.HasEntityBaseTypes)
            {
                var hasEntityBaseTypes = dbGraphCfg.HasEntityBaseTypes;

                foreach (var t in trees)
                {
                    var root = t.GetRoot();
                    foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        var entityName = cls.Identifier.Text;
                        var isFromDbSet = dbSetEntityNames.Contains(entityName);
                        bool matchesBase = false;
                        string? baseTypeText = null;

                        if (hasEntityBaseTypes && cls.BaseList != null && cls.BaseList.Types.Count > 0)
                        {
                            var baseTypeSyntax = cls.BaseList.Types.FirstOrDefault();
                            if (baseTypeSyntax != null)
                            {
                                baseTypeText = baseTypeSyntax.ToString();
                                if (!string.IsNullOrWhiteSpace(baseTypeText))
                                {
                                    var baseTypeSimple = baseTypeText.Split('.').Last();

                                    Console.WriteLine($"[ENTITY-DEBUG] Class='{cls.Identifier.Text}', BaseSyntax='{baseTypeText}'");

                                    matchesBase = dbGraphCfg.EntityBaseTypes.Any(cfgType =>
                                    {
                                        if (string.IsNullOrWhiteSpace(cfgType))
                                            return false;

                                        var cfgSimple = cfgType.Split('.').Last();

                                        return string.Equals(baseTypeText, cfgType, StringComparison.Ordinal)
                                               || string.Equals(baseTypeText, cfgSimple, StringComparison.Ordinal)
                                               || string.Equals(baseTypeSimple, cfgSimple, StringComparison.Ordinal);
                                    });

                                    if (!matchesBase)
                                    {
                                        Console.WriteLine(
                                            $"[ENTITY-DEBUG]   -> Base DOES NOT match config for '{cls.Identifier.Text}' (base='{baseTypeText}')");
                                    }
                                }
                            }
                        }

                        // If class is neither referenced by DbSet<T> nor matches configured base type, skip it.
                        if (!isFromDbSet && !matchesBase)
                            continue;

                        if (matchesBase)
                        {
                            Console.WriteLine(
                                $"[ENTITY-DEBUG]   -> Base MATCHES config for '{cls.Identifier.Text}' (base='{baseTypeText}')");
                        }
                        else if (isFromDbSet)
                        {
                            Console.WriteLine(
                                $"[ENTITY-DEBUG]   -> Marked as ENTITY via DbSet<...> for '{cls.Identifier.Text}'");
                        }

                        var entityKey = $"csharp:{entityName}|ENTITY";

                        // 4a) Create POCO body file once per ENTITY and remember it for sql_bodies.jsonl.
                        if (!pocoBodies.ContainsKey(entityKey))
                        {
                            var ns = GetNamespace(cls);
                            var typeFullName = FullName(cls);
                            var typeNameForFile = string.IsNullOrEmpty(typeFullName) ? entityName : typeFullName;

                            var safeTypeName = new string(
                                typeNameForFile
                                    .Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' ? ch : '_')
                                    .ToArray());

                            var bodyFileName = $"Poco.{safeTypeName}.ENTITY.cs";
                            var bodyAbsPath = Path.Combine(bodiesDir, bodyFileName);
                            var bodyRelPath = "docs/bodies/" + bodyFileName;

                            // Extract class text from the syntax tree – we keep exactly the C# class body.
                            var text = t.GetText();
                            var bodyText = text.ToString(cls.Span);

                            File.WriteAllText(bodyAbsPath, bodyText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                            var artifact = new EntityBodyArtifact(
                                entityKey: entityKey,
                                entityName: entityName,
                                @namespace: ns,
                                typeFullName: typeFullName,
                                sourcePath: t.FilePath,
                                bodyRelPath: bodyRelPath,
                                body: bodyText);

                            pocoBodies[entityKey] = artifact;

                            nodes.TryAdd(
                                entityKey,
                                new NodeRow(
                                    entityKey,
                                    "ENTITY",
                                    entityName,
                                    "csharp",
                                    t.FilePath,
                                    null,
                                    "code",
                                    bodyRelPath));
                        }
                        else
                        {
                            // ENTITY node without overwriting an existing bodyPath (if any).
                            nodes.TryAdd(
                                entityKey,
                                new NodeRow(
                                    entityKey,
                                    "ENTITY",
                                    entityName,
                                    "csharp",
                                    t.FilePath,
                                    null,
                                    "code",
                                    pocoBodies[entityKey].BodyRelPath));
                        }

                        Console.WriteLine($"[ENTITY-DEBUG]   -> ENTITY node added for '{entityName}' ({entityKey})");

                        // Best-effort: try to find / create TABLE node to attach MapsTo.
                        bool mapFound = false;
                        string schema = "dbo";  // default EF schema
                        string tableName = entityName;

                        // 4b.1: entityMap (Fluent / EF mappings)
                        if (entityMap.TryGetValue(entityName, out var map2))
                        {
                            schema = map2.Item1;
                            tableName = map2.Item2;
                            mapFound = true;
                            Console.WriteLine(
                                $"[ENTITY-DEBUG]   -> Using entityMap direct for '{entityName}': {schema}.{tableName}");
                        }
                        else
                        {
                            var foundKey = entityMap.Keys.FirstOrDefault(k =>
                                string.Equals(k, entityName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(k.Split('.').Last(), entityName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(k, entityName + "s", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(k + "s", entityName, StringComparison.OrdinalIgnoreCase));

                            if (foundKey != null)
                            {
                                var m = entityMap[foundKey];
                                schema = m.Item1;
                                tableName = m.Item2;
                                mapFound = true;
                                Console.WriteLine(
                                    $"[ENTITY-DEBUG]   -> Using entityMap heuristic for '{entityName}': key='{foundKey}', table={schema}.{tableName}");
                            }
                        }

                        // 4b.2: [Table] attribute on the class
                        if (!mapFound)
                        {
                            var tableAttr = cls.AttributeLists
                                .SelectMany(al => al.Attributes)
                                .FirstOrDefault(a =>
                                {
                                    var n = a.Name.ToString();
                                    return n == "Table"
                                           || n == "TableAttribute"
                                           || n.EndsWith(".Table")
                                           || n.EndsWith(".TableAttribute");
                                });

                            if (tableAttr != null && tableAttr.ArgumentList != null)
                            {
                                foreach (var arg in tableAttr.ArgumentList.Arguments)
                                {
                                    // Positional: table name
                                    if (arg.NameEquals == null &&
                                        arg.Expression is LiteralExpressionSyntax lit &&
                                        lit.IsKind(SyntaxKind.StringLiteralExpression))
                                    {
                                        tableName = lit.Token.ValueText;
                                    }
                                    // Named: Schema = "..."
                                    else if (arg.NameEquals != null &&
                                             arg.NameEquals.Name.Identifier.Text == "Schema" &&
                                             arg.Expression is LiteralExpressionSyntax schemaLit &&
                                             schemaLit.IsKind(SyntaxKind.StringLiteralExpression))
                                    {
                                        schema = schemaLit.Token.ValueText;
                                    }
                                }

                                mapFound = true;
                                Console.WriteLine(
                                    $"[ENTITY-DEBUG]   -> Using [Table] attribute for '{entityName}': {schema}.{tableName}");
                            }
                        }

                        // 4b.3: fallback – existing TABLE from SQL stage (if any)
                        if (!mapFound && tableNodes.Count > 0)
                        {
                            var tableNode = tableNodes.FirstOrDefault(n =>
                                string.Equals(n.Name, entityName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(n.Name, entityName + "s", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(n.Name + "s", entityName, StringComparison.OrdinalIgnoreCase));

                            if (tableNode != null)
                            {
                                schema = string.IsNullOrEmpty(tableNode.Schema) ? "dbo" : tableNode.Schema;
                                tableName = tableNode.Name;
                                mapFound = true;
                                Console.WriteLine(
                                    $"[ENTITY-DEBUG]   -> Using TABLE node fallback for '{entityName}': {schema}.{tableName}");
                            }
                        }

                        // 4b.4: as a last resort, if class came from DbSet<T>, use convention mapping.
                        if (!mapFound && isFromDbSet)
                        {
                            mapFound = true;
                            Console.WriteLine(
                                $"[ENTITY-DEBUG]   -> Using convention mapping for '{entityName}': {schema}.{tableName} (from DbSet<...>)");
                        }

                        // 4c) If there is still no TABLE mapping – leave ENTITY alone.
                        if (!mapFound)
                        {
                            Console.WriteLine(
                                $"[ENTITY-DEBUG]   -> NO TABLE mapping for '{entityName}' – ENTITY without MapsTo.");
                            continue;
                        }

                        var tableKey = $"{schema}.{tableName}|TABLE";

                        // Ensure TABLE node exists (existing SQL node is preserved by TryAdd).
                        nodes.TryAdd(
                            tableKey,
                            new NodeRow(
                                tableKey,
                                "TABLE",
                                tableName,
                                schema,
                                t.FilePath,
                                null,
                                "ef",
                                null));

                        edges.Add(new EdgeRow(
                            entityKey,
                            tableKey,
                            "MapsTo",
                            "TABLE",
                            t.FilePath,
                            null));

                        Console.WriteLine(
                            $"[ENTITY-DEBUG]   -> ENTITY + MapsTo added for '{entityName}' => {tableKey}");
                    }
                }

             
            }
          
            AppendEntityBodiesToJsonl(outDir, pocoBodies.Values.ToList());
            AppendEfFluentForeignKeyEdges(trees, entityMap, nodes, edges);

        }

        /// <summary>
        /// Detects ForeignKey relationships defined via EF Fluent API
        /// (HasOne/HasMany + HasForeignKey) and emits TABLE→TABLE (ForeignKey) edges.
        /// </summary>
        private static void AppendEfFluentForeignKeyEdges(
            IEnumerable<SyntaxTree> trees,
            Dictionary<string, Tuple<string, string>> entityMap,
            ConcurrentDictionary<string, NodeRow> nodes,
            List<EdgeRow> edges)
        {
            if (trees == null)
                return;

            foreach (var tree in trees)
            {
                if (tree == null)
                    continue;

                var root = tree.GetRoot();
                if (root == null)
                    continue;

                // Look for Fluent API chains that end with:
                //   .HasOne(...).WithMany(...).HasForeignKey(...)
                //   .HasMany(...).WithOne(...).HasForeignKey(...)
                // We always walk the chain backwards from HasForeignKey to find
                // the HasOne/HasMany segment and the root Entity<T>() call.
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax fkMember)
                        continue;

                    if (!string.Equals(fkMember.Name.Identifier.Text, "HasForeignKey", StringComparison.Ordinal))
                        continue;

                    // Step 1: walk back to the HasOne/HasMany invocation in the chain.
                    InvocationExpressionSyntax navInvocation = null;
                    MemberAccessExpressionSyntax navMember = null;

                    ExpressionSyntax currentExpr = fkMember.Expression;
                    while (currentExpr is InvocationExpressionSyntax currentInv)
                    {
                        if (currentInv.Expression is not MemberAccessExpressionSyntax currentMember)
                            break;

                        var nameText = currentMember.Name.Identifier.Text;
                        if (string.Equals(nameText, "HasOne", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(nameText, "HasMany", StringComparison.OrdinalIgnoreCase))
                        {
                            navInvocation = currentInv;
                            navMember = currentMember;
                            break;
                        }

                        // Skip WithMany / WithOne / etc. and keep walking left.
                        currentExpr = currentMember.Expression;
                    }

                    if (navInvocation == null || navMember == null)
                        continue;

                    var navName = navMember.Name.Identifier.Text;
                    var isHasOne = string.Equals(navName, "HasOne", StringComparison.OrdinalIgnoreCase);
                    var isHasMany = string.Equals(navName, "HasMany", StringComparison.OrdinalIgnoreCase);
                    if (!isHasOne && !isHasMany)
                        continue;

                    // Step 2: resolve relatedType from HasOne/HasMany generic argument
                    // or from simple lambda body (c => c.Parent).
                    string relatedType = null;

                    // Generic form: HasOne<Parent>() / HasMany<Child>()
                    if (navMember.Name is GenericNameSyntax hasGeneric &&
                        hasGeneric.TypeArgumentList != null &&
                        hasGeneric.TypeArgumentList.Arguments.Count == 1)
                    {
                        relatedType = hasGeneric.TypeArgumentList.Arguments[0].ToString().Trim();
                    }

                    // Expression form: HasOne(c => c.Parent)
                    if (string.IsNullOrWhiteSpace(relatedType) &&
                        navInvocation.ArgumentList != null &&
                        navInvocation.ArgumentList.Arguments.Count > 0)
                    {
                        var firstArgExpr = navInvocation.ArgumentList.Arguments[0].Expression;
                        if (firstArgExpr is SimpleLambdaExpressionSyntax lambda)
                        {
                            var body = lambda.Body;

                            if (body is MemberAccessExpressionSyntax bodyAccess)
                            {
                                // c => c.Parent  -> "Parent"
                                relatedType = bodyAccess.Name.Identifier.Text;
                            }
                            else if (body is IdentifierNameSyntax id)
                            {
                                relatedType = id.Identifier.Text;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(relatedType))
                        continue;

                    // Step 3: walk back from HasOne/HasMany to find the Entity<T>() root.
                    string entityType = null;

                    ExpressionSyntax entityExpr = navMember.Expression;
                    while (entityExpr is InvocationExpressionSyntax entityInv)
                    {
                        if (entityInv.Expression is not MemberAccessExpressionSyntax entityMember)
                            break;

                        // We only care about "...Entity<Child>()" part.
                        if (entityMember.Name is GenericNameSyntax entityGeneric &&
                            string.Equals(entityGeneric.Identifier.Text, "Entity", StringComparison.Ordinal) &&
                            entityGeneric.TypeArgumentList.Arguments.Count == 1)
                        {
                            entityType = entityGeneric.TypeArgumentList.Arguments[0].ToString().Trim();
                            break;
                        }

                        entityExpr = entityMember.Expression;
                    }

                    if (string.IsNullOrWhiteSpace(entityType))
                        continue;

                    // Step 4: map EF types -> TABLE (schema, name).
                    string entitySchema, entityTable;
                    string relatedSchema, relatedTable;

                    if (!TryResolveEntityToTable(entityType, entityMap, out entitySchema, out entityTable))
                        continue;

                    if (!TryResolveEntityToTable(relatedType, entityMap, out relatedSchema, out relatedTable))
                        continue;

                    // Decide direction:
                    //  - Entity<Child>().HasOne(Parent)  => Child -> Parent
                    //  - Entity<Parent>().HasMany(Child) => Child -> Parent
                    string childSchema, childTable, parentSchema, parentTable;

                    if (isHasOne)
                    {
                        childSchema = entitySchema;
                        childTable = entityTable;
                        parentSchema = relatedSchema;
                        parentTable = relatedTable;
                    }
                    else
                    {
                        childSchema = relatedSchema;
                        childTable = relatedTable;
                        parentSchema = entitySchema;
                        parentTable = entityTable;
                    }

                    var childKey = $"{childSchema}.{childTable}|TABLE";
                    var parentKey = $"{parentSchema}.{parentTable}|TABLE";

                    var childName = childTable;
                    var parentName = parentTable;

                    // Ensure TABLE nodes exist for both ends (same pattern as other EF edges).
                    nodes.TryAdd(
                        childKey,
                        new NodeRow(
                            childKey,
                            "TABLE",
                            childName,
                            childSchema,
                            tree.FilePath ?? string.Empty,
                            null,
                            "ef",
                            null));

                    nodes.TryAdd(
                        parentKey,
                        new NodeRow(
                            parentKey,
                            "TABLE",
                            parentName,
                            parentSchema,
                            tree.FilePath ?? string.Empty,
                            null,
                            "ef",
                            null));

                    // Finally: TABLE -> TABLE (ForeignKey).
                    edges.Add(
                        new EdgeRow(
                            childKey,
                            parentKey,
                            "ForeignKey",
                            "TABLE",
                            tree.FilePath ?? string.Empty,
                            null));
                }
            }
        }


        /// <summary>
        /// Resolves EF entity type name to (schema, table) using entityMap + EF conventions.
        /// </summary>
        private static bool TryResolveEntityToTable(
            string entityType,
            Dictionary<string, Tuple<string, string>> entityMap,
            out string schema,
            out string tableName)
        {
            schema = "dbo";
            tableName = null;

            if (string.IsNullOrWhiteSpace(entityType))
                return false;

            var simple = entityType.Split('.').Last();

            if (entityMap != null)
            {
                Tuple<string, string> map;

                // 1) Exact key, e.g. "MiniEf.Child"
                if (entityMap.TryGetValue(entityType, out map))
                {
                    schema = string.IsNullOrWhiteSpace(map.Item1) ? "dbo" : map.Item1;
                    tableName = !string.IsNullOrWhiteSpace(map.Item2) ? map.Item2 : simple;
                    return !string.IsNullOrWhiteSpace(tableName);
                }

                // 2) Simple key, e.g. "Child"
                if (!string.IsNullOrWhiteSpace(simple) &&
                    entityMap.TryGetValue(simple, out map))
                {
                    schema = string.IsNullOrWhiteSpace(map.Item1) ? "dbo" : map.Item1;
                    tableName = !string.IsNullOrWhiteSpace(map.Item2) ? map.Item2 : simple;
                    return !string.IsNullOrWhiteSpace(tableName);
                }
            }

            // 3) Pure convention: dbo.SimpleName
            if (!string.IsNullOrWhiteSpace(simple))
            {
                tableName = simple;
                return true;
            }

            return false;
        }





        private static void AppendEfMigrationEdgesAndNodes(
            IEnumerable<string> codeRoots,
            string outDir,
            ConcurrentDictionary<string, NodeRow> nodes,
            List<EdgeRow> edges)
        {
            ConsoleLog.Info("EF-Migrations (v2): Roslyn-based EfMigrationAnalyzer…");

            // 1) Collect all roots to scan:
            //    - explicit GlobalEfMigrationRoots from config.json,
            //    - plus codeRoots (for backward compatibility), without duplicates.
            var effectiveRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (GlobalEfMigrationRoots != null)
            {
                foreach (var raw in GlobalEfMigrationRoots)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var dir = NormalizeDir(raw);
                    if (!Directory.Exists(dir))
                    {
                        ConsoleLog.Warn("EF-Migrations: directory from GlobalEfMigrationRoots does not exist: " + dir);
                        continue;
                    }

                    effectiveRoots.Add(dir);
                }
            }

            var codeRootsArray = (codeRoots ?? Array.Empty<string>()).ToArray();

            if (effectiveRoots.Count == 0)
            {
                // No explicit config – fall back to legacy behaviour: scan codeRoots only.
                foreach (var raw in codeRootsArray)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var dir = NormalizeDir(raw);
                    if (!Directory.Exists(dir))
                    {
                        ConsoleLog.Warn("EF-Migrations: directory does not exist: " + dir);
                        continue;
                    }

                    effectiveRoots.Add(dir);
                }
            }
            else
            {
                // We *do* have explicit migration roots – still allow migrations living next to codeRoots.
                foreach (var raw in codeRootsArray)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var dir = NormalizeDir(raw);
                    if (Directory.Exists(dir))
                        effectiveRoots.Add(dir);
                }
            }

            if (effectiveRoots.Count == 0)
            {
                ConsoleLog.Info("EF-Migrations (v2): no migration roots configured/found – skipped.");
                return;
            }

            ConsoleLog.Info("EF-Migrations (v2): migrations roots:");
            foreach (var root in effectiveRoots)
                ConsoleLog.Info("  " + root);

            var docsDir = Path.Combine(outDir, "docs");
            var bodiesDir = Path.Combine(docsDir, "bodies");
            Directory.CreateDirectory(docsDir);
            Directory.CreateDirectory(bodiesDir);

            var analyzer = new EfMigrationAnalyzer();
            int addedMigrations = 0;
            int addedEdges = 0;

            var migrationBodies = new List<MigrationBodyArtifact>();

            // 2) Primary path: use EfMigrationAnalyzer (for real projects like nopCommerce).
            foreach (var root in effectiveRoots)
            {
                try
                {
                    var infos = analyzer.Analyze(root, repoRoot: string.Empty, cancellationToken: CancellationToken.None);
                    if (infos == null || infos.Count == 0)
                        continue;

                    foreach (var mig in infos)
                    {
                        if (mig == null || string.IsNullOrEmpty(mig.ClassName))
                            continue;

                        var migrationName = !string.IsNullOrEmpty(mig.TypeFullName)
                            ? mig.TypeFullName
                            : mig.ClassName;

                        var migrationKey = "csharp:" + migrationName + "|MIGRATION";

                        // Prepare body file for Up(), if available.
                        string bodyRelPath = null;
                        var upBody = mig.UpBody ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(upBody))
                        {
                            var safeName = new string(
                                migrationName
                                    .Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' ? ch : '_')
                                    .ToArray());

                            var bodyFileName = $"Migration.{safeName}.MIGRATION.cs";
                            var bodyAbs = Path.Combine(bodiesDir, bodyFileName);

                            File.WriteAllText(bodyAbs, upBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                            bodyRelPath = "docs/bodies/" + bodyFileName;

                            // Register for sql_bodies.jsonl.
                            migrationBodies.Add(
                                new MigrationBodyArtifact(
                                    migrationKey: migrationKey,
                                    migrationName: mig.ClassName,
                                    @namespace: mig.Namespace,
                                    typeFullName: mig.TypeFullName,
                                    sourcePath: string.IsNullOrEmpty(mig.SourcePath) ? (mig.FileRelativePath ?? string.Empty) : mig.SourcePath,
                                    bodyRelPath: bodyRelPath,
                                    body: upBody,
                                    operations: mig.UpOperations));
                        }

                        // Migration node in the graph.
                        nodes.TryAdd(
                            migrationKey,
                            new NodeRow(
                                key: migrationKey,
                                kind: "MIGRATION",
                                name: mig.ClassName,
                                schema: "csharp",
                                file: mig.FileRelativePath ?? string.Empty,
                                batch: null,
                                domain: "code",
                                bodyPath: bodyRelPath));

                        addedMigrations++;

                        // Edges: use UpOperations (schema evolution "forward").
                        foreach (var op in mig.UpOperations ?? Array.Empty<EfMigrationOperation>())
                        {
                            string relation;
                            switch (op.Kind)
                            {
                                case EfMigrationOperationKind.CreateTable:
                                case EfMigrationOperationKind.DropTable:
                                case EfMigrationOperationKind.CreateIndex:
                                case EfMigrationOperationKind.DropIndex:
                                case EfMigrationOperationKind.TouchTable:
                                    relation = "SchemaChange";
                                    break;

                                case EfMigrationOperationKind.RawSql:
                                case EfMigrationOperationKind.Unknown:
                                case EfMigrationOperationKind.DataChange:
                                default:
                                    // Raw SQL and pure data changes are preserved only in the MIGRATION body summary.
                                    continue;
                            }

                            var tableName = op.Table;
                            if (string.IsNullOrWhiteSpace(tableName))
                                continue;

                            // For now we default to dbo – SQL side may refine this later.
                            var tableKey = "dbo." + tableName + "|TABLE";

                            // MIGRATION -> TABLE edge (schema evolution information).
                            edges.Add(new EdgeRow(
                                from: migrationKey,
                                to: tableKey,
                                relation: relation,
                                toKind: "TABLE",
                                file: mig.FileRelativePath ?? string.Empty,
                                batch: null));

                            // TABLE -> TABLE ForeignKey edge for AddForeignKey operations.
                            // We intentionally do not emit TABLE->TABLE for DropForeignKey – the static graph
                            // is meant to represent current logical relationships; drops are handled by consumers.
                            if (string.Equals(op.Operation, "AddForeignKey", StringComparison.Ordinal))
                            {
                                var principalTable = TryGetPrincipalTableFromRaw(op);
                                if (!string.IsNullOrWhiteSpace(principalTable))
                                {
                                    var principalKey = "dbo." + principalTable + "|TABLE";

                                    edges.Add(new EdgeRow(
                                        from: tableKey,          // dependent table, e.g. dbo.Orders|TABLE
                                        to: principalKey,        // principal table, e.g. dbo.Customers|TABLE
                                        relation: "ForeignKey",
                                        toKind: "TABLE",
                                        file: mig.FileRelativePath ?? string.Empty,
                                        batch: null));
                                }
                            }

                            addedEdges++;
                        }


                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog.Warn("EF-Migrations (v2): failed to analyze root '" + root + "': " + ex.Message);
                }

                // Local helper: best-effort principalTable extraction from EF migration DSL.
                // Example we expect in op.Raw:
                //   MigrationBuilder.AddForeignKey(
                //       name: "FK_Orders_Customers_CustomerId",
                //       table: "Orders",
                //       column: "CustomerId",
                //       principalTable: "Customers",
                //       principalColumn: "Id", ...);
                static string TryGetPrincipalTableFromRaw(EfMigrationOperation op)
                {
                    if (op == null)
                        return null;

                    var raw = op.Raw;
                    if (string.IsNullOrEmpty(raw))
                        return null;

                    const string marker = "principalTable";
                    var idx = raw.IndexOf(marker, StringComparison.Ordinal);
                    if (idx < 0)
                        return null;

                    // Find first quote after "principalTable"
                    var quoteStart = raw.IndexOf('"', idx);
                    if (quoteStart < 0 || quoteStart + 1 >= raw.Length)
                        return null;

                    var quoteEnd = raw.IndexOf('"', quoteStart + 1);
                    if (quoteEnd < 0 || quoteEnd <= quoteStart + 1)
                        return null;

                    return raw.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                }


            }

            // 3) Fallback path (MiniEf) – bez zmian, tylko korzysta z tych samych zmiennych
            if (addedMigrations == 0)
            {
                int fallbackMigrations = 0;
                int fallbackEdges = 0;

                foreach (var root in effectiveRoots)
                {
                    IEnumerable<string> csFiles;
                    try
                    {
                        csFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        ConsoleLog.Warn("EF-Migrations (fallback): cannot enumerate *.cs under " + root + ": " + ex.Message);
                        continue;
                    }

                    foreach (var file in csFiles)
                    {
                        string code;
                        try
                        {
                            code = File.ReadAllText(file);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                        {
                            ConsoleLog.Warn("EF-Migrations (fallback): cannot read " + file + ": " + ex.Message);
                            continue;
                        }

                        // Find classes ending with 'Migration'
                        var classMatches = Regex.Matches(code, @"class\s+(\w*Migration)\b");
                        if (classMatches.Count == 0)
                            continue;

                        // Extract table names from Schema.Table(...) calls.
                        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Schema.Table(nameof(Customer))
                        var nameofMatches = Regex.Matches(code, @"Schema\.Table\(\s*nameof\(\s*(\w+)\s*\)\s*\)");
                        foreach (Match m in nameofMatches)
                        {
                            var name = m.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(name))
                                tableNames.Add(name);
                        }

                        // Schema.Table("Customer")
                        var stringMatches = Regex.Matches(code, @"Schema\.Table\(\s*""([^""]+)""\s*\)");
                        foreach (Match m in stringMatches)
                        {
                            var name = m.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(name))
                                tableNames.Add(name);
                        }

                        foreach (Match m in classMatches)
                        {
                            var className = m.Groups[1].Value;
                            var migKey = "csharp:" + className + "|MIGRATION";

                            nodes.TryAdd(
                                migKey,
                                new NodeRow(
                                    key: migKey,
                                    kind: "MIGRATION",
                                    name: className,
                                    schema: "csharp",
                                    file: file,
                                    batch: null,
                                    domain: "code",
                                    bodyPath: null));

                            fallbackMigrations++;

                            foreach (var tableName in tableNames)
                            {
                                var tableKey = "dbo." + tableName + "|TABLE";

                                edges.Add(new EdgeRow(
                                    from: migKey,
                                    to: tableKey,
                                    relation: "SchemaChange",
                                    toKind: "TABLE",
                                    file: file,
                                    batch: null));

                                fallbackEdges++;
                            }
                        }
                    }
                }

                if (fallbackMigrations > 0)
                {
                    addedMigrations += fallbackMigrations;
                    addedEdges += fallbackEdges;
                    ConsoleLog.Info(
                        "EF-Migrations (fallback) added: migrations=" + fallbackMigrations +
                        ", edges=" + fallbackEdges);
                }
            }

            // 4) Persist MIGRATION bodies/summaries to sql_bodies.jsonl (historical source only).
            AppendMigrationBodiesToJsonl(outDir, migrationBodies);

            ConsoleLog.Info(
                "EF-Migrations (v2) finished: migrations=" + addedMigrations +
                ", edges(Up)=" + addedEdges +
                ", total nodes=" + nodes.Count +
                ", total edges=" + edges.Count);
        }

        /// <summary>
        /// Appends POCO ENTITY bodies to sql_bodies.jsonl.
        /// Each artifact represents a single C# entity class that was treated as ENTITY
        /// (either via DbSet&lt;T&gt; or configured EntityBaseTypes).
        /// </summary>
        private static void AppendEntityBodiesToJsonl(
            string outputDir,
            IReadOnlyList<EntityBodyArtifact> entityBodies)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
                return;

            if (entityBodies == null || entityBodies.Count == 0)
                return;

            var docsDir = Path.Combine(outputDir, "docs");
            Directory.CreateDirectory(docsDir);

            // Same file as SQL and inline SQL bodies; POCO ENTITY entries are just another kind.
            var bodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");

            try
            {
                using (var stream = new FileStream(bodiesPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    foreach (var e in entityBodies)
                    {
                        if (e == null)
                            continue;

                        var jsonObject = new
                        {
                            key = e.EntityKey,          // e.g. "csharp:Product|ENTITY"
                            kind = "ENTITY",
                            name = e.EntityName,
                            @namespace = e.Namespace,
                            typeFullName = e.TypeFullName,
                            file = e.SourcePath,        // full C# path
                            bodyPath = e.BodyRelPath,   // docs/bodies/Poco....
                            body = e.Body               // raw C# class text
                        };

                        writer.WriteLine(JsonConvert.SerializeObject(jsonObject));
                    }
                }
            }
            catch (Exception ex)
            {
                // Do not fail the entire indexer – POCO bodies are helpful but not critical.
                ConsoleLog.Warn("ENTITY bodies (POCO): failed to append POCO bodies to sql_bodies.jsonl: " + ex.Message);
            }
        }

        // Appends inline-SQL entries to sql_bodies.jsonl and creates .sql body files
        // in docs/bodies for InlineSQL artifacts originating from C# code.
        // Tests only require that methodFullName appears in the JSONL; additional fields
        // (origin, body, bodyPath, C# context) are safe to add.
        private static void AppendInlineSqlBodiesToJsonl(
            string outputDir,
            IReadOnlyList<SqlArtifact> artifacts)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
                return;

            if (artifacts == null || artifacts.Count == 0)
                return;

            var docsDir = Path.Combine(outputDir, "docs");
            var bodiesDir = Path.Combine(docsDir, "bodies");

            // Ensure both docs and bodies directories exist.
            Directory.CreateDirectory(docsDir);
            Directory.CreateDirectory(bodiesDir);

            // Path consistent with BuildSqlKnowledge output:
            //   <outputDir>\docs\sql_bodies.jsonl
            var bodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");

            try
            {
                // Append mode — do not overwrite content already written by the SQL parser (.sql files).
                using (var stream = new FileStream(bodiesPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    foreach (var a in artifacts)
                    {
                        if (a == null)
                            continue;

                        if (!string.Equals(a.ArtifactKind, "InlineSQL", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (string.IsNullOrWhiteSpace(a.MethodFullName))
                            continue;

                        var sqlText = a.Body ?? string.Empty;
                        string? bodyRelPath = null;

                        if (!string.IsNullOrEmpty(sqlText))
                        {
                            // Build a stable file name that can be traced back to the method and line.
                            var safeMethodName = new string(
                                a.MethodFullName
                                    .Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' ? ch : '_')
                                    .ToArray());

                            var linePart = a.LineNumber.HasValue
                                ? "L" + a.LineNumber.Value.ToString()
                                : "L0";

                            var fileName = $"InlineSql.{safeMethodName}.{linePart}.sql";
                            var bodyAbsPath = Path.Combine(bodiesDir, fileName);

                            // Overwrite is fine — for a given method+line the SQL body is deterministic.
                            File.WriteAllText(bodyAbsPath, sqlText, new UTF8Encoding(false));
                            bodyRelPath = "docs/bodies/" + fileName;
                        }

                        // Rich JSONL entry for InlineSQL, keeping methodFullName for tests.
                        var jsonObject = new
                        {
                            key = a.MethodFullName,
                            kind = "InlineSQL",
                            methodFullName = a.MethodFullName,
                            bodyOrigin = a.BodyOrigin,          // "HotMethod" / "ExtraHotMethod" / "HeuristicRoslyn" / "HeuristicFallback"
                            @namespace = a.Namespace,
                            typeFullName = a.TypeFullName,
                            relativePath = a.RelativePath,      // C# file relative to inlineSql root
                            file = a.SourcePath,                // full C# path as seen by scanner
                            lineNumber = a.LineNumber,
                            bodyPath = bodyRelPath,             // docs/bodies/InlineSql....sql (or null if body is empty)
                            body = sqlText                      // raw inline SQL text
                        };

                        writer.WriteLine(JsonConvert.SerializeObject(jsonObject));
                    }
                }
            }
            catch (Exception ex)
            {
                // Do not fail the entire indexer — only log a warning if inline entries cannot be appended.
                ConsoleLog.Warn("Inline-SQL (scanner): failed to append inline SQL bodies to sql_bodies.jsonl: " + ex.Message);
            }
        }

        /// <summary>
        /// Adapter that takes InlineSQL SqlArtifact instances (produced by InlineSqlScanner)
        /// and projects them onto the legacy graph model:
        ///   - METHOD node: csharp:{MethodFullName}|METHOD
        ///   - ReadsFrom edge: METHOD -> {schema}.{name}|{kind}
        ///
        /// This method is NOT yet wired into RunBuild; it is safe/unused until the switch.
        /// </summary>

        private static void AppendInlineSqlEdgesAndNodes_FromArtifacts(
    IEnumerable<SqlArtifact> artifacts,
    string sqlRoot,
    ConcurrentDictionary<string, NodeRow> nodes,
    List<EdgeRow> edges)
        {
            if (artifacts == null)
            {
                ConsoleLog.Info("Inline-SQL (scanner): no artifacts provided – skipped.");
                return;
            }

            var prepared = new List<SqlArtifact>();

            foreach (var a in artifacts)
            {
                if (a == null)
                    continue;

                if (!string.Equals(a.ArtifactKind, "InlineSQL", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Fallback: if scanner did not populate MethodFullName,
                // try to recover method context from the C# file.
                if (string.IsNullOrWhiteSpace(a.MethodFullName))
                {
                    TryPopulateMethodContextFromFile(a);
                }

                if (string.IsNullOrWhiteSpace(a.MethodFullName))
                    continue;

                prepared.Add(a);
            }

            if (prepared.Count == 0)
            {
                ConsoleLog.Info("Inline-SQL (scanner): no InlineSQL artifacts with method context – skipped.");
                return;
            }

            int addedMethods = 0;
            int addedEdges = 0;

            // Group by MethodFullName so each C# method becomes a single METHOD node.
            var groups = prepared.GroupBy(a => a.MethodFullName);

            foreach (var group in groups)
            {
                var methodFullName = group.Key;
                if (string.IsNullOrWhiteSpace(methodFullName))
                    continue;

                var first = group.First();

                // Keep legacy behavior: File column is path relative to sqlRoot.
                var fileRel = GetRelativePathSafe(sqlRoot, first.SourcePath);

                var methodKey = "csharp:" + methodFullName + "|METHOD";

                // Ensure METHOD node exists (TryAdd is idempotent).
                nodes.TryAdd(
                    methodKey,
                    new NodeRow(
                        key: methodKey,
                        kind: "METHOD",
                        name: methodFullName,
                        schema: "csharp",
                        file: fileRel,
                        batch: null,
                        domain: "code-inline-sql",
                        bodyPath: null));

                addedMethods++;

                foreach (var art in group)
                {
                    if (!TryParseInlineSqlIdentifier(art.Identifier, out var schema, out var name, out var kind))
                        continue;

                    var tableKey = string.Format("{0}.{1}|{2}", schema, name, kind);

                    // METHOD -> TABLE_OR_VIEW (ReadsFrom), same as before.
                    edges.Add(new EdgeRow(
                        from: methodKey,
                        to: tableKey,
                        relation: "ReadsFrom",
                        toKind: kind,
                        file: fileRel,
                        batch: null));

                    addedEdges++;

                    // NEW: TABLE -> TABLE (ForeignKey) edges from inline SQL
                    addedEdges += AppendInlineSqlForeignKeyEdgesFromArtifact(
                        artifact: art,
                        fileRel: fileRel,
                        edges: edges);
                }
            }

            ConsoleLog.Info(
                $"Inline-SQL (scanner): added {addedMethods} METHOD node(s) and {addedEdges} edge(s) from SqlArtifact stream.");
        }

        /// <summary>
        /// Detects FOREIGN KEY ... REFERENCES ... patterns in an inline SQL snippet
        /// and appends TABLE -> TABLE (ForeignKey) edges.
        /// Returns the number of added edges.
        /// </summary>
        private static int AppendInlineSqlForeignKeyEdgesFromArtifact(
            SqlArtifact artifact,
            string fileRel,
            List<EdgeRow> edges)
        {
            if (artifact == null)
                return 0;

            var body = artifact.Body;
            if (string.IsNullOrWhiteSpace(body))
                return 0;

            // Cheap guard: avoid regex work when obviously not a FK definition.
            if (body.IndexOf("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) < 0 ||
                body.IndexOf("REFERENCES", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return 0;
            }

            // Child table comes from InlineSQL identifier (same logic as for ReadsFrom).
            if (!TryParseInlineSqlIdentifier(artifact.Identifier, out var childSchema, out var childTable, out var _))
                return 0;

            if (string.IsNullOrWhiteSpace(childTable))
                return 0;

            var effectiveChildSchema = string.IsNullOrWhiteSpace(childSchema) ? "dbo" : childSchema;
            var childKey = string.Format("{0}.{1}|TABLE", effectiveChildSchema, childTable);

            int added = 0;

            foreach (var parent in ExtractForeignKeyTargetsFromInlineBody(body, effectiveChildSchema))
            {
                var parentSchema = parent.Item1;
                var parentTable = parent.Item2;

                if (string.IsNullOrWhiteSpace(parentTable))
                    continue;

                var parentKey = string.Format("{0}.{1}|TABLE", parentSchema, parentTable);

                edges.Add(new EdgeRow(
                    from: childKey,
                    to: parentKey,
                    relation: "ForeignKey",
                    toKind: "TABLE",
                    file: fileRel,
                    batch: null));

                added++;
            }

            return added;
        }


        /// <summary>
        /// Extracts referenced table identifiers from inline SQL FOREIGN KEY definitions.
        /// This is a lightweight, text-based parser for common
        /// "FOREIGN KEY (...) REFERENCES [schema].[Table](...)" patterns.
        /// </summary>
        private static IEnumerable<Tuple<string, string>> ExtractForeignKeyTargetsFromInlineBody(
            string body,
            string defaultSchema)
        {
            if (string.IsNullOrWhiteSpace(body))
                yield break;

            // Very small, culture-invariant regex; it does not try to cover all T-SQL,
            // just enough to capture typical FOREIGN KEY ... REFERENCES patterns.
            var regex = new Regex(
                @"\bFOREIGN\s+KEY\b[^;]*?\bREFERENCES\b\s+(?<table>(\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(\s*\.\s*(\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            foreach (Match match in regex.Matches(body))
            {
                if (!match.Success)
                    continue;

                var raw = match.Groups["table"].Value;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                raw = raw.Trim();

                string schema;
                string name;

                var parts = raw.Split('.');
                if (parts.Length == 2)
                {
                    schema = UnwrapInlineIdentifierPart(parts[0]);
                    name = UnwrapInlineIdentifierPart(parts[1]);
                }
                else
                {
                    schema = string.IsNullOrWhiteSpace(defaultSchema) ? "dbo" : defaultSchema;
                    name = UnwrapInlineIdentifierPart(raw);
                }

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                yield return Tuple.Create(schema, name);
            }
        }

        /// <summary>
        /// Removes brackets and quotes from identifier parts like [dbo], [Order], "dbo".
        /// </summary>
        private static string UnwrapInlineIdentifierPart(string part)
        {
            if (string.IsNullOrEmpty(part))
                return string.Empty;

            var p = part.Trim();

            if (p.Length >= 2 && p[0] == '[' && p[p.Length - 1] == ']')
            {
                p = p.Substring(1, p.Length - 2);
            }

            p = p.Trim('"', '\'');

            return p.Trim();
        }


        /// <summary>
        /// New inline-SQL path: use InlineSqlScanner to produce SqlArtifact
        /// and then project them onto the legacy graph via
        /// AppendInlineSqlEdgesAndNodes_FromArtifacts.
        /// </summary>
        private static void AppendInlineSqlEdgesAndNodes_UsingScanner(
                IEnumerable<string> inlineSqlRoots,
                string sqlRoot,
                IEnumerable<string>? extraHotMethods,
                string outputDir,
                ConcurrentDictionary<string, NodeRow> nodes,
                List<EdgeRow> edges)
        {
            if (inlineSqlRoots == null)
            {
                ConsoleLog.Info("Inline-SQL (scanner): inlineSql roots are null – skipped.");
                return;
            }

            var rootsList = inlineSqlRoots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();

            if (rootsList.Count == 0)
            {
                ConsoleLog.Info("Inline-SQL (scanner): no non-empty inlineSql roots – skipped.");
                return;
            }

            // InlineSqlScanner przyjmuje jeden string z separatorami.
            var rootsCombined = string.Join(";", rootsList);

            ConsoleLog.Info($"Inline-SQL (scanner): scanning roots: {rootsCombined}");

            var artifacts = InlineSqlScanner
                .ScanInlineSql(rootsCombined, extraHotMethods)
                .ToList();

            AppendInlineSqlEdgesAndNodes_FromArtifacts(
                artifacts: artifacts,
                sqlRoot: sqlRoot,
                nodes: nodes,
                edges: edges);

            AppendInlineSqlBodiesToJsonl(outputDir, artifacts);
        }

        /// <summary>
        /// Appends EF MIGRATION bodies (Up() methods) to sql_bodies.jsonl.
        /// Each entry carries both raw C# body text and a simple structured summary
        /// of affected tables/columns/foreign keys.
        /// </summary>
        private static void AppendMigrationBodiesToJsonl(
            string outputDir,
            IReadOnlyList<MigrationBodyArtifact> migrations)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
                return;

            if (migrations == null || migrations.Count == 0)
                return;

            var docsDir = Path.Combine(outputDir, "docs");
            Directory.CreateDirectory(docsDir);

            var bodiesPath = Path.Combine(docsDir, "sql_bodies.jsonl");

            try
            {
                using (var stream = new FileStream(bodiesPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    foreach (var m in migrations)
                    {
                        if (m == null)
                            continue;

                        var createsTables = new List<string>();
                        var dropsTables = new List<string>();
                        var addsColumns = new List<object>();
                        var dropsColumns = new List<object>();
                        var renamesColumns = new List<object>();
                        var addsForeignKeys = new List<object>();
                        var dropsForeignKeys = new List<object>();

                        foreach (var op in m.Operations ?? Array.Empty<EfMigrationOperation>())
                        {
                            if (op == null)
                                continue;

                            var opType = op.Operation ?? string.Empty;
                            var canonicalTable = CanonicalTableName(op.Schema, op.Table);

                            switch (opType)
                            {
                                case "CreateTable":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable))
                                        createsTables.Add(canonicalTable);
                                    break;

                                case "DropTable":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable))
                                        dropsTables.Add(canonicalTable);
                                    break;

                                case "AddColumn":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable) &&
                                        !string.IsNullOrWhiteSpace(op.Column))
                                    {
                                        addsColumns.Add(new
                                        {
                                            table = canonicalTable,
                                            column = op.Column
                                        });
                                    }
                                    break;

                                case "DropColumn":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable) &&
                                        !string.IsNullOrWhiteSpace(op.Column))
                                    {
                                        dropsColumns.Add(new
                                        {
                                            table = canonicalTable,
                                            column = op.Column
                                        });
                                    }
                                    break;

                                case "RenameColumn":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable) &&
                                        !string.IsNullOrWhiteSpace(op.Column) &&
                                        !string.IsNullOrWhiteSpace(op.NewColumn))
                                    {
                                        renamesColumns.Add(new
                                        {
                                            table = canonicalTable,
                                            from = op.Column,
                                            to = op.NewColumn
                                        });
                                    }
                                    break;

                                case "AddForeignKey":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable) &&
                                        !string.IsNullOrWhiteSpace(op.ForeignKey))
                                    {
                                        addsForeignKeys.Add(new
                                        {
                                            table = canonicalTable,
                                            foreignKey = op.ForeignKey
                                        });
                                    }
                                    break;

                                case "DropForeignKey":
                                    if (!string.IsNullOrWhiteSpace(canonicalTable) &&
                                        !string.IsNullOrWhiteSpace(op.ForeignKey))
                                    {
                                        dropsForeignKeys.Add(new
                                        {
                                            table = canonicalTable,
                                            foreignKey = op.ForeignKey
                                        });
                                    }
                                    break;

                                default:
                                    // Other operations (indexes, DataChange, RawSql, TouchTable-only)
                                    // are kept in Raw field only; we can extend summary later if needed.
                                    break;
                            }
                        }

                        var jsonObject = new
                        {
                            key = m.MigrationKey,          // e.g. "csharp:MyApp.Migrations.AddCustomer|MIGRATION"
                            kind = "MIGRATION",
                            name = m.MigrationName,        // class name or full name (see caller)
                            @namespace = m.Namespace,
                            typeFullName = m.TypeFullName,
                            file = m.SourcePath,           // full path to C# file
                            bodyPath = m.BodyRelPath,      // docs/bodies/Migration....
                            body = m.Body,                 // raw C# Up() method text

                            // Structured summary of schema changes in Up():
                            createsTables = createsTables,
                            dropsTables = dropsTables,
                            addsColumns = addsColumns,
                            dropsColumns = dropsColumns,
                            renamesColumns = renamesColumns,
                            addsForeignKeys = addsForeignKeys,
                            dropsForeignKeys = dropsForeignKeys
                        };

                        writer.WriteLine(JsonConvert.SerializeObject(jsonObject));
                    }
                }
            }
            catch (Exception ex)
            {
                // MIGRATION summaries are helpful but must not break the indexer.
                ConsoleLog.Warn("EF-Migrations bodies: failed to append migration bodies to sql_bodies.jsonl: " + ex.Message);
            }
        }

        private static List<string> AutoDiscoverCodeRoots(string sqlRoot)
        {
            var results = new List<string>();

            DirectoryInfo parent;
            try
            {
                parent = Directory.GetParent(sqlRoot);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
            {
                ConsoleLog.Warn("AutoDiscoverCodeRoots: invalid sqlRoot '" + sqlRoot + "': " + ex.Message);
                return results;
            }

            if (parent == null)
                return results;

            var p2 = parent.Parent ?? parent;
            var p3 = p2.Parent ?? p2;
            var src = p3.FullName;

            var preferred = Path.Combine(src, "Server", "DataAccess");
            var bases = Directory.Exists(preferred)
                ? new[] { preferred }
                : new[] { src };

            string[] excludes = { "test", "tests", "example", "examples", "sample", "samples", "dev", "client", "tools" };

            bool IsExcluded(string path) =>
                path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Any(seg => excludes.Any(x =>
                        string.Equals(seg, x, StringComparison.OrdinalIgnoreCase)));

            string[] csproj;
            try
            {
                csproj = bases
                    .Where(Directory.Exists)
                    .SelectMany(b => Directory.EnumerateFiles(b, "*.csproj", SearchOption.AllDirectories))
                    .Where(p => p.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                p.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) < 0 &&
                                p.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                p.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) < 0)
                    .Where(p => !IsExcluded(p))
                    .ToArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                ConsoleLog.Warn("AutoDiscoverCodeRoots: unauthorized while enumerating *.csproj under "
                    + string.Join(";", bases) + ": " + ex.Message);
                csproj = Array.Empty<string>();
            }
            catch (IOException ex)
            {
                ConsoleLog.Warn("AutoDiscoverCodeRoots: IO error while enumerating *.csproj under "
                    + string.Join(";", bases) + ": " + ex.Message);
                csproj = Array.Empty<string>();
            }

            bool LooksLikeEfDir(string dir)
            {
                string[] files;
                try
                {
                    files = Directory
                        .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                        .Take(300)
                        .ToArray();
                }
                catch (UnauthorizedAccessException ex)
                {
                    ConsoleLog.Warn("AutoDiscoverCodeRoots: unauthorized while enumerating *.cs under " + dir + ": " + ex.Message);
                    return false;
                }
                catch (IOException ex)
                {
                    ConsoleLog.Warn("AutoDiscoverCodeRoots: IO error while enumerating *.cs under " + dir + ": " + ex.Message);
                    return false;
                }

                foreach (var f in files)
                {
                    string txt;
                    try
                    {
                        txt = File.ReadAllText(f);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        ConsoleLog.Warn("AutoDiscoverCodeRoots: unauthorized while reading " + f + ": " + ex.Message);
                        continue;
                    }
                    catch (IOException ex)
                    {
                        ConsoleLog.Warn("AutoDiscoverCodeRoots: IO error while reading " + f + ": " + ex.Message);
                        continue;
                    }

                    if (Regex.IsMatch(txt, @"class\s+\w+\s*:\s*(\w+\.)*(Identity)?DbContext\b"))
                        return true;

                    if (txt.Contains(" DbSet<") || txt.Contains("\tDbSet<"))
                        return true;
                }

                return false;
            }

            foreach (var proj in csproj)
            {
                var dir = Path.GetDirectoryName(proj);
                if (string.IsNullOrEmpty(dir))
                    continue;

                try
                {
                    if (LooksLikeEfDir(dir))
                        results.Add(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    ConsoleLog.Warn("AutoDiscoverCodeRoots: error while scanning directory " + dir + ": " + ex.Message);
                }
            }

            return results;
        }

        private static (ConcurrentDictionary<string, NodeRow>, List<EdgeRow>, int) BuildSqlKnowledge(string sqlRoot, string outDir)
        {
            ConsoleLog.Info("SQL stage: scanning .sql files…");
            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Change Scripts","ChangeScripts","Initial Data","InitialData",
                "Snapshots","Tools",".git",".vs","bin","obj"
            };

            IEnumerable<string> EnumerateSql(string dir)
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (ignore.Contains(name)) continue;
                    foreach (var p in EnumerateSql(d))
                        yield return p;
                }

                foreach (var f in Directory.EnumerateFiles(dir, "*.sql"))
                    yield return f;
            }

            var files = EnumerateSql(sqlRoot).ToArray();
            ConsoleLog.Info("Found SQL files: " + files.Length);

            var nodes = new ConcurrentDictionary<string, NodeRow>(StringComparer.OrdinalIgnoreCase);
            var edges = new ConcurrentBag<EdgeRow>();
            var definedKinds = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var parser = new TSql150Parser(true);
            var bodiesDir = Path.Combine(outDir, "docs", "bodies");
            var bodiesJsonl = Path.Combine(outDir, "docs", "sql_bodies.jsonl");
            Directory.CreateDirectory(bodiesDir);
            int bodiesCount = 0;

            using (var bodiesWriter = new StreamWriter(bodiesJsonl, false, new UTF8Encoding(false)))
            {
                files
                    .AsParallel()
                    .WithDegreeOfParallelism(Environment.ProcessorCount)
                    .ForAll(path =>
                    {
                        try
                        {
                            var sql = ReadAndPreprocess(path);
                            IList<ParseError> errors;
                            using (var sr = new StringReader(sql))
                            {
                                var fragment = parser.Parse(sr, out errors);
                                if (errors != null && errors.Count > 0)
                                {
                                    var msg = string.Join("; ",
                                        errors.Take(2).Select(e => e.Message + " (L" + e.Line + ",C" + e.Column + ")"));
                                    ConsoleLog.Warn("[" + Path.GetFileName(path) + "] parser errors: " + msg);
                                }

                                var script = fragment as TSqlScript;
                                if (script == null) return;
                                var domain = DeriveDomain(sqlRoot, path);
                                var isPrePost = IsPreOrPostDeployment(path);

                                for (int bi = 0; bi < script.Batches.Count; bi++)
                                {
                                    var batch = script.Batches[bi];
                                    var collector = new RefCollector();
                                    batch.Accept(collector);

                                    List<Tuple<SchemaObjectName, string, TSqlFragment>> defines;
                                    if (collector.Defines.Count > 0)
                                    {
                                        defines = collector.Defines;
                                    }
                                    else
                                    {
                                        var pseudo = MakePseudoDefine(path, bi, isPrePost ? "DEPLOY" : "BATCH");
                                        defines = new List<Tuple<SchemaObjectName, string, TSqlFragment>> { pseudo };
                                    }

                                    foreach (var def in defines)
                                    {
                                        var key = Key(def.Item1, def.Item2);
                                        var schema = def.Item1.SchemaIdentifier != null
                                            ? def.Item1.SchemaIdentifier.Value
                                            : "dbo";
                                        var name = def.Item1.BaseIdentifier != null
                                            ? def.Item1.BaseIdentifier.Value
                                            : "(anon)";

                                        string bodyRelPath = null;
                                        if (IsBodyKind(def.Item2))
                                        {
                                            var fileName = schema + "." + name + "." + def.Item2 + ".sql";
                                            var bodyAbs = Path.Combine(bodiesDir, fileName);
                                            string scripted;
                                            lock (SqlGen)
                                            {
                                                SqlGen.GenerateScript(def.Item3, out scripted);
                                                File.WriteAllText(bodyAbs, scripted, new UTF8Encoding(false));
                                            }

                                            bodyRelPath = "docs/bodies/" + fileName;

                                            lock (bodiesWriter)
                                            {
                                                var obj = new
                                                {
                                                    key,
                                                    kind = def.Item2,
                                                    schema,
                                                    name,
                                                    domain,
                                                    file = path,
                                                    body = File.ReadAllText(bodyAbs, Encoding.UTF8)
                                                };
                                                bodiesWriter.WriteLine(JsonConvert.SerializeObject(obj));
                                                bodiesCount++;
                                            }
                                        }

                                        var node = new NodeRow(key, def.Item2, name, schema, path, bi, domain, bodyRelPath);
                                        nodes.TryAdd(key, node);
                                        definedKinds.TryAdd(NameKey(def.Item1), def.Item2);

                                        foreach (var r in collector.References)
                                        {
                                            var toKeyBase = NameKey(r.Item1);
                                            var toKind = r.Item2 ?? "UNKNOWN";
                                            edges.Add(new EdgeRow(
                                                from: key,
                                                to: toKeyBase + "|" + toKind,
                                                relation: r.Item3 ?? "Uses",
                                                toKind: toKind,
                                                file: path,
                                                batch: bi
                                            ));
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ConsoleLog.Error("[SQL:" + Path.GetFileName(path) + "] " + ex.Message);
                        }
                    });
            }

            // resolve kinds on edges + dedupe
            var fixedEdges = new List<EdgeRow>(edges.Count);
            foreach (var e in edges)
            {
                var split = SplitToNameAndKind(e.To);
                var baseName = split.Item1;
                var _kind = split.Item2;

                if (definedKinds.TryGetValue(baseName, out var realKind))
                {
                    fixedEdges.Add(new EdgeRow(
                        e.From,
                        baseName + "|" + realKind,
                        e.Relation,
                        realKind,
                        e.File,
                        e.Batch));
                }
                else
                {
                    fixedEdges.Add(e);
                }
            }

            fixedEdges = fixedEdges
                .GroupBy(x => x.From + "|" + x.To + "|" + x.Relation)
                .Select(g => g.First())
                .ToList();

            // ensure missing target nodes exist
            foreach (var e in fixedEdges)
            {
                if (!nodes.ContainsKey(e.To))
                {
                    var pair = SplitToNameAndKind(e.To);
                    var baseName = pair.Item1;
                    var kind = pair.Item2;
                    var sn = SplitSchemaAndName(baseName);
                    var schema = sn.Item1;
                    var name = sn.Item2;

                    nodes.TryAdd(
                        e.To,
                        new NodeRow(e.To, kind, name, schema, null, null, "(external)", null));
                }
            }

            ConsoleLog.Info("SQL stage finished: nodes=" + nodes.Count + ", edges=" + fixedEdges.Count + ", bodies=" + bodiesCount);
            return (nodes, fixedEdges, bodiesCount);
        }

        /// <summary>
        /// Produces a canonical "schema.table" representation.
        /// If table already contains a dot, it is returned unchanged.
        /// </summary>
        private static string CanonicalTableName(string schema, string table)
        {
            if (string.IsNullOrWhiteSpace(table))
                return null;

            if (table.Contains("."))
                return table;

            var s = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema;
            return s + "." + table;
        }

        private static string Csv(params object[] vals)
                    => string.Join(",", vals.Select(v =>
                        "\"" + ((v != null ? v.ToString() : string.Empty).Replace("\"", "\"\"")) + "\""));

        private static string DeriveDomain(string root, string file)
        {
            var rel = GetRelativePathSafe(root, file);
            var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : string.Empty;
        }

        private static Dictionary<string, Tuple<string, string>> ExtractEntityMappings(List<SyntaxTree> trees)
        {
            var map = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in trees)
            {
                var root = t.GetRoot();

                // [Table("Name", Schema="dbo")]
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var attr = cls.AttributeLists
                        .SelectMany(a => a.Attributes)
                        .FirstOrDefault(a =>
                            a.Name.ToString().EndsWith("Table") ||
                            a.Name.ToString().EndsWith("TableAttribute"));

                    if (attr != null)
                    {
                        var nameArgSyntax =
                            attr.ArgumentList != null && attr.ArgumentList.Arguments.Count > 0
                                ? attr.ArgumentList.Arguments[0]
                                : null;

                        var nameArg = nameArgSyntax != null
                            ? nameArgSyntax.ToString().Trim('"', '\'')
                            : null;

                        var schema = "dbo";
                        var schemaArg = attr.ArgumentList != null
                            ? attr.ArgumentList.Arguments.FirstOrDefault(a =>
                                a.ToString().StartsWith("Schema", StringComparison.OrdinalIgnoreCase))
                            : null;

                        if (schemaArg != null)
                        {
                            var s = schemaArg.ToString();
                            var idx = s.IndexOf('=');
                            if (idx > 0)
                                schema = s.Substring(idx + 1).Trim().Trim('"', '\'');
                        }

                        if (!string.IsNullOrEmpty(nameArg))
                            map[FullName(cls)] = Tuple.Create(schema, nameArg);
                    }
                }

                // Fluent API: modelBuilder.Entity<T>().ToTable("Name","Schema")
                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var ma = inv.Expression as MemberAccessExpressionSyntax;
                    if (ma != null &&
                        ma.Name.Identifier.Text == "ToTable" &&
                        inv.ArgumentList != null &&
                        inv.ArgumentList.Arguments.Count >= 1)
                    {
                        var args = inv.ArgumentList.Arguments;
                        var name = args[0].ToString().Trim('"', '\'');
                        var schema = args.Count >= 2
                            ? args[1].ToString().Trim('"', '\'')
                            : "dbo";

                        var ent = ma.Expression as InvocationExpressionSyntax;
                        var ma2 = ent != null ? ent.Expression as MemberAccessExpressionSyntax : null;
                        if (ma2 != null &&
                            ma2.Name.Identifier.Text == "Entity" &&
                            ent.ArgumentList != null &&
                            ent.ArgumentList.Arguments.Count == 1)
                        {
                            var arg = ent.ArgumentList.Arguments[0].ToString();
                            string type;
                            if (arg.Contains("typeof"))
                            {
                                var open = arg.IndexOf('(');
                                var close = arg.IndexOf(')');
                                if (open >= 0 && close > open)
                                    type = arg.Substring(open + 1, close - open - 1);
                                else
                                    type = arg;
                            }
                            else
                            {
                                type = arg.Trim('<', '>');
                            }

                            map[type] = Tuple.Create(schema, name);
                        }
                    }
                }
            }

            return map;
        }

        private static string FullName(ClassDeclarationSyntax cls)
        {
            var nsDecl = cls.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            var ns = nsDecl != null ? nsDecl.Name.ToString() : null;
            return string.IsNullOrEmpty(ns) ? cls.Identifier.Text : ns + "." + cls.Identifier.Text;
        }

        private static string? GetContainingTypeName(SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (current is TypeDeclarationSyntax t)
                    return t.Identifier.ValueText;

                current = current.Parent;
            }

            return null;
        }

        private static string? GetNamespace(SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (current is NamespaceDeclarationSyntax ns)
                    return ns.Name.ToString();

                if (current is FileScopedNamespaceDeclarationSyntax fns)
                    return fns.Name.ToString();

                current = current.Parent;
            }

            return null;
        }

        // ===== Utilities =====
        private static string GetRelativePathSafe(string baseDir, string fullPath)
        {
            try
            {
                var baseUri = new Uri(AppendDirSep(baseDir));
                var pathUri = new Uri(fullPath);
                var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString());
                return rel.Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return fullPath;
            }
        }

        private static bool IsBodyKind(string kind)
                    => kind == "PROC" || kind == "FUNC" || kind == "VIEW" ||
                       kind == "TRIGGER" || kind == "TABLE" || kind == "SEQUENCE" || kind == "TYPE";

        private static bool IsDirectoryEmpty(string path)
        {
            try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
            catch { return true; }
        }

        private static bool IsPreOrPostDeployment(string p)
        {
            var name = Path.GetFileName(p);
            return name.Equals("PreDeployment.sql", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("PostDeployment.sql", StringComparison.OrdinalIgnoreCase);
        }

        private static string Key(SchemaObjectName n, string kind) => NameKey(n) + "|" + kind;

       

        private static Tuple<SchemaObjectName, string, TSqlFragment> MakePseudoDefine(string file, int bi, string kind)
        {
            var n = new SchemaObjectName();
            n.Identifiers.Add(new Identifier { Value = "dbo" });
            n.Identifiers.Add(new Identifier
            {
                Value = "__" + kind.ToLowerInvariant() + "__:" + Path.GetFileName(file) + ":" + bi
            });
            return Tuple.Create(n, kind, (TSqlFragment)new TSqlScript());
        }

        private static string NameKey(SchemaObjectName n)
        {
            var db = n.DatabaseIdentifier != null ? n.DatabaseIdentifier.Value : null;
            var schema = n.SchemaIdentifier != null ? n.SchemaIdentifier.Value : "dbo";
            var name = n.BaseIdentifier != null ? n.BaseIdentifier.Value : "(anon)";
            return (db != null ? db + "." : "") + schema + "." + name;
        }

        private static string NormalizeDir(string p)
        {
            var full = Path.GetFullPath(p);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ReadAndPreprocess(string path)
        {
            var text = File.ReadAllText(path);
            var sb = new StringBuilder();

            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (Regex.IsMatch(raw, @"^\s*:(r|setvar|connect|on\s+error\s+exit)\b",
                        RegexOptions.IgnoreCase))
                    continue;

                sb.AppendLine(raw);
            }

            var cleaned = sb.ToString();
            cleaned = Regex.Replace(cleaned, @"\$\([^)]+\)", "0");
            return cleaned;
        }

        // ====== Build orchestration (legacy parity) ======
        private static int RunBuild(string outputDir, string sqlProjectRoot, string efRoot = "")
        {
            var outGraph = Path.Combine(outputDir, "graph");
            var outDocs = Path.Combine(outputDir, "docs");
            Directory.CreateDirectory(outGraph);
            Directory.CreateDirectory(outDocs);
            Directory.CreateDirectory(Path.Combine(outDocs, "bodies"));

            var sqlRoot = NormalizeDir(sqlProjectRoot);
            if (!Directory.Exists(sqlRoot))
            {
                ConsoleLog.Error("Provided sqlProjectRoot does not exist: " + sqlRoot);
                return 1;
            }

            ConsoleLog.Info("SQL root: " + sqlRoot);
            ConsoleLog.Info("Out dir : " + outputDir);

            // 1) SQL indexing
            var (nodes, edges, bodiesJsonlCount) = BuildSqlKnowledge(sqlRoot, outputDir);

            // 2) EF autodiscovery / explicit root
            List<string> codeRoots;
            if (!string.IsNullOrWhiteSpace(efRoot))
            {
                if (File.Exists(efRoot) &&
                    Path.GetExtension(efRoot).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    efRoot = Path.GetDirectoryName(efRoot);
                }

                var efDir = NormalizeDir(efRoot);

                if (!Directory.Exists(efDir))
                {
                    ConsoleLog.Warn("Provided efRoot does not exist: " + efDir + " — falling back to autodiscovery.");
                    codeRoots = AutoDiscoverCodeRoots(sqlRoot);
                }
                else if (IsDirectoryEmpty(efDir))
                {
                    ConsoleLog.Warn("Provided efRoot directory is empty: " + efDir + " — falling back to autodiscovery.");
                    codeRoots = AutoDiscoverCodeRoots(sqlRoot);
                }
                else
                {
                    codeRoots = new List<string> { efDir };
                }
            }
            else
            {
                codeRoots = AutoDiscoverCodeRoots(sqlRoot);
            }

            if (codeRoots.Count > 0)
            {
                ConsoleLog.Info("EF code roots:");
                foreach (var cr in codeRoots)
                    ConsoleLog.Info("  " + cr);

                AppendEfEdgesAndNodes(codeRoots, outputDir, nodes, edges);
                AppendEfMigrationEdgesAndNodes(codeRoots, outputDir, nodes, edges);
            }
            else
            {
                ConsoleLog.Info("No EF code roots detected – EF stage skipped.");
            }

            // 3) Inline-SQL (C# methods with raw SQL literals)
            if (GlobalInlineSqlRoots != null && GlobalInlineSqlRoots.Length > 0)
            {
                // Docelowo zawsze używamy InlineSqlScanner + adaptera.
                AppendInlineSqlEdgesAndNodes_UsingScanner(
                 inlineSqlRoots: GlobalInlineSqlRoots,
                 sqlRoot: sqlRoot,
                 extraHotMethods: GlobalInlineSqlHotMethods,
                 outputDir: outputDir,
                 nodes: nodes,
                 edges: edges);
            }
            else
            {
                ConsoleLog.Info("Inline-SQL: no inlineSql roots configured – skipped.");
            }

            WriteGraph(outputDir, nodes.Values, edges);
            WriteManifest(
                outDir: outputDir,
                sqlRoot: sqlRoot,
                codeRoots: codeRoots,
                nodeCount: nodes.Count,
                edgeCount: edges.Count,
                bodiesCount: bodiesJsonlCount);

            ConsoleLog.Info("LegacySqlIndexer finished.");
            return 0;
        }

        private static Tuple<string, string> SplitSchemaAndName(string baseName)
        {
            var parts = baseName.Split('.');
            if (parts.Length == 3) return Tuple.Create(parts[1], parts[2]);
            if (parts.Length == 2) return Tuple.Create(parts[0], parts[1]);
            return Tuple.Create("dbo", parts[parts.Length - 1]);
        }

        private static Tuple<string, string> SplitToNameAndKind(string key)
        {
            var i = key.LastIndexOf('|');
            if (i >= 0)
                return Tuple.Create(key.Substring(0, i), key.Substring(i + 1));

            return Tuple.Create(key, "UNKNOWN");
        }

        private static string ToRel(string p) => p == null ? null : p.Replace('\\', '/');

        /// <summary>
        /// Parses InlineSQL identifier created by InlineSqlScanner.AnalyzeSqlSnippet, e.g.:
        ///   "dbo.Customer|TABLE_OR_VIEW|inline@Some/File.cs:L42"
        /// Returns schema, name and kind suitable for graph keys.
        /// </summary>
        private static bool TryParseInlineSqlIdentifier(
            string identifier,
            out string schema,
            out string name,
            out string kind)
        {
            schema = "dbo";
            name = string.Empty;
            kind = "TABLE_OR_VIEW";

            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            // Expected format:
            //   {schema}.{name}|{kind}|inline@{relPath}:L{line}
            var parts = identifier.Split('|');
            if (parts.Length < 3)
                return false;

            var objectId = parts[0];   // e.g. "dbo.Customer"
            var kindPart = parts[1];   // e.g. "TABLE", "TABLE_OR_VIEW", "PROC"

            if (!string.IsNullOrWhiteSpace(kindPart))
                kind = kindPart;

            var dotIndex = objectId.IndexOf('.');
            if (dotIndex <= 0 || dotIndex == objectId.Length - 1)
                return false;

            schema = objectId.Substring(0, dotIndex);
            name = objectId.Substring(dotIndex + 1);

            return !string.IsNullOrWhiteSpace(name);
        }

        /// <summary>
        /// Best-effort recovery of C# method context for an InlineSQL artifact
        /// based on SourcePath + LineNumber. Najpierw próbujemy znaleźć metodę,
        /// która zawiera podaną linię; jeżeli się nie uda, bierzemy metodę,
        /// której zakres jest "najbliżej" tej linii.
        /// </summary>
        private static void TryPopulateMethodContextFromFile(SqlArtifact artifact)
        {
            if (artifact == null)
                return;

            // Jeżeli ktoś już wcześniej wypełnił MethodFullName – nie ruszamy.
            if (!string.IsNullOrWhiteSpace(artifact.MethodFullName))
                return;

            if (string.IsNullOrWhiteSpace(artifact.SourcePath) ||
                !File.Exists(artifact.SourcePath))
                return;

            try
            {
                var text = File.ReadAllText(artifact.SourcePath);
                var tree = CSharpSyntaxTree.ParseText(text, path: artifact.SourcePath);
                var root = tree.GetRoot();

                var methods = root
                    .DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .ToList();

                if (methods.Count == 0)
                    return;

                MethodDeclarationSyntax? method = null;
                var line = artifact.LineNumber;

                // 1) Najpierw klasyczne "czy linia wpada w zakres metody".
                if (line.HasValue && line.Value > 0)
                {
                    var target = line.Value;

                    method = methods.FirstOrDefault(m =>
                    {
                        var span = m.GetLocation().GetLineSpan();
                        var start = span.StartLinePosition.Line + 1;
                        var end = span.EndLinePosition.Line + 1;
                        return target >= start && target <= end;
                    });

                    // 2) Jeżeli exact-hit się nie udał (np. lekko przesunięte linie),
                    // wybieramy metodę, której zakres jest "najbliżej" danej linii.
                    if (method == null)
                    {
                        method = methods
                            .OrderBy(m =>
                            {
                                var span = m.GetLocation().GetLineSpan();
                                var start = span.StartLinePosition.Line + 1;
                                var end = span.EndLinePosition.Line + 1;

                                if (target < start) return start - target;
                                if (target > end) return target - end;
                                return 0;
                            })
                            .FirstOrDefault();
                    }
                }

                // 3) Brak linii w artefakcie – bierzemy po prostu pierwszą metodę
                // z pliku (bez kombinacji). To i tak lepsze niż brak kontekstu.
                if (method == null)
                    method = methods.FirstOrDefault();

                if (method == null)
                    return;

                var ns = GetNamespace(method);
                var typeName = GetContainingTypeName(method);

                string? typeFullName;
                if (string.IsNullOrEmpty(typeName))
                {
                    typeFullName = string.IsNullOrEmpty(ns) ? null : ns;
                }
                else
                {
                    typeFullName = string.IsNullOrEmpty(ns)
                        ? typeName
                        : ns + "." + typeName;
                }

                var methodName = method.Identifier.ValueText;
                string? methodFullName;
                if (string.IsNullOrEmpty(methodName))
                {
                    methodFullName = typeFullName;
                }
                else
                {
                    methodFullName = string.IsNullOrEmpty(typeFullName)
                        ? methodName
                        : typeFullName + "." + methodName;
                }

                if (!string.IsNullOrEmpty(ns) &&
                    string.IsNullOrEmpty(artifact.Namespace))
                {
                    artifact.Namespace = ns;
                }

                if (!string.IsNullOrEmpty(typeFullName) &&
                    string.IsNullOrEmpty(artifact.TypeFullName))
                {
                    artifact.TypeFullName = typeFullName;
                }

                if (!string.IsNullOrEmpty(methodFullName))
                {
                    artifact.MethodFullName = methodFullName;
                }
            }
            catch
            {
                // Fallback only – nie zabijamy indeksowania, najwyżej artefakt
                // zostanie bez kontekstu metody.
            }
        }

        // ========================================================
        //  Persist
        // ========================================================
        private static void WriteGraph(string outDir, IEnumerable<NodeRow> nodes, IEnumerable<EdgeRow> edges)
        {
            var graphDir = Path.Combine(outDir, "graph");
            Directory.CreateDirectory(graphDir);

            var nodesCsv = Path.Combine(graphDir, "nodes.csv");
            var edgesCsv = Path.Combine(graphDir, "edges.csv");
            var graphJson = Path.Combine(graphDir, "graph.json");

            File.WriteAllLines(
                nodesCsv,
                (new[] { "key,kind,name,schema,file,batch,domain,body_path" })
                    .Concat(nodes.OrderBy(n => n.Key)
                        .Select(n => Csv(n.Key, n.Kind, n.Name, n.Schema, ToRel(n.File), n.Batch, n.Domain, n.BodyPath))));

            File.WriteAllLines(
                edgesCsv,
                (new[] { "from,to,relation,to_kind,file,batch" })
                    .Concat(edges.OrderBy(e => e.From).ThenBy(e => e.To)
                        .Select(e => Csv(e.From, e.To, e.Relation, e.ToKind, ToRel(e.File), e.Batch))));

            var graph = new
            {
                nodes = nodes.OrderBy(n => n.Key).ToArray(),
                edges = edges.OrderBy(e => e.From).ThenBy(e => e.To).ToArray()
            };

            File.WriteAllText(graphJson, JsonConvert.SerializeObject(graph, Formatting.Indented));
            ConsoleLog.Info("Written graph artifacts: " + nodesCsv + " | " + edgesCsv + " | " + graphJson);
        }

        private static void WriteManifest(
                    string outDir,
                    string sqlRoot,
                    List<string> codeRoots,
                    int nodeCount,
                    int edgeCount,
                    int bodiesCount)
        {
            var manifestObj = new
            {
                schema = 1,
                builtAt = DateTimeOffset.Now.ToString("o"),
                sqlRoot,
                codeRoots,
                counts = new { nodes = nodeCount, edges = edgeCount, docs = bodiesCount }
            };

            var manifestPath = Path.Combine(outDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifestObj, Formatting.Indented));
            ConsoleLog.Info("Manifest written: " + manifestPath);
        }

        private sealed class EdgeRow
        {
            public int? Batch;
            public string File;
            public string From;
            public string Relation;
            public string To;
            public string ToKind;
            public EdgeRow(string from, string to, string relation, string toKind, string file, int? batch)
            {
                From = from;
                To = to;
                Relation = relation;
                ToKind = toKind;
                File = file;
                Batch = batch;
            }
        }

        private sealed class EntityBodyArtifact
        {
            public string Body;
            public string BodyRelPath;
            public string EntityKey;
            public string EntityName;
            public string? Namespace;
            public string SourcePath;
            public string? TypeFullName;
            public EntityBodyArtifact(
                string entityKey,
                string entityName,
                string? @namespace,
                string? typeFullName,
                string sourcePath,
                string bodyRelPath,
                string body)
            {
                EntityKey = entityKey;
                EntityName = entityName;
                Namespace = @namespace;
                TypeFullName = typeFullName;
                SourcePath = sourcePath;
                BodyRelPath = bodyRelPath;
                Body = body;
            }
        }

        private sealed class MigrationBodyArtifact
        {
            public string Body;
            public string BodyRelPath;
            public string MigrationKey;
            public string MigrationName;
            public string Namespace;
            public IList<EfMigrationOperation> Operations;
            public string SourcePath;
            public string TypeFullName;
            public MigrationBodyArtifact(
                string migrationKey,
                string migrationName,
                string @namespace,
                string typeFullName,
                string sourcePath,
                string bodyRelPath,
                string body,
                IList<EfMigrationOperation> operations)
            {
                MigrationKey = migrationKey;
                MigrationName = migrationName;
                Namespace = @namespace;
                TypeFullName = typeFullName;
                SourcePath = sourcePath;
                BodyRelPath = bodyRelPath;
                Body = body;
                Operations = operations ?? Array.Empty<EfMigrationOperation>();
            }
        }

        // ====== EF raw-SQL mini collector ======
        private sealed class MiniSqlRefCollector : TSqlFragmentVisitor
        {
            public readonly List<Tuple<string, string, string, string>> Refs =
                new List<Tuple<string, string, string, string>>();

            public override void Visit(NamedTableReference node)
            {
                var s = node.SchemaObject != null && node.SchemaObject.SchemaIdentifier != null
                    ? node.SchemaObject.SchemaIdentifier.Value
                    : "dbo";

                var n = node.SchemaObject != null && node.SchemaObject.BaseIdentifier != null
                    ? node.SchemaObject.BaseIdentifier.Value
                    : null;

                if (n != null)
                    Refs.Add(Tuple.Create(s, n, "TABLE_OR_VIEW", "ReadsFrom"));
            }

            public override void Visit(InsertStatement node)
            {
                var t = node.InsertSpecification != null
                    ? node.InsertSpecification.Target as NamedTableReference
                    : null;

                if (t != null && t.SchemaObject != null && t.SchemaObject.BaseIdentifier != null)
                {
                    var schema = t.SchemaObject.SchemaIdentifier != null
                        ? t.SchemaObject.SchemaIdentifier.Value
                        : "dbo";

                    Refs.Add(Tuple.Create(schema, t.SchemaObject.BaseIdentifier.Value, "TABLE", "WritesTo"));
                }
            }

            public override void Visit(UpdateStatement node)
            {
                var t = node.UpdateSpecification != null
                    ? node.UpdateSpecification.Target as NamedTableReference
                    : null;

                if (t != null && t.SchemaObject != null && t.SchemaObject.BaseIdentifier != null)
                {
                    var schema = t.SchemaObject.SchemaIdentifier != null
                        ? t.SchemaObject.SchemaIdentifier.Value
                        : "dbo";

                    Refs.Add(Tuple.Create(schema, t.SchemaObject.BaseIdentifier.Value, "TABLE", "WritesTo"));
                }
            }

            public override void Visit(DeleteStatement node)
            {
                var t = node.DeleteSpecification != null
                    ? node.DeleteSpecification.Target as NamedTableReference
                    : null;

                if (t != null && t.SchemaObject != null && t.SchemaObject.BaseIdentifier != null)
                {
                    var schema = t.SchemaObject.SchemaIdentifier != null
                        ? t.SchemaObject.SchemaIdentifier.Value
                        : "dbo";

                    Refs.Add(Tuple.Create(schema, t.SchemaObject.BaseIdentifier.Value, "TABLE", "WritesTo"));
                }
            }

            public override void Visit(ExecuteSpecification node)
            {
                var pr = node.ExecutableEntity as ExecutableProcedureReference;
                if (pr != null &&
                    pr.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference.Name != null)
                {
                    var nm = pr.ProcedureReference.ProcedureReference.Name;
                    var schema = nm.SchemaIdentifier != null ? nm.SchemaIdentifier.Value : "dbo";
                    Refs.Add(Tuple.Create(schema, nm.BaseIdentifier.Value, "PROC", "Executes"));
                }
            }
        }

        // ====== data rows (classes instead of records) ======
        private sealed class NodeRow
        {
            public int? Batch;
            public string BodyPath;
            public string Domain;
            public string File;
            public string Key;
            public string Kind;
            public string Name;
            public string Schema;
            public NodeRow(string key, string kind, string name, string schema, string file, int? batch, string domain, string bodyPath)
            {
                Key = key;
                Kind = kind;
                Name = name;
                Schema = schema;
                File = file;
                Batch = batch;
                Domain = domain;
                BodyPath = bodyPath;
            }
        }
        // ====== Visitor (legacy) ======

        // ====== Visitor (legacy) ======
        private sealed class RefCollector : TSqlFragmentVisitor
        {
            public readonly List<Tuple<SchemaObjectName, string, TSqlFragment>> Defines =
                new List<Tuple<SchemaObjectName, string, TSqlFragment>>();

            public readonly List<Tuple<SchemaObjectName, string, string>> References =
                new List<Tuple<SchemaObjectName, string, string>>();

            public override void ExplicitVisit(CreateSynonymStatement node)
            {
                AddDefine(node.Name, "SYNONYM", node);
                if (node.ForName != null)
                    AddRef(node.ForName, null, "SynonymFor");
            }

            // Definitions
            public override void Visit(CreateTableStatement node)
            {
                // Definition of TABLE itself
                AddDefine(node.SchemaObjectName, "TABLE", node);

                // Foreign key constraints declared inside CREATE TABLE
                // will be translated into edges: ChildTable --(ForeignKey)--> ParentTable.
                if (node.Definition != null)
                {
                    CollectForeignKeyReferences(node.Definition);
                }
            }

            public override void Visit(CreateViewStatement node) =>
                AddDefine(node.SchemaObjectName, "VIEW", node);

            public override void Visit(CreateProcedureStatement node)
            {
                if (node.ProcedureReference != null)
                    AddDefine(node.ProcedureReference.Name, "PROC", node);
            }

            public override void Visit(CreateFunctionStatement node) =>
                AddDefine(node.Name, "FUNC", node);

            public override void Visit(CreateTypeTableStatement node) =>
                AddDefine(node.Name, "TYPE", node);

            public override void Visit(CreateTypeUddtStatement node) =>
                AddDefine(node.Name, "TYPE", node);

            public override void Visit(CreateSequenceStatement node) =>
                AddDefine(node.Name, "SEQUENCE", node);

            public override void Visit(CreateTriggerStatement node)
            {
                AddDefine(node.Name, "TRIGGER", node);
                var trgObj = node.TriggerObject;
                if (trgObj != null && trgObj.Name != null)
                    AddRef(trgObj.Name, null, "On");
            }

            // ALTER statements
            public override void Visit(AlterTableAddTableElementStatement node)
            {
                // ALTER TABLE always "touches" a TABLE definition
                AddDefine(node.SchemaObjectName, "TABLE", node);

                // If this ALTER adds foreign key constraints, capture them
                // as ChildTable --(ForeignKey)--> ParentTable relations.
                if (node.Definition != null)
                {
                    CollectForeignKeyReferences(node.Definition);
                }
            }

            // References
            public override void Visit(NamedTableReference node)
            {
                if (node.SchemaObject != null)
                    AddRef(node.SchemaObject, "TABLE_OR_VIEW", "ReadsFrom");
            }

            public override void Visit(InsertStatement node)
            {
                if (node.InsertSpecification != null &&
                    node.InsertSpecification.Target is NamedTableReference t)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");
            }

            public override void Visit(UpdateStatement node)
            {
                if (node.UpdateSpecification != null &&
                    node.UpdateSpecification.Target is NamedTableReference t)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");
            }

            public override void Visit(DeleteStatement node)
            {
                if (node.DeleteSpecification != null &&
                    node.DeleteSpecification.Target is NamedTableReference t)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");
            }

            public override void Visit(MergeStatement node)
            {
                var ms = node.MergeSpecification;
                if (ms == null) return;

                var t = ms.Target as NamedTableReference;
                if (t != null)
                    AddRef(t.SchemaObject, "TABLE", "WritesTo");

                var s = ms.TableReference as NamedTableReference;
                if (s != null)
                    AddRef(s.SchemaObject, "TABLE_OR_VIEW", "ReadsFrom");
            }

            public override void Visit(ExecuteSpecification node)
            {
                var pr = node.ExecutableEntity as ExecutableProcedureReference;
                if (pr != null &&
                    pr.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference != null &&
                    pr.ProcedureReference.ProcedureReference.Name != null)
                {
                    var nm = pr.ProcedureReference.ProcedureReference.Name;
                    AddRef(nm, "PROC", "Executes");
                }
            }

            /// <summary>
            /// Collects foreign key references from table-level constraints and
            /// emits them as TABLE --(ForeignKey)--> TABLE references.
            /// Only table-level FK constraints are handled here; column-level FK
            /// syntax may be added later if needed.
            /// </summary>
            private void CollectForeignKeyReferences(TableDefinition definition)
            {
                if (definition == null || definition.TableConstraints == null || definition.TableConstraints.Count == 0)
                    return;

                foreach (var constraint in definition.TableConstraints.OfType<ForeignKeyConstraintDefinition>())
                {
                    var refTable = constraint.ReferenceTableName;
                    if (refTable != null)
                    {
                        // 'refTable' is the parent (referenced) TABLE.
                        // The current TABLE (child) is the batch's defining object;
                        // BuildSqlKnowledge will connect: childKey -> parentKey with relation "ForeignKey".
                        AddRef(refTable, "TABLE", "ForeignKey");
                    }
                }
            }

            private void AddDefine(SchemaObjectName name, string kind, TSqlFragment frag)
            {
                if (name == null || name.BaseIdentifier == null) return;
                Defines.Add(Tuple.Create(name, kind, frag));
            }

            private void AddRef(SchemaObjectName name, string kindHint, string relation)
            {
                if (name == null || name.BaseIdentifier == null) return;
                if (IsTempOrVar(name)) return;
                References.Add(Tuple.Create(name, kindHint, relation));
            }

            private bool IsTempOrVar(SchemaObjectName n)
            {
                var bi = n.BaseIdentifier != null ? n.BaseIdentifier.Value : null;
                if (bi == null) return false;
                return bi.StartsWith("#") || bi.StartsWith("@");
            }
        }


    }
}