using EdgeLoop.Classes;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    public class TitleSanitizerTests : TestBase
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]                                  // too short
        [InlineData("NA")]
        [InlineData("view")]
        [InlineData("Unknown")]
        [InlineData("[NA] something")]
        [InlineData("UZcPefzEvubIa5")]                       // Iwara video ID
        [InlineData("3f2b8c1e-9d4a-4e6b-8f1a-2c3d4e5f6a7b")] // UUID
        [InlineData("d41d8cd98f00b204e9800998ecf8427e")]     // MD5
        public void IsIdLike_RejectsIdsAndPlaceholders(string? text)
        {
            TitleSanitizer.IsIdLike(text).Should().BeTrue();
        }

        [Theory]
        [InlineData("My Favourite Video")]
        [InlineData("Episode 12")]
        [InlineData("bengugu-hmv-sfx")]
        [InlineData("Summertime")]                           // 10+ letters but no digits
        public void IsIdLike_AcceptsRealTitles(string text)
        {
            TitleSanitizer.IsIdLike(text).Should().BeFalse();
        }

        [Theory]
        [InlineData("<b>Great</b>  &amp;   Fun", null, "Great & Fun")]
        [InlineData("Cool Video - Videos - Hypnotube", null, "Cool Video")]
        [InlineData("Clip | Rule34Video", null, "Clip")]
        [InlineData("Search result (1)", null, "Search result")]
        [InlineData("Best of (2024)", null, "Best of (2024)")]
        [InlineData("Mix by SomeUploader", "pmvhaven.com", "Mix")]
        [InlineData("Mix by SomeUploader", "hypnotube.com", "Mix by SomeUploader")]
        public void Clean_NormalisesScrapedTitles(string raw, string? host, string expected)
        {
            TitleSanitizer.Clean(raw, host).Should().Be(expected);
        }

        [Theory]
        [InlineData("view")]
        [InlineData("  NA ")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Clean_ReturnsNullForPlaceholders(string? raw)
        {
            TitleSanitizer.Clean(raw).Should().BeNull();
        }

        [Fact]
        public void Clean_TruncatesVeryLongTitles()
        {
            var cleaned = TitleSanitizer.Clean(new string('a', 500));

            cleaned.Should().HaveLength(200).And.EndWith("...");
        }

        [Theory]
        [InlineData("my-cool-video-7132", "My Cool Video")]
        [InlineData("some_thing_here", "Some Thing Here")]
        public void DeSlugify_TurnsSlugsIntoTitles(string slug, string expected)
        {
            TitleSanitizer.DeSlugify(slug).Should().Be(expected);
        }

        [Theory]
        [InlineData("https://www.iwara.tv/video/UZcPefzEvubIa5/bengugu-hmv-sfx", "Bengugu Hmv Sfx")]
        [InlineData("https://hypnotube.com/video/seen-my-boyfriend-78110.html", "Seen My Boyfriend")]
        [InlineData("not a url", null)]
        public void FromUrlSlug_UsesTheSitesSlugPosition(string url, string? expected)
        {
            TitleSanitizer.FromUrlSlug(url).Should().Be(expected);
        }
    }
}
