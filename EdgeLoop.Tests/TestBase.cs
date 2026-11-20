using System;
using Moq;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;

// Every test registers its mocks in the static ServiceContainer (and App.Settings reads from it),
// so test classes running in parallel overwrite each other's mocks. Run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EdgeLoop.Tests
{
    public abstract class TestBase : IDisposable
    {
        protected Mock<UserSettings> MockSettings { get; } = new Mock<UserSettings>();
        protected Mock<IVideoUrlExtractor> MockExtractor { get; } = new Mock<IVideoUrlExtractor>();

        protected TestBase()
        {
            ServiceContainer.Clear();
            Logger.MinimumLevel = LogLevel.Debug;

            // Register mock settings
            ServiceContainer.Register(MockSettings.Object);
            ServiceContainer.Register<IVideoUrlExtractor>(MockExtractor.Object);
        }

        public virtual void Dispose()
        {
            ServiceContainer.Clear();
        }
    }
}

