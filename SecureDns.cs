using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MessengerHub
{
    /// <summary>
    /// Secure DNS (DNS over HTTPS) for the browser engine, in strict mode: lookups are never sent unencrypted.
    /// About once a day the app times every server below and keeps the fastest working ones; the browser
    /// uses the fastest first and the next two as backups. The order is saved, so startup is never slowed.
    /// </summary>
    internal static class SecureDns
    {
        private sealed class Server
        {
            public string Name, Template;
            public Server(string name, string template) { Name = name; Template = template; }
        }

        // Plain (non-filtering) DoH servers. {?dns} makes the browser use GET requests.
        private static readonly Server[] Servers =
        {
            new Server("Cloudflare", "https://cloudflare-dns.com/dns-query{?dns}"),
            new Server("Ali DNS",    "https://dns.alidns.com/dns-query{?dns}"),
            new Server("Google",     "https://dns.google/dns-query{?dns}"),
            new Server("Control D",  "https://freedns.controld.com/p0{?dns}"),
            new Server("AdGuard",    "https://unfiltered.adguard-dns.com/dns-query{?dns}"),
            new Server("NextDNS",    "https://dns.nextdns.io/dns-query{?dns}"),
            new Server("Quad9",      "https://dns10.quad9.net/dns-query{?dns}"),
        };

        private const string OrderKey = "SecureDns";            // fastest first, e.g. "Google,Cloudflare,Quad9"
        private const string CheckedKey = "SecureDnsChecked";   // when the servers were last timed (UTC, round-trip format)
        private const int UseCount = 3;                         // fastest + two backups
        private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
        private static readonly string[] DefaultOrder = { "Cloudflare", "Google", "Quad9" };

        private static int running;

        /// <summary>The servers in use, fastest first (the saved order, or the default before the first timing).</summary>
        public static string[] CurrentOrder(Settings settings)
        {
            var names = (settings.Get(OrderKey) ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => Servers.Any(s => s.Name == n))
                .Take(UseCount)
                .ToArray();
            return names.Length > 0 ? names : DefaultOrder;
        }

        /// <summary>Browser command-line switch that turns on strict DNS over HTTPS with the current servers.</summary>
        public static string BrowserArgument(Settings settings)
        {
            string templates = string.Join(" ",
                CurrentOrder(settings).Select(n => Servers.First(s => s.Name == n).Template));
            // Feature parameters are URL-escaped; Fallback/false = secure mode (never plain DNS).
            return "--enable-features=DnsOverHttps:Fallback/false/Templates/" + Uri.EscapeDataString(templates);
        }

        /// <summary>
        /// Times the servers in the background if the last timing is older than a day. When done, onResult
        /// is called (on a background thread) with the new order; the app saves it and uses it from the next start.
        /// </summary>
        public static void CheckIfDue(Settings settings, Action<string> onResult)
        {
            if (DateTime.TryParse(settings.Get(CheckedKey), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                DateTime.UtcNow - last < CheckEvery)
                return;
            if (System.Threading.Interlocked.Exchange(ref running, 1) == 1) return;

            Task.Run(async () =>
            {
                try
                {
                    var timings = await Task.WhenAll(Servers.Select(TimeServerAsync));
                    var fastest = timings.Where(t => t.Ms < double.MaxValue).OrderBy(t => t.Ms).ToList();
                    Diag.Write("secure DNS timing: " + (fastest.Count == 0 ? "no server answered" :
                        string.Join(", ", fastest.Select(t => t.Name + " " + Math.Round(t.Ms) + " ms"))));
                    if (fastest.Count > 0) onResult(string.Join(",", fastest.Take(UseCount).Select(t => t.Name)));
                }
                catch (Exception ex) { Diag.Write("secure DNS timing failed: " + ex.Message); }
                finally { System.Threading.Interlocked.Exchange(ref running, 0); }
            });
        }

        /// <summary>Saves a new order and the time of the check.</summary>
        public static void Save(Settings settings, string order)
        {
            settings.Set(OrderKey, order);
            settings.Set(CheckedKey, DateTime.UtcNow.ToString("o"));
        }

        // A DNS query for "example.com" (type A) in wire format, base64url-encoded (RFC 8484 GET).
        private const string ProbeQuery = "AAABAAABAAAAAAAAB2V4YW1wbGUDY29tAAABAAE";

        /// <summary>Median of 3 real lookups (ms), or double.MaxValue if the server didn't answer properly.</summary>
        private static async Task<(string Name, double Ms)> TimeServerAsync(Server server)
        {
            string url = server.Template.Replace("{?dns}", "") + "?dns=" + ProbeQuery;
            var times = new List<double>();
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) })
            {
                http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-message");
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using (var resp = await http.GetAsync(url))
                        {
                            byte[] body = await resp.Content.ReadAsByteArrayAsync();
                            sw.Stop();
                            // A valid DNS answer: HTTP 200, at least a 12-byte header, and the response flag set.
                            if (!resp.IsSuccessStatusCode || body.Length < 12 || (body[2] & 0x80) == 0)
                                return (server.Name, double.MaxValue);
                        }
                        times.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    catch { return (server.Name, double.MaxValue); }
                }
            }
            times.Sort();
            return (server.Name, times[1]);
        }
    }
}
