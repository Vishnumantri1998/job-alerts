using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml;
using SendGrid;
using SendGrid.Helpers.Mail;

class Program
{
    // Put feed URLs here (update with more feeds you want)
    static readonly string[] RSS_FEEDS = new[]
    {
        // Example Indeed (change to your country domain if needed)
        "https://www.indeed.com/rss?q=Full+Stack+.NET+Developer+Angular+Azure&l=India"
        // Add more RSS feed URLs here
    };

    static readonly string[] KEYWORDS = new[] { "full stack", ".net", "dotnet", "angular", "azure" };
    static readonly string[] EXPERIENCE_TOKENS = new[] { "4+", "4 years", "4 yrs", "4-6", "4 - 6", "4–6", "mid-senior", "mid senior" };

    static async Task<int> Main(string[] args)
    {
        try
        {
            // Configuration via environment variables (set these in GitHub secrets)
            var sendGridApiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
            var emailFrom = Environment.GetEnvironmentVariable("EMAIL_FROM");
            var emailTo = Environment.GetEnvironmentVariable("EMAIL_TO"); // comma separated
            var daysLookbackStr = Environment.GetEnvironmentVariable("DAYS_LOOKBACK") ?? "2";
            if (string.IsNullOrWhiteSpace(sendGridApiKey) || string.IsNullOrWhiteSpace(emailFrom) || string.IsNullOrWhiteSpace(emailTo))
            {
                Console.Error.WriteLine("Missing environment variables. Required: SENDGRID_API_KEY, EMAIL_FROM, EMAIL_TO");
                return 2;
            }

            if (!int.TryParse(daysLookbackStr, out int daysLookback))
                daysLookback = 2;

            var cutoff = DateTime.UtcNow.AddDays(-daysLookback);

            var matched = new List<(string title, string link, string summary)>();
            using var http = new HttpClient();

            foreach (var feedUrl in RSS_FEEDS)
            {
                try
                {
                    Console.WriteLine($"Fetching feed: {feedUrl}");
                    using var stream = await http.GetStreamAsync(feedUrl);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true });
                    var feed = SyndicationFeed.Load(reader);
                    if (feed == null) continue;

                    foreach (var item in feed.Items)
                    {
                        // Published date fallback
                        var pubDate = item.PublishDate.UtcDateTime;
                        if (pubDate == DateTime.MinValue) pubDate = DateTime.UtcNow; // treat unknown as now

                        if (pubDate < cutoff) continue;

                        var title = item.Title?.Text ?? "";
                        var summary = item.Summary?.Text ?? "";
                        var link = item.Links?.FirstOrDefault()?.Uri?.ToString() ?? "";

                        var combined = (title + " " + summary).ToLowerInvariant();

                        if (!KEYWORDS.Any(k => combined.Contains(k)))
                            continue;

                        if (EXPERIENCE_TOKENS.Any(tok => combined.Contains(tok)) || KEYWORDS.Any(k => combined.Contains(k)))
                        {
                            matched.Add((title, link, Truncate(summary, 500)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to read feed {feedUrl}: {ex.Message}");
                }
            }

            var subject = matched.Count == 0
                ? $"[Jobs Alert] No matches — {DateTime.UtcNow:yyyy-MM-dd}"
                : $"[Jobs Alert] {matched.Count} matches for Full Stack .NET (Angular/Azure) - {DateTime.UtcNow:yyyy-MM-dd}";

            var body = ComposeBody(matched);

            var client = new SendGridClient(sendGridApiKey);
            var from = new EmailAddress(emailFrom);
            var tos = emailTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(e => new EmailAddress(e)).ToList();

            var msg = new SendGridMessage()
            {
                From = from,
                Subject = subject,
                PlainTextContent = body
            };
            msg.AddTos(tos);

            Console.WriteLine("Sending email via SendGrid...");
            var resp = await client.SendEmailAsync(msg);
            Console.WriteLine($"SendGrid status: {(int)resp.StatusCode} {resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
            {
                var respBody = await resp.Body.ReadAsStringAsync();
                Console.Error.WriteLine("SendGrid error response: " + respBody);
                return 3;
            }

            Console.WriteLine("Done. Email sent.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unhandled error: " + ex);
            return 99;
        }
    }

    static string ComposeBody(List<(string title, string link, string summary)> jobs)
    {
        if (jobs == null || jobs.Count == 0)
            return "No new matching jobs found in the monitored feeds.";

        using var sw = new StringWriter();
        foreach (var j in jobs)
        {
            sw.WriteLine($"- {j.title}");
            if (!string.IsNullOrEmpty(j.link)) sw.WriteLine($"  {j.link}");
            if (!string.IsNullOrWhiteSpace(j.summary)) sw.WriteLine($"  {j.summary}");
            sw.WriteLine();
        }
        return sw.ToString();
    }

    static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", " "); // remove HTML tags crudely
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
        if (cleaned.Length <= maxLen) return cleaned;
        return cleaned.Substring(0, maxLen) + "...";
    }
}
