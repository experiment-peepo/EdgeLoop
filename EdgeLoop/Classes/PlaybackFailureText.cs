using System;
using System.Text.RegularExpressions;

namespace EdgeLoop.Classes
{
    /// <summary>Short, plain wording for playback failures, for on-screen notices and Now Playing.</summary>
    public static class PlaybackFailureText
    {
        /// <summary>Why a video failed, as a sentence ("It isn't online anymore (404)."), or null when it isn't clear.</summary>
        public static string Reason(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return null;
            bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);

            if (Has("404")) return "It isn't online anymore (404).";
            if (Has("403")) return "The site refused access (403).";
            if (Has("timeout") || Has("timed out")) return "It took too long to load.";
            if (Has("0x8898050C")) return "The graphics card ran out of memory.";
            if (Has("codec") || Has("0xC00D5212") || Has("not supported") || Has("Invalid data found")) return "Its format isn't supported.";
            if (Has("FileNotFound") || Has("does not exist") || Has("No such file") || Has("could not find file")) return "The file is missing.";
            if (Has("network") || Has("connection") || Has("resolve") || Has("unreachable")) return "There's a network problem.";
            return null;
        }

        /// <summary>The headline for one failed video: "Couldn't play “Title”".</summary>
        public static string Headline(string title) =>
            string.IsNullOrWhiteSpace(title) ? "Couldn't play a video" : $"Couldn't play \u201C{title}\u201D";

        /// <summary>The reason (if known) and what happens next.</summary>
        public static string Detail(string error) =>
            Reason(error) is { } reason ? $"{reason} Skipping to the next video." : "Skipping to the next video.";

        /// <summary>"Screen 2" for "\\.\DISPLAY2".</summary>
        public static string ScreenName(string device)
        {
            var number = Regex.Match(device ?? "", @"DISPLAY(\d+)", RegexOptions.IgnoreCase);
            return number.Success ? $"Screen {number.Groups[1].Value}" : "this screen";
        }
    }
}
