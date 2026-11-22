using System;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>Reading a site's cookies from whatever the user copied, keeping only that site's.</summary>
    public class CookiePasteTests : TestBase
    {
        private const string Site = "hypnotube.com";

        [Fact]
        public void CookiesTxt_FromCookieEditor_KeepsTheHttpOnlySessionAndDropsOtherSites()
        {
            // Cookie-Editor marks HttpOnly cookies with "#HttpOnly_": a parser that skips every "#" line loses PHPSESSID
            var text = "# Netscape HTTP Cookie File\n" +
                       "#HttpOnly_.hypnotube.com\tTRUE\t/\tTRUE\t1795600000\tPHPSESSID\tabc123\n" +
                       ".hypnotube.com\tTRUE\t/\tFALSE\t1795600000\ttheme\tdark\n" +
                       ".youtube.com\tTRUE\t/\tTRUE\t1795600000\tVISITOR_INFO1_LIVE\txyz\n";

            var pasted = CookiePaste.Parse(text, Site);

            pasted.Header.Should().Be("PHPSESSID=abc123; theme=dark");
            pasted.Summary.Should().Be("Found 2 cookies for hypnotube.com in a cookies.txt file; left out 1 cookie for other sites.");
        }

        [Fact]
        public void CookiesTxt_WithWindowsLineEndings_AndNoHttpOnlyMarker()
        {
            var text = "# Netscape HTTP Cookie File\r\nhypnotube.com\tFALSE\t/\tTRUE\t0\tPHPSESSID\tabc123\r\n";

            CookiePaste.Parse(text, Site).Header.Should().Be("PHPSESSID=abc123");
        }

        [Fact]
        public void Json_FromCookieEditor()
        {
            var text = "[{\"domain\":\".hypnotube.com\",\"hostOnly\":false,\"httpOnly\":true,\"name\":\"PHPSESSID\",\"path\":\"/\",\"secure\":true,\"value\":\"abc123\"}," +
                       "{\"domain\":\".google.com\",\"name\":\"NID\",\"value\":\"g\"}]";

            var pasted = CookiePaste.Parse(text, Site);

            pasted.Header.Should().Be("PHPSESSID=abc123");
            pasted.Summary.Should().Contain("an extension's JSON export").And.Contain("left out 1 cookie");
        }

        [Theory]
        // Firefox, POSIX
        [InlineData("curl 'https://hypnotube.com/' -H 'User-Agent: Mozilla/5.0' -H 'Cookie: PHPSESSID=abc123; theme=dark' -H 'Upgrade-Insecure-Requests: 1'")]
        // Firefox, Windows: ^"…^" quoting
        [InlineData("curl.exe ^\"https://hypnotube.com/^\" -H ^\"User-Agent: Mozilla/5.0^\" -H ^\"Cookie: PHPSESSID=abc123; theme=dark^\"")]
        // Chrome/Edge, bash: -b
        [InlineData("curl 'https://hypnotube.com/' \\\n  -H 'accept: text/html' \\\n  -b 'PHPSESSID=abc123; theme=dark' \\\n  -H 'user-agent: Mozilla/5.0'")]
        // Chrome/Edge, cmd: -b ^"…^" and ^ line continuations
        [InlineData("curl ^\"https://hypnotube.com/^\" ^\r\n  -H ^\"accept: text/html^\" ^\r\n  -b ^\"PHPSESSID=abc123; theme=dark^\" ^\r\n  -H ^\"user-agent: Mozilla/5.0^\"")]
        public void CopyAsCurl_FromFirefoxOrChrome_InEitherQuoting(string text)
        {
            var pasted = CookiePaste.Parse(text, Site);

            pasted.Header.Should().Be("PHPSESSID=abc123; theme=dark");
            pasted.Summary.Should().Be("Found 2 cookies for hypnotube.com in a cURL command.");
        }

        [Fact]
        public void CopyAsCurl_ForAnotherSite_SaysWhereTheRequestWent()
        {
            var pasted = CookiePaste.Parse("curl 'https://www.youtube.com/' -H 'Cookie: PHPSESSID=abc123'", Site);

            pasted.Summary.Should().EndWith("Note: that request went to www.youtube.com, not hypnotube.com.");
        }

        [Fact]
        public void CopyAsPowerShell_FromChrome()
        {
            var text = "$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession\n" +
                       "$session.Cookies.Add((New-Object System.Net.Cookie(\"PHPSESSID\", \"abc123\", \"/\", \"hypnotube.com\")))\n" +
                       "$session.Cookies.Add((New-Object System.Net.Cookie(\"_ga\", \"GA1.1\", \"/\", \".google.com\")))\n" +
                       "Invoke-WebRequest -UseBasicParsing -Uri \"https://hypnotube.com/\" -WebSession $session";

            CookiePaste.Parse(text, Site).Header.Should().Be("PHPSESSID=abc123");
        }

        [Fact]
        public void RequestHeaders_FromFirefoxCopyRequestHeaders()
        {
            var text = "GET / HTTP/2\nHost: hypnotube.com\nUser-Agent: Mozilla/5.0\nCookie: PHPSESSID=abc123; theme=dark\nUpgrade-Insecure-Requests: 1";

            CookiePaste.Parse(text, Site).Header.Should().Be("PHPSESSID=abc123; theme=dark");
        }

        [Theory]
        [InlineData("Cookie: PHPSESSID=abc123; theme=dark", "PHPSESSID=abc123; theme=dark")]
        [InlineData("PHPSESSID=abc123; theme=dark", "PHPSESSID=abc123; theme=dark")]
        [InlineData("PHPSESSID=abc123;theme=dark", "PHPSESSID=abc123; theme=dark")] // Cookie-Editor's header string
        [InlineData("h8a2k3m4n5p6q7r8s9t0", "PHPSESSID=h8a2k3m4n5p6q7r8s9t0")]       // just the PHPSESSID value
        public void HeaderValues_Pairs_AndABareSessionValue(string text, string header)
        {
            CookiePaste.Parse(text, Site).Header.Should().Be(header);
        }

        [Theory]
        [InlineData("", "The clipboard is empty")]
        [InlineData("hello world", "Couldn't find any cookies")]
        [InlineData("curl 'https://hypnotube.com/' -H 'accept: text/html'", "Found a cURL command, but no cookies for hypnotube.com")]
        public void NothingUsable_SaysWhy(string text, string summaryStart)
        {
            var pasted = CookiePaste.Parse(text, Site);

            pasted.Found.Should().BeFalse();
            pasted.Summary.Should().StartWith(summaryStart);
        }

        [Fact]
        public void LookAlikeDomains_AreNotThisSite()
        {
            var text = "nothypnotube.com\tFALSE\t/\tFALSE\t0\tPHPSESSID\tevil\n";

            CookiePaste.Parse(text, Site).Found.Should().BeFalse();
        }

        [Fact]
        public void Settings_Paste_KeepsOnlyTheSitesCookies_AndSaysWhatItFound()
        {
            MockSettings.SetupProperty(s => s.HypnotubeCookiesEncrypted);
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>());

            settings.PasteCookies("Hypnotube", "curl 'https://hypnotube.com/' -H 'Cookie: PHPSESSID=abc123; theme=dark'");
            settings.HypnotubeCookies.Should().Be("PHPSESSID=abc123; theme=dark");
            settings.HypnotubePasteSummary.Should().Be("Found 2 cookies for hypnotube.com in a cURL command.");
            settings.HypnotubeCookiesNote.Should().BeNull("PHPSESSID is there");

            settings.PasteCookies("Hypnotube", "hello world");
            settings.HypnotubeCookies.Should().Be("PHPSESSID=abc123; theme=dark", "a paste with nothing usable leaves the box alone");
            settings.HypnotubePasteSummary.Should().Be("Couldn't find any cookies in what was pasted.");
        }

        [Fact]
        public void Settings_Save_ReadsATypedInCurlToo()
        {
            MockSettings.SetupProperty(s => s.HypnotubeCookiesEncrypted);
            var settings = new SettingsViewModel(_ => true, Array.Empty<ScreenViewer>());

            settings.HypnotubeCookies = "curl 'https://hypnotube.com/' -b 'PHPSESSID=abc123'";
            settings.Save();

            MockSettings.Object.HypnotubeCookies.Should().Be("PHPSESSID=abc123");
        }
    }
}
