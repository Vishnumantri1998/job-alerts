using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using SendGrid;
using SendGrid.Helpers.Mail;

class Program
{
    // === RSS feeds to monitor (add/remove as you like) ===
    static readonly string[] RSS_FEEDS = new[]
    {
        // Indeed India search RSS
        "https://www.indeed.co.in/rss?q=Full+Stack+.NET+Developer+Angular+Azure&l=India",
        // Indeed global (Remote)
        "https://www.indeed.com/rss?q=Full+Stack+.NET+Developer+Angular+Azure&l=Remote",
        // Remote job boards
        "https://weworkremotely.com/categories/remote-programming-jobs.rss",
        "https://remoteok.com/remote-dev-jobs.rss",
        // Add more company or board RSS feeds here
    };

    // Broadened keywords to match more variations
    static readonly string[] KEYWORDS = new[]
    {
        "full stack", ".net", "dotnet", "c#", ".net core", "asp.net", "angular", "azure", "web api", "microservices", "mvc", "entity framework", "sql"
    };

    static readonly string[] EXPERIENCE_TOKENS = new[]
    {
        "4+", "4 years", "4 yrs", "3+ years", "5+ years", "4-6", "4 - 6", "4–6", "mid-senior", "mid senior", "senior"
    };

    static async Task<int> Main(string[] args)
    {
        try
        {
            // env vars from GitHub secrets
            var sendGridApiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
            var emailFrom = Environment.GetEnvironmentVariable("EMAIL_FROM");
            var emailTo = Environment.GetEnvironmentVariable("EMAIL_TO"); // comma-separated
            var daysLookbackStr = Environment.GetEnvironmentVariable("DAYS_LOOKBACK") ?? "7"; // default 7 while testing

            if (string.IsNullOrWhiteSpace(sendGridApiKey) || string.IsNullOrWhiteSpace(emailFrom) || string.IsNullOrWhiteSpace(emailTo))
            {
                Console.Error.WriteLine("Missing environment variables. Required: SENDGRID_API_KEY, EMAIL_FROM, EMAIL_TO");
                return 2;
            }

            if (!int.TryParse(daysLookbackStr, out int daysLookback))
                daysLookback = 7;

            var cutoff = DateTime.UtcNow.AddDays(-daysLookback);

            var matched = new List<(string title, string link, string summary)>();
            // in-run dedupe set (avoid duplicate entries from multiple feeds)
            var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var http = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            // set a friendly User-Agent — many feeds reject empty/default UA
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JobAlertsBot/1.0 (+https://github.com/)");

            foreach (var feedUrl in RSS_FEEDS)
            {
                try
                {
                    Console.WriteLine($"Fetching feed: {feedUrl}");
                    using var stream = await http.GetStreamAsync(feedUrl);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true });
                    var feed = SyndicationFeed.Load(reader);
                    if (feed == null)
                    {
                        Console.WriteLine($"Feed returned no items: {feedUrl}");
                        continue;
                    }

                    foreach (var item in feed.Items)
                    {
                        var pubDate = item.PublishDate.UtcDateTime;
                        if (pubDate == DateTime.MinValue) pubDate = DateTime.UtcNow;

                        if (pubDate < cutoff) continue;

                        var title = item.Title?.Text ?? "";
                        var summary = item.Summary?.Text ?? "";
                        var link = item.Links?.FirstOrDefault()?.Uri?.ToString() ?? "";

                        // fallback: some feeds put URL in ElementExtensions or in Id
                        if (string.IsNullOrWhiteSpace(link) && !string.IsNullOrWhiteSpace(item.Id) && Uri.IsWellFormedUriString(item.Id, UriKind.Absolute))
                            link = item.Id;

                        var combined = (title + " " + summary).ToLowerInvariant();

                        // require at least one keyword
                        if (!KEYWORDS.Any(k => combined.Contains(k)))
                            continue;

                        // require experience token OR accept if keywords matched (kept for flexibility)
                        if (!EXPERIENCE_TOKENS.Any(tok => combined.Contains(tok)) && !KEYWORDS.Any(k => combined.Contains(k)))
                            continue;

                        // dedupe by link (or title if link empty)
                        var dedupeKey = !string.IsNullOrWhiteSpace(link) ? link : title;
                        if (seenLinks.Contains(dedupeKey)) continue;
                        seenLinks.Add(dedupeKey);

                        matched.Add((title.Trim(), link.Trim(), Truncate(summary, 500).Trim()));
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

            var plainBody = ComposePlainBody(matched);
            var htmlBody = ComposeHtmlBody(matched);

            var client = new SendGridClient(sendGridApiKey);
            var from = new EmailAddress(emailFrom);
            var tos = emailTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(e => new EmailAddress(e)).ToList();

            var msg = new SendGridMessage()
            {
                From = from,
                Subject = subject,
                PlainTextContent = plainBody,
                HtmlContent = htmlBody
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

    static string ComposePlainBody(List<(string title, string link, string summary)> jobs)
    {
        if (jobs == null || jobs.Count == 0)
            return "No new matching jobs found in the monitored feeds.";

        using var sw = new StringWriter();
        sw.WriteLine("Job matches:");
        sw.WriteLine();
        foreach (var j in jobs)
        {
            sw.WriteLine($"- {j.title}");
            if (!string.IsNullOrEmpty(j.link)) sw.WriteLine($"  {j.link}");
            if (!string.IsNullOrWhiteSpace(j.summary)) sw.WriteLine($"  {j.summary}");
            sw.WriteLine();
        }
        return sw.ToString();
    }

    static string ComposeHtmlBody(List<(string title, string link, string summary)> jobs)
    {
        if (jobs == null || jobs.Count == 0)
            return "<p>No new matching jobs found in the monitored feeds.</p>";

        var sb = new StringBuilder();
        sb.Append("<html><body>");
        sb.AppendFormat("<h2>{0} new job(s)</h2>", WebUtility.HtmlEncode(jobs.Count));
        sb.Append("<ul>");
        foreach (var j in jobs)
        {
            var titleHtml = WebUtility.HtmlEncode(j.title);
            var linkHtml = string.IsNullOrWhiteSpace(j.link) ? "" : WebUtility.HtmlEncode(j.link);
            var summaryHtml = WebUtility.HtmlEncode(j.summary);

            sb.Append("<li style='margin-bottom:12px;'>");
            if (!string.IsNullOrWhiteSpace(linkHtml))
                sb.AppendFormat("<a href=\"{0}\" target=\"_blank\" style='font-weight:600'>{1}</a><br/>", linkHtml, titleHtml);
            else
                sb.AppendFormat("<span style='font-weight:600'>{0}</span><br/>", titleHtml);

            if (!string.IsNullOrWhiteSpace(summaryHtml))
                sb.AppendFormat("<div style='color:#333;margin-top:4px'>{0}</div>", summaryHtml);

            sb.Append("</li>");
        }
        sb.Append("</ul>");
        sb.Append("<hr/><div style='font-size:12px;color:#666'>Sent by your GitHub Actions Job Alerts service.</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", " ");
        cleaned = WebUtility.HtmlDecode(cleaned);
        if (cleaned.Length <= maxLen) return cleaned;
        return cleaned.Substring(0, maxLen) + "...";
    }
}
