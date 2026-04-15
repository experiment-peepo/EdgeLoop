using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program {
    static async Task Main() {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        try {
            var html = await client.GetStringAsync("https://iwara.tv/video/4fCgpgm5p3MyK");
            System.IO.File.WriteAllText("iwara_html.txt", html);
            Console.WriteLine("HTML saved: " + html.Length + " bytes");
        } catch (Exception ex) {
            Console.WriteLine("Error: " + ex.Message);
        }
    }
}
