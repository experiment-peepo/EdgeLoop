using Xunit.Abstractions;

// Tests that start the Flyleaf engine must run after everything else (see EngineTestsLastOrderer).
[assembly: TestCollectionOrderer(EdgeLoop.Tests.EngineTestsLastOrderer.TypeName, EdgeLoop.Tests.EngineTestsLastOrderer.AssemblyName)]

namespace EdgeLoop.Tests
{
    /// <summary>
    /// Runs the <see cref="EngineCollection"/> last. Starting the Flyleaf engine is global and
    /// cannot be undone, and once it is running the view-model tests' players really open their
    /// fake files and URLs, which makes timing-sensitive tests flaky.
    /// </summary>
    public class EngineTestsLastOrderer : ITestCollectionOrderer
    {
        public const string TypeName = "EdgeLoop.Tests.EngineTestsLastOrderer";
        public const string AssemblyName = "EdgeLoop.Tests";

        public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
            => testCollections.OrderBy(c => c.DisplayName == EngineCollection.Name ? 1 : 0); // stable: keeps the rest in order
    }

    /// <summary>Tests that start the real Flyleaf engine. Always run last.</summary>
    [CollectionDefinition(Name)]
    public class EngineCollection
    {
        public const string Name = "Flyleaf engine";
    }
}
