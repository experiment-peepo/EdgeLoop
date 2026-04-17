using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using EdgeLoop.Classes;

namespace EdgeLoop.Tests.Stability {
    /// <summary>
    /// Tests for YtDlpService future compatibility features.
    /// These tests verify the defensive coding patterns that protect
    /// against yt-dlp version changes and JSON schema evolution.
    /// </summary>
    public class YtDlpCompatibilityTests : TestBase {

        [Fact]
        public void SafeGetProperty_ShouldReturnDefault_WhenPropertyMissing() {
            // Arrange — simulate a future yt-dlp data object that lacks expected properties
            var anonymousObj = new { Title = "Test Video", SomeNewField = 42 };

            // Act — use reflection-based safe access (same pattern as YtDlpService)
            var method = typeof(YtDlpService).GetMethod("SafeGetProperty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[] { typeof(object), typeof(string), typeof(string) },
                null);
            
            // The string overload: SafeGetProperty(object, string, string)
            var result = method?.Invoke(null, new object[] { anonymousObj, "NonExistentProp", "fallback" });

            // Assert
            result.Should().Be("fallback", "Missing properties should return the default value");
        }

        [Fact]
        public void SafeGetProperty_ShouldReturnActualValue_WhenPropertyExists() {
            // Arrange
            var anonymousObj = new { Title = "Test Video" };

            // Act
            var method = typeof(YtDlpService).GetMethod("SafeGetProperty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[] { typeof(object), typeof(string), typeof(string) },
                null);
            
            var result = method?.Invoke(null, new object[] { anonymousObj, "Title", "fallback" });

            // Assert
            result.Should().Be("Test Video");
        }

        [Fact]
        public void SafeGetProperty_ShouldReturnDefault_WhenObjectIsNull() {
            // Act
            var method = typeof(YtDlpService).GetMethod("SafeGetProperty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[] { typeof(object), typeof(string), typeof(string) },
                null);
            
            var result = method?.Invoke(null, new object[] { null, "Title", "fallback" });

            // Assert
            result.Should().Be("fallback");
        }

        [Fact]
        public void IsDefinitiveError_ShouldReturnTrue_ForPermanentErrors() {
            // Arrange
            var method = typeof(YtDlpService).GetMethod("IsDefinitiveError",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            
            // Act & Assert
            var permanentErrors = new[] {
                "ERROR: Video unavailable",
                "ERROR: This video is private",
                "ERROR: This video has been removed",
                "ERROR: Unsupported URL: https://example.com",
                "ERROR: This account has been terminated",
                "No video formats found"
            };

            foreach (var error in permanentErrors) {
                var result = (bool)method?.Invoke(null, new object[] { error });
                result.Should().BeTrue($"'{error}' should be classified as definitive");
            }
        }

        [Fact]
        public void IsDefinitiveError_ShouldReturnFalse_ForTransientErrors() {
            var method = typeof(YtDlpService).GetMethod("IsDefinitiveError",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            
            var transientErrors = new[] {
                "ERROR: Unable to download webpage",
                "ERROR: HTTP Error 429: Too Many Requests",
                "ERROR: Connection reset by peer",
                null,
                ""
            };

            foreach (var error in transientErrors) {
                var result = (bool)method?.Invoke(null, new object[] { error });
                result.Should().BeFalse($"'{error}' should NOT be classified as definitive");
            }
        }

        [Fact]
        public void YtDlpService_WhenPathNotFound_ShouldBeUnavailable() {
            // Arrange & Act
            var service = new YtDlpService(@"C:\nonexistent\path\yt-dlp.exe");

            // Assert
            service.IsAvailable.Should().BeFalse("Service should be unavailable when yt-dlp path doesn't exist");
        }

        [Fact]
        public async Task GetBestVideoUrlAsync_WhenUnavailable_ShouldReturnNull() {
            // Arrange
            var service = new YtDlpService(@"C:\nonexistent\path\yt-dlp.exe");

            // Act
            var result = await service.GetBestVideoUrlAsync("https://example.com/video", CancellationToken.None);

            // Assert
            result.Should().BeNull("Unavailable service should immediately return null without throwing");
        }

        [Fact]
        public async Task ExtractVideoInfoAsync_WhenUnavailable_ShouldReturnNull() {
            // Arrange
            var service = new YtDlpService(@"C:\nonexistent\path\yt-dlp.exe");

            // Act
            var result = await service.ExtractVideoInfoAsync("https://example.com/video");

            // Assert
            result.Should().BeNull("Unavailable service should immediately return null without throwing");
        }

        [Fact]
        public async Task TryUpdateAsync_WhenPathMissing_ShouldReturnFalse() {
            // Arrange
            var service = new YtDlpService(@"C:\nonexistent\path\yt-dlp.exe");

            // Act
            var result = await service.TryUpdateAsync();

            // Assert
            result.Should().BeFalse("Update should fail gracefully when binary doesn't exist");
        }

        [Fact]
        public void DetectedVersion_WhenUnavailable_ShouldBeNull() {
            // Arrange
            var service = new YtDlpService(@"C:\nonexistent\path\yt-dlp.exe");

            // Assert
            service.DetectedVersion.Should().BeNull("Version should not be detected when service is unavailable");
        }
    }
}

