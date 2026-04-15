using System;
using System.Threading.Tasks;
using GOON.Classes;
using System.Threading;

class Program {
    static async Task Main() {
        var service = new YtDlpService("./Dependencies/yt-dlp.exe");
        var info = await service.ExtractVideoInfoAsync("https://iwara.tv/video/hZlq9eUUg3IWQk", CancellationToken.None);
        if (info != null) {
            Console.WriteLine("URL: " + info.Url);
            Console.WriteLine("Title: " + info.Title);
        } else {
            Console.WriteLine("Failed!");
        }
    }
}
