// RoslynIndexer.Core/Sql/EfMigrations/EfMigrationAnalyzer.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynIndexer.Core.Internals; // PathEx

namespace RoslynIndexer.Core.Sql.EfMigrations
{
    /// <summary>
    /// Lightweight FluentMigrator / EF-style migrations analyzer.
    /// Parses migration classes and extracts table/index/data operations into a neutral model.
    /// No coupling to runners or graph format – intended to be called from LegacySqlIndexer
    /// and future graph builders.
    /// </summary>
    public sealed class EfMigrationAnalyzer
    {
        /// <summary>
        /// Convenience overload without cancellation token.
        /// </summary>
        public IList<EfMigrationInfo> Analyze(
            string migrationsRoot,
            string repoRoot)
        {
            return Analyze(migrationsRoot, repoRoot, CancellationToken.None);
        }

        /// <summary>
        /// Scan the given folder for C# migration classes and extract operations.
        /// </summary>
        public IList<EfMigrationInfo> Analyze(
            string migrationsRoot,
            string repoRoot,
            CancellationToken cancellationToken)
        {
            var result = new List<EfMigrationInfo>();

            if (string.IsNullOrWhiteSpace(migrationsRoot))
                return result;

            if (!Directory.Exists(migrationsRoot))
                return result;
           
            var files = Directory.EnumerateFiles(migrationsRoot, "*.cs", SearchOption.AllDirectories).ToList();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken);
                var root = tree.GetRoot(cancellationToken);

                var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                foreach (var cls in classes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!IsMigrationClass(cls))
                        continue;

                    var info = AnalyzeMigrationClass(cls, file, repoRoot, tree, cancellationToken);
                    if (info != null)
                        result.Add(info);
                }
            }


            return result;
        }

        /// <summary>
        /// Heuristic: class is a migration if base type ends with "Migration"/"ForwardOnlyMigration"
        /// or has any attribute whose name contains "Migration" / "UpdateMigration".
        /// Covers FluentMigrator and Nop-specific attributes like [NopUpdateMigration(...)]. 
        /// </summary>
        private static bool IsMigrationClass(ClassDeclarationSyntax cls)
        {
            // Base type check
            if (cls.BaseList != null)
            {
                foreach (var bt in cls.BaseList.Types)
                {
                    var name = bt.Type.ToString();
                    if (name.EndsWith("Migration", StringComparison.Ordinal) ||
                        name.EndsWith("ForwardOnlyMigration", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            // Attribute check
            foreach (var attrList in cls.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrName = attr.Name.ToString();
                    if (attrName.IndexOf("Migration", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        attrName.IndexOf("UpdateMigration", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static EfMigrationInfo AnalyzeMigrationClass(
    ClassDeclarationSyntax cls,
    string filePath,
    string repoRoot,
    SyntaxTree tree,
    CancellationToken cancellationToken)
        {
            var className = cls.Identifier.Text;

            var relativeFile = !string.IsNullOrEmpty(repoRoot)
                ? PathEx.GetRelativePath(repoRoot, filePath)
                : filePath;

            var ns = GetNamespace(cls);
            var typeFullName = string.IsNullOrEmpty(ns)
                ? className
                : ns + "." + className;

            var attributes = ExtractMigrationAttributes(cls);
            var upOps = new List<EfMigrationOperation>();
            var downOps = new List<EfMigrationOperation>();

            MethodDeclarationSyntax upMethod = null;

            var methods = cls.Members.OfType<MethodDeclarationSyntax>().ToList();
            foreach (var method in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(method.Identifier.Text, "Up", StringComparison.Ordinal))
                {
                    ExtractOperationsFromBody(method, upOps);

                    // Keep the first Up() method for body extraction.
                    if (upMethod == null)
                        upMethod = method;
                }
                else if (string.Equals(method.Identifier.Text, "Down", StringComparison.Ordinal))
                {
                    ExtractOperationsFromBody(method, downOps);
                }
            }

            string upBody = null;
            if (upMethod != null)
            {
                try
                {
                    var sourceText = tree.GetText(cancellationToken);
                    upBody = sourceText.ToString(upMethod.Span);
                }
                catch
                {
                    // UpBody is optional – failure here must not break migration discovery.
                    upBody = null;
                }
            }

            // Even if no operations were detected, we still return the migration info.
            return new EfMigrationInfo
            {
                ClassName = className,
                Namespace = ns,
                TypeFullName = typeFullName,
                SourcePath = filePath,
                FileRelativePath = relativeFile,
                Attributes = attributes,
                UpOperations = upOps,
                DownOperations = downOps,
                UpBody = upBody
            };
        }


        private static IList<EfMigrationAttribute> ExtractMigrationAttributes(ClassDeclarationSyntax cls)
        {
            var list = new List<EfMigrationAttribute>();

            foreach (var attrList in cls.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = attr.Name.ToString();
                    if (name.IndexOf("Migration", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("UpdateMigration", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var values = new List<string>();
                    if (attr.ArgumentList != null)
                    {
                        foreach (var arg in attr.ArgumentList.Arguments)
                        {
                            var literal = TryExtractLiteralValue(arg.Expression);
                            var raw = arg.Expression.ToString();
                            values.Add(literal ?? raw);
                        }
                    }

                    list.Add(new EfMigrationAttribute
                    {
                        Name = name,
                        Values = values
                    });
                }
            }

            return list;
        }

        private static void ExtractOperationsFromBody(
            MethodDeclarationSyntax method,
            IList<EfMigrationOperation> sink)
        {
            // Block-bodied Up/Down
            if (method.Body != null)
            {
                foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var op = TryParseOperation(invocation);
                    if (op != null)
                        sink.Add(op);
                }
            }

            // Expression-bodied Up/Down (rare, but cheap to support)
            if (method.ExpressionBody != null)
            {
                foreach (var invocation in method.ExpressionBody.Expression
                             .DescendantNodesAndSelf()
                             .OfType<InvocationExpressionSyntax>())
                {
                    var op = TryParseOperation(invocation);
                    if (op != null)
                        sink.Add(op);
                }
            }
        }

        /// <summary>
        /// Attempt to interpret a single invocation as a migration operation.
        /// Supports patterns commonly used in Nop/FluentMigrator:
        /// - Create.Table(...)
        /// - Delete.Table(...)
        /// - Create.Index(...)
        /// - Delete.Index(...)
        /// - Schema.Table(...)
        /// - Data.InsertEntity(...) / UpdateEntity(...) / DeleteEntity(...)
        /// - Sql("...")
        /// </summary>
        private static EfMigrationOperation TryParseOperation(InvocationExpressionSyntax invocation)
        {
            var parts = FlattenMemberAccess(invocation.Expression);
            if (parts.Count == 0)
                return null;

            var last = parts[parts.Count - 1];

            // === FluentMigrator-style: Create.Table(...), Create.Index(...) ===
            if (string.Equals(parts[0], "Create", StringComparison.Ordinal))
            {
                if (parts.Count > 1 && string.Equals(parts[1], "Table", StringComparison.Ordinal))
                {
                    var nameArg = GetFirstArgument(invocation);
                    var tableName = nameArg != null ? TryExtractName(nameArg.Expression) : null;

                    return new EfMigrationOperation
                    {
                        Kind = EfMigrationOperationKind.CreateTable,
                        Operation = "CreateTable",
                        Table = tableName,
                        Raw = invocation.ToString()
                    };
                }

                if (parts.Count > 1 && string.Equals(parts[1], "Index", StringComparison.Ordinal))
                {
                    var nameArg = GetFirstArgument(invocation);
                    var indexName = nameArg != null ? TryExtractName(nameArg.Expression) : null;

                    return new EfMigrationOperation
                    {
                        Kind = EfMigrationOperationKind.CreateIndex,
                        Operation = "CreateIndex",
                        Index = indexName,
                        Raw = invocation.ToString()
                    };
                }
            }

            // === FluentMigrator-style: Delete.Table(...), Delete.Index(...) ===
            if (string.Equals(parts[0], "Delete", StringComparison.Ordinal))
            {
                if (parts.Count > 1 && string.Equals(parts[1], "Table", StringComparison.Ordinal))
                {
                    var nameArg = GetFirstArgument(invocation);
                    var tableName = nameArg != null ? TryExtractName(nameArg.Expression) : null;

                    return new EfMigrationOperation
                    {
                        Kind = EfMigrationOperationKind.DropTable,
                        Operation = "DropTable",
                        Table = tableName,
                        Raw = invocation.ToString()
                    };
                }

                if (parts.Count > 1 && string.Equals(parts[1], "Index", StringComparison.Ordinal))
                {
                    var nameArg = GetFirstArgument(invocation);
                    var indexName = nameArg != null ? TryExtractName(nameArg.Expression) : null;

                    return new EfMigrationOperation
                    {
                        Kind = EfMigrationOperationKind.DropIndex,
                        Operation = "DropIndex",
                        Index = indexName,
                        Raw = invocation.ToString()
                    };
                }
            }

            // === FluentMigrator-style: Schema.Table(nameof(Entity)) – existence checks / table touches ===
            if (string.Equals(parts[0], "Schema", StringComparison.Ordinal) &&
                parts.Count > 1 &&
                string.Equals(parts[1], "Table", StringComparison.Ordinal))
            {
                var nameArg = GetFirstArgument(invocation);
                var tableName = nameArg != null ? TryExtractName(nameArg.Expression) : null;

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.TouchTable,
                    Operation = "TouchTable",
                    Table = tableName,
                    Raw = invocation.ToString()
                };
            }

            // === EF Core-style MigrationBuilder APIs ===
            if (string.Equals(last, "CreateTable", StringComparison.Ordinal))
            {
                var tableName = GetNamedArgumentName(invocation, "name") ?? TryExtractNameFromFirstArgument(invocation);
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.CreateTable,
                    Operation = "CreateTable",
                    Table = tableName,
                    Schema = schema,
                    Raw = invocation.ToString()
                };
            }

            if (string.Equals(last, "DropTable", StringComparison.Ordinal))
            {
                var tableName = GetNamedArgumentName(invocation, "name") ?? TryExtractNameFromFirstArgument(invocation);
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.DropTable,
                    Operation = "DropTable",
                    Table = tableName,
                    Schema = schema,
                    Raw = invocation.ToString()
                };
            }

            if (string.Equals(last, "AddColumn", StringComparison.Ordinal))
            {
                var columnName = GetNamedArgumentName(invocation, "name");
                var tableName = GetNamedArgumentName(invocation, "table");
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.TouchTable,
                    Operation = "AddColumn",
                    Table = tableName,
                    Schema = schema,
                    Column = columnName,
                    Raw = invocation.ToString()
                };
            }

            if (string.Equals(last, "DropColumn", StringComparison.Ordinal))
            {
                var columnName = GetNamedArgumentName(invocation, "name");
                var tableName = GetNamedArgumentName(invocation, "table");
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.TouchTable,
                    Operation = "DropColumn",
                    Table = tableName,
                    Schema = schema,
                    Column = columnName,
                    Raw = invocation.ToString()
                };
            }

            if (string.Equals(last, "RenameColumn", StringComparison.Ordinal))
            {
                var oldName = GetNamedArgumentName(invocation, "name");
                var newName = GetNamedArgumentName(invocation, "newName")
                              ?? GetNamedArgumentName(invocation, "newColumnName");
                var tableName = GetNamedArgumentName(invocation, "table");
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.TouchTable,
                    Operation = "RenameColumn",
                    Table = tableName,
                    Schema = schema,
                    Column = oldName,
                    NewColumn = newName,
                    Raw = invocation.ToString()
                };
            }

            if (string.Equals(last, "AddForeignKey", StringComparison.Ordinal))
            {
                var fkName = GetNamedArgumentName(invocation, "name");
                var tableName = GetNamedArgumentName(invocation, "table");
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.TouchTable,
                    Operation = "AddForeignKey",
                    Table = tableName,
                    Schema = schema,
                    ForeignKey = fkName,
                    Raw = invocation.ToString()
                };
            }

            if (string.Equals(last, "DropForeignKey", StringComparison.Ordinal))
            {
                var fkName = GetNamedArgumentName(invocation, "name");
                var tableName = GetNamedArgumentName(invocation, "table");
                var schema = GetNamedArgumentName(invocation, "schema");

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.TouchTable,
                    Operation = "DropForeignKey",
                    Table = tableName,
                    Schema = schema,
                    ForeignKey = fkName,
                    Raw = invocation.ToString()
                };
            }



            // === Data.* operations: InsertEntity / UpdateEntity / DeleteEntity / (plural variants) ===
            if (string.Equals(last, "InsertEntity", StringComparison.Ordinal) ||
                string.Equals(last, "InsertEntities", StringComparison.Ordinal) ||
                string.Equals(last, "UpdateEntity", StringComparison.Ordinal) ||
                string.Equals(last, "UpdateEntities", StringComparison.Ordinal) ||
                string.Equals(last, "DeleteEntity", StringComparison.Ordinal) ||
                string.Equals(last, "DeleteEntities", StringComparison.Ordinal))
            {
                var firstArg = GetFirstArgument(invocation);
                if (firstArg != null)
                {
                    var entityName = TryExtractEntityNameFromArgument(firstArg.Expression);
                    if (!string.IsNullOrWhiteSpace(entityName))
                    {
                        return new EfMigrationOperation
                        {
                            Kind = EfMigrationOperationKind.DataChange,
                            Operation = last,
                            Table = entityName,
                            Raw = invocation.ToString()
                        };
                    }
                }
            }

            // === Raw SQL inside migrations: Sql("...") ===
            if (string.Equals(last, "Sql", StringComparison.Ordinal))
            {
                var arg = GetFirstArgument(invocation);
                var sql = arg != null
                    ? (TryExtractLiteralValue(arg.Expression) ?? arg.Expression.ToString())
                    : invocation.ToString();

                return new EfMigrationOperation
                {
                    Kind = EfMigrationOperationKind.RawSql,
                    Operation = "RawSql",
                    Raw = sql
                };
            }

            return null;
        }

        private static string GetNamedArgumentName(InvocationExpressionSyntax invocation, string parameterName)
        {
            if (invocation.ArgumentList == null)
                return null;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.NameColon == null)
                    continue;

                var name = arg.NameColon.Name.Identifier.Text;
                if (!string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return TryExtractName(arg.Expression);
            }

            return null;
        }

        private static string TryExtractNameFromFirstArgument(InvocationExpressionSyntax invocation)
        {
            var arg = GetFirstArgument(invocation);
            if (arg == null)
                return null;

            return TryExtractName(arg.Expression);
        }



        /// <summary>
        /// Flatten member access chain into ["Create", "Table", "OnTable", ...].
        /// Root-most identifier comes first.
        /// Supports both simple and generic method names (e.g. AddColumn, AddColumn<int>).
        /// </summary>
        private static List<string> FlattenMemberAccess(ExpressionSyntax expression)
        {
            var parts = new List<string>();

            while (expression is MemberAccessExpressionSyntax ma)
            {
                switch (ma.Name)
                {
                    case IdentifierNameSyntax id:
                        // Simple member name, e.g. CreateTable, DropColumn
                        parts.Insert(0, id.Identifier.Text);
                        break;

                    case GenericNameSyntax gen:
                        // Generic member name, e.g. AddColumn<int>
                        parts.Insert(0, gen.Identifier.Text);
                        break;
                }

                expression = ma.Expression;
            }

            if (expression is IdentifierNameSyntax rootId)
                parts.Insert(0, rootId.Identifier.Text);

            return parts;
        }



        private static ArgumentSyntax GetFirstArgument(InvocationExpressionSyntax invocation)
        {
            if (invocation.ArgumentList == null || invocation.ArgumentList.Arguments.Count == 0)
                return null;

            return invocation.ArgumentList.Arguments[0];
        }

        /// <summary>
        /// Try to resolve a "name-like" value:
        /// - "Customer"
        /// - nameof(Customer)
        /// - nameof(Customer.LoyaltyPoints)
        /// Fallback: raw expression string.
        /// </summary>
        private static string TryExtractName(ExpressionSyntax expression)
        {
            var literal = TryExtractLiteralValue(expression);
            if (literal != null)
                return literal;

            // nameof(...)
            var invocation = expression as InvocationExpressionSyntax;
            if (invocation != null &&
                invocation.Expression is IdentifierNameSyntax id &&
                string.Equals(id.Identifier.Text, "nameof", StringComparison.Ordinal))
            {
                if (invocation.ArgumentList != null && invocation.ArgumentList.Arguments.Count > 0)
                {
                    var argExpr = invocation.ArgumentList.Arguments[0].Expression;
                    var current = argExpr;

                    // Walk to the rightmost identifier, e.g.
                    // nameof(Customer.LoyaltyPoints) -> "LoyaltyPoints"
                    // nameof(Namespace.Customer.Name) -> "Name"
                    while (current != null)
                    {
                        var simpleId = current as IdentifierNameSyntax;
                        if (simpleId != null)
                            return simpleId.Identifier.Text;

                        var member = current as MemberAccessExpressionSyntax;
                        if (member != null)
                        {
                            current = member.Name;
                            continue;
                        }

                        var qualified = current as QualifiedNameSyntax;
                        if (qualified != null)
                        {
                            current = qualified.Right;
                            continue;
                        }

                        break;
                    }
                }
            }

            // Fallback: raw syntax
            return expression.ToString();
        }


        private static string TryExtractLiteralValue(ExpressionSyntax expression)
        {
            var literal = expression as LiteralExpressionSyntax;
            if (literal != null && literal.IsKind(SyntaxKind.StringLiteralExpression))
                return literal.Token.ValueText;

            return null;
        }

        /// <summary>
        /// Try to infer entity type name from the first argument of Data.* call.
        /// Handles:
        /// - Data.InsertEntity(new Post { ... })
        /// - Data.InsertEntities(new[] { new Post { ... } })
        /// - Data.InsertEntities(new Post[] { ... })
        /// </summary>
        private static string TryExtractEntityNameFromArgument(ExpressionSyntax expr)
        {
            // new Post { ... }
            if (expr is ObjectCreationExpressionSyntax oc)
                return GetShortTypeName(oc.Type.ToString());

            // new Post[] { new Post { ... } }
            if (expr is ArrayCreationExpressionSyntax ac && ac.Initializer != null && ac.Initializer.Expressions.Count > 0)
            {
                var first = ac.Initializer.Expressions[0] as ObjectCreationExpressionSyntax;
                if (first != null)
                    return GetShortTypeName(first.Type.ToString());
            }

            // new[] { new Post { ... } }
            if (expr is ImplicitArrayCreationExpressionSyntax iac && iac.Initializer != null && iac.Initializer.Expressions.Count > 0)
            {
                var first = iac.Initializer.Expressions[0] as ObjectCreationExpressionSyntax;
                if (first != null)
                    return GetShortTypeName(first.Type.ToString());
            }

            // Fallback: identifier variable name (used only when the seeds are very simple)
            if (expr is IdentifierNameSyntax id)
                return id.Identifier.Text;

            return null;
        }

        private static string GetShortTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeName;

            var idx = typeName.LastIndexOf('.');
            return idx >= 0 ? typeName.Substring(idx + 1) : typeName;
        }
        private static string GetNamespace(SyntaxNode node)
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


    }



    /// <summary>
    /// High-level description of a single migration class.
    /// </summary>
    public sealed class EfMigrationInfo
    {
        public EfMigrationInfo()
        {
            Attributes = new List<EfMigrationAttribute>();
            UpOperations = new List<EfMigrationOperation>();
            DownOperations = new List<EfMigrationOperation>();
        }

        public string ClassName { get; set; }

        /// <summary>
        /// Namespace of the migration class, if any.
        /// </summary>
        public string Namespace { get; set; }

        /// <summary>
        /// Fully qualified type name (Namespace.ClassName) when available.
        /// </summary>
        public string TypeFullName { get; set; }

        /// <summary>
        /// Full physical path of the C# file that contains this migration.
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// File path relative to the repository root (or raw path if repoRoot was not provided).
        /// </summary>
        public string FileRelativePath { get; set; }

        /// <summary>
        /// Migration-related attributes, e.g. [Migration(2023100501)] or [NopUpdateMigration(...)]. 
        /// </summary>
        public IList<EfMigrationAttribute> Attributes { get; set; }

        /// <summary>
        /// Operations executed in Up().
        /// </summary>
        public IList<EfMigrationOperation> UpOperations { get; set; }

        /// <summary>
        /// Operations executed in Down().
        /// </summary>
        public IList<EfMigrationOperation> DownOperations { get; set; }

        /// <summary>
        /// Raw C# source text of the Up() method (signature + body), if it was found.
        /// </summary>
        public string UpBody { get; set; }
    }

    public sealed class EfMigrationAttribute
    {
        public EfMigrationAttribute()
        {
            Values = new List<string>();
        }

        public string Name { get; set; }

        /// <summary>
        /// Attribute argument values (stringified), with literal values normalized where possible.
        /// </summary>
        public IList<string> Values { get; set; }
    }

    public sealed class EfMigrationOperation
    {
        /// <summary>
        /// Coarse-grained operation kind (CreateTable / DropTable / CreateIndex / DropIndex / RawSql / DataChange / TouchTable).
        /// </summary>
        public EfMigrationOperationKind Kind { get; set; }

        /// <summary>
        /// Logical table name if the operation clearly targets a single table (without schema).
        /// For DataChange this is usually derived from entity type name.
        /// </summary>
        public string Table { get; set; }

        /// <summary>
        /// Optional table schema, when available (for EF-style MigrationBuilder APIs).
        /// </summary>
        public string Schema { get; set; }

        /// <summary>
        /// Index name, if applicable.
        /// </summary>
        public string Index { get; set; }

        /// <summary>
        /// Column name, when the operation targets a single column (AddColumn / DropColumn / RenameColumn).
        /// </summary>
        public string Column { get; set; }

        /// <summary>
        /// New column name for rename operations, when applicable.
        /// </summary>
        public string NewColumn { get; set; }

        /// <summary>
        /// Foreign key name for AddForeignKey / DropForeignKey operations, when applicable.
        /// </summary>
        public string ForeignKey { get; set; }

        /// <summary>
        /// Canonical operation name, e.g. "CreateTable", "DropTable", "AddColumn", "RawSql".
        /// </summary>
        public string Operation { get; set; }

        /// <summary>
        /// Raw snippet of migration DSL or SQL (one invocation).
        /// </summary>
        public string Raw { get; set; }
    }

    public enum EfMigrationOperationKind
    {
        Unknown = 0,
        TouchTable = 1,
        CreateTable = 2,
        DropTable = 3,
        CreateIndex = 4,
        DropIndex = 5,
        RawSql = 6,
        DataChange = 7
    }




}
