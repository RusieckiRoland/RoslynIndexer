using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynIndexer.Tests.CustomEntities
{
    /// <summary>
    /// Test seed that simulates projects where entities inherit from a common base type
    /// (e.g. NopCommerce-style BaseEntity or custom EntityBase).
    /// </summary>
    internal static class DbGraphEntitySeed
    {
        /// <summary>
        /// Creates a set of Roslyn syntax trees that imitates NopCommerce-style entities
        /// and another project with a different base entity type.
        /// 
        /// Patterns covered:
        /// - Nop.Core.BaseEntity + derived entities (Product, Customer)
        /// - MyProject.Domain.EntityBase + derived entity (Order)
        /// - A class that does NOT inherit from any entity base (negative example)
        /// </summary>
        internal static IReadOnlyList<SyntaxTree> CreateNopStyleAndCustomEntityTrees()
        {
            var sources = new[]
            {
                // 1) Nop-style BaseEntity
                new
                {
                    Path = @"Nop.Core\BaseEntity.cs",
                    Text = @"
using System;

namespace Nop.Core
{
    /// <summary>
    /// Simplified NopCommerce-style base entity.
    /// </summary>
    public abstract class BaseEntity
    {
        public int Id { get; set; }
    }
}
"
                },

                // 2) Nop-style Product entity
                new
                {
                    Path = @"Nop.Core\Domain\Catalog\Product.cs",
                    Text = @"
using Nop.Core;

namespace Nop.Core.Domain.Catalog
{
    /// <summary>
    /// Example entity that should be discovered as ENTITY via Nop.Core.BaseEntity.
    /// </summary>
    public class Product : BaseEntity
    {
        public string Name { get; set; }
    }
}
"
                },

                // 3) Nop-style Customer entity
                new
                {
                    Path = @"Nop.Core\Domain\Customers\Customer.cs",
                    Text = @"
using Nop.Core;

namespace Nop.Core.Domain.Customers
{
    /// <summary>
    /// Another entity that should be discovered as ENTITY via Nop.Core.BaseEntity.
    /// </summary>
    public class Customer : BaseEntity
    {
        public string Email { get; set; }
    }
}
"
                },

                // 4) Other project: EntityBase in a different namespace
                new
                {
                    Path = @"MyProject\Domain\EntityBase.cs",
                    Text = @"
using System;

namespace MyProject.Domain
{
    /// <summary>
    /// Custom base type used as an entity root in another project.
    /// </summary>
    public abstract class EntityBase
    {
        public Guid Id { get; set; }
    }
}
"
                },

                // 5) Derived entity from MyProject.Domain.EntityBase
                new
                {
                    Path = @"MyProject\Domain\Orders\Order.cs",
                    Text = @"
using System;

namespace MyProject.Domain.Orders
{
    /// <summary>
    /// Example entity that should be discovered as ENTITY via MyProject.Domain.EntityBase.
    /// </summary>
    public class Order : MyProject.Domain.EntityBase
    {
        public string Number { get; set; }
    }
}
"
                },

                // 6) Class that should NOT be treated as entity (no entity base type)
                new
                {
                    Path = @"Nop.Core\Domain\Misc\NotAnEntity.cs",
                    Text = @"
namespace Nop.Core.Domain.Misc
{
    /// <summary>
    /// This class does not inherit from any configured entity base type
    /// and should NOT be discovered as ENTITY.
    /// </summary>
    public class NotAnEntity
    {
        public int Value { get; set; }
    }
}
"
                }
            };

            var trees = new List<SyntaxTree>();

            foreach (var s in sources)
            {
                // Important: we set 'path' so that graph-building code can use FilePath
                var tree = CSharpSyntaxTree.ParseText(
                    s.Text,
                    encoding: Encoding.UTF8,
                    path: s.Path);

                trees.Add(tree);
            }

            return trees;
        }
    }
}
