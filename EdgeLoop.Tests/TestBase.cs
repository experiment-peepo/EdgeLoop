using System;
using Moq;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Tests {
    public abstract class TestBase : IDisposable {
        protected Mock<UserSettings> MockSettings { get; } = new Mock<UserSettings>();
        protected Mock<IVideoUrlExtractor> MockExtractor { get; } = new Mock<IVideoUrlExtractor>();
        
        protected TestBase() {
            ServiceContainer.Clear();
            Logger.MinimumLevel = LogLevel.Debug;
            
            // Register mock settings
            ServiceContainer.Register(MockSettings.Object);
            ServiceContainer.Register<IVideoUrlExtractor>(MockExtractor.Object);
        }

        public virtual void Dispose() {
            // Signal logger to flush if possible (though background thread is tricky)
            Thread.Sleep(500); // Give time to flush
            ServiceContainer.Clear();
        }
    }
}

