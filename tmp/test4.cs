using System;
using YoutubeDLSharp.Options;

class Program {
    static void Main() {
        var options = new OptionSet();
        options.AddCustomOption<string>("--impersonate", "chrome");
        Console.WriteLine("Success!");
    }
}
