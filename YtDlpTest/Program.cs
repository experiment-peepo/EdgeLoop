using System;
using YoutubeDLSharp;

var ytdl = new YoutubeDL() { YoutubeDLPath = @"D:\Projects\EdgeLoop-Final\EdgeLoop\EdgeLoop\bin\Debug\net10.0-windows\yt-dlp.exe", OutputFolder = System.IO.Path.GetTempPath() };
var opts = new YoutubeDLSharp.Options.OptionSet();
opts.AddCustomOption<string>("--impersonate", "chrome");

var u1 = "https://iwara.tv/video/uGOkFiDbAZPh2L";
var u2 = "https://iwara.tv/video/hZlq9eUUg3IWQk";

var t1 = ytdl.RunVideoDataFetch(u1, overrideOptions: opts).GetAwaiter().GetResult();
Console.WriteLine($"U1 Title: {t1.Data.Title}");
Console.WriteLine($"U1 URL: {t1.Data.Url}");

var t2 = ytdl.RunVideoDataFetch(u2, overrideOptions: opts).GetAwaiter().GetResult();
Console.WriteLine($"U2 Title: {t2.Data.Title}");
Console.WriteLine($"U2 URL: {t2.Data.Url}");

