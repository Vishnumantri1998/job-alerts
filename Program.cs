using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using SendGrid;
using SendGrid.Helpers.Mail;

class Program
{
    // Feeds to check (add/remove as desired)
    static readonly string[] RSS_FEEDS = new[]
    {
        // Many sites block CI runners (e.g., Indeed) — fallback to HTML is attempted below
        "https://www.indeed.co.in/rss?q=Full+Stack+.NET+Developer+Angular+Azure&l=India",
        "https://www.indeed.com/rss?q=Full+Stack+.NET+Developer+Angular+Azure&l=Remote",
        "https://weworkremotely.com/categories/remote-programming-jobs.rss",
        "https://remoteok.com/remote-dev-jobs.rss"
    };

    static readonly string[] KEYWORDS = new[]
    {
        "full stack", ".net", "dotnet", "c#", ".net core", "asp.net", "angular", "azure", "web api", "microservices", "mvc", "entity framework", "sql", "backend", "developer"
    };

    // While testing, keep a bigger window
    const int DEFAULT_DAYS_LOOKBACK = 7;

    static async Task<int> Main(string[] args)
    {
        try
        {
            var sendGridApiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
            var emailFrom = Environment.GetEnvironmentVariable("EMAIL_FROM");
            var emailTo = Environment.GetEnvironmentVariable("EMAIL_TO");
            var daysLookbackStr = Environment.GetEnvironmentVariable("DAYS_LOOKBACK") ?? DEFAULT_DAYS_LOOKBACK.ToString();

            if (string.IsNullOrWhiteSpace(sendGridApiKey) || string.IsNullOrWhiteSpace(emailFrom) || string.IsNullOrWhiteSpace(emailTo))
            {
                Console.Error.WriteLine("Missing environment variables. Required: SENDGRID_API_KEY, EMAIL_FROM, EMAIL_TO");
                return 2;
            }

            if (!int.TryParse(daysLookbackStr, out int daysLookback))
                daysLookback = DEFAULT_DAYS_LOOKBACK;

            var cutoff = DateTime.UtcNow.AddDays(-daysLookback);
            var matched = new List<(string title, string link, string summary)>();
            var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keep feed statuses for the email summary (URL -> status message)
            var feedStatuses = new List<(string url, string status, int itemsFound)>();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JobAlertsBot/1.0 (+https://github.com/)");

            foreach (var feedUrl in RSS_FEEDS)
            {
                Console.WriteLine($"--- Fetching feed: {feedUrl}");
                bool gotRssItems = false;
                int feedMatches = 0;

                try
                {
                    using var stream = await http.GetStreamAsync(feedUrl);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true });
                    var feed = SyndicationFeed.Load(reader);
                    if (feed != null && feed.Items.Any())
                    {
                        Console.WriteLine($"RSS feed loaded: title='{feed.Title?.Text ?? "(no title)"}' items={feed.Items.Count()}");
                        foreach (var item in feed.Items)
                        {
                            var pubDate = item.PublishDate.UtcDateTime;
                            if (pubDate == DateTime.MinValue) pubDate = DateTime.UtcNow;

                            var title = item.Title?.Text ?? "";
                            var summary = item.Summary?.Text ?? "";
                            var link = item.Links?.FirstOrDefault()?.Uri?.ToString() ?? "";
                            if (string.IsNullOrWhiteSpace(link) && !string.IsNullOrWhiteSpace(item.Id) && Uri.IsWellFormedUriString(item.Id, UriKind.Absolute))
                                link = item.Id;

                            Console.WriteLine($"ITEM: {pubDate:u} | Title: {title} | Link: {link}");
                            if (!string.IsNullOrWhiteSpace(summary))
                            {
                                var s = summary.Length > 120 ? summary.Substring(0, 120).Replace("\r", " ").Replace("\n", " ") + "..." : summary;
                                Console.WriteLine($"  SummaryPreview: {s}");
                            }

                            if (pubDate < cutoff) continue;

                            var combined = (title + " " + summary).ToLowerInvariant();
                            if (!KEYWORDS.Any(k => combined.Contains(k))) continue;

                            var dedupeKey = !string.IsNullOrWhiteSpace(link) ? link : title;
                            if (seenLinks.Contains(dedupeKey)) continue;
                            seenLinks.Add(dedupeKey);

                            matched.Add((title.Trim(), link?.Trim() ?? "", Truncate(summary, 500).Trim()));
                            feedMatches++;
                        }

                        gotRssItems = true;
                        feedStatuses.Add((feedUrl, "RSS OK", feedMatches));
                    }
                    else
                    {
                        // RSS loaded but no items — treat as empty, try HTML fallback
                        Console.WriteLine($"RSS loaded but no items: {feedUrl}");
                        feedStatuses.Add((feedUrl, "RSS empty (will attempt HTML fallback)", 0));
                    }
                }
                catch (HttpRequestException hre)
                {
                    // network errors, 403, 401 etc show up here usually with inner message
                    Console.Error.WriteLine($"RSS attempt failed for {feedUrl}: {hre.Message}");
                    // add a temporary status; we'll attempt HTML fallback below
                    feedStatuses.Add((feedUrl, $"RSS failed: {ParseShortHttpError(hre.Message)} (attempting HTML fallback)", 0));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RSS attempt failed for {feedUrl}: {ex.Message}");
                    feedStatuses.Add((feedUrl, $"RSS failed: {ex.Message} (attempting HTML fallback)", 0));
                }

                // If no RSS items or RSS failed, try HTML fallback
                if (!gotRssItems)
                {
                    try
                    {
                        Console.WriteLine($"Trying HTML fallback for: {feedUrl}");
                        var html = await http.GetStringAsync(feedUrl);
                        var aRegex = new Regex("<a[^>]*href=[\"'](?<h>[^\"']+)[\"'][^>]*>(?<t>.*?)</a>",
                                               RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        var matches = aRegex.Matches(html);
                        Console.WriteLine($"HTML anchors found: {matches.Count}");

                        int fallbackMatches = 0;
                        foreach (Match m in matches)
                        {
                            try
                            {
                                var href = m.Groups["h"].Value.Trim();
                                var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups["t"].Value, "<.*?>", " ")).Trim();
                                if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(href)) continue;

                                var combined = (text + " " + href).ToLowerInvariant();
                                if (!KEYWORDS.Any(k => combined.Contains(k))) continue;

                                // Normalize relative URLs
                                if (!Uri.IsWellFormedUriString(href, UriKind.Absolute))
                                {
                                    if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, href, out var abs))
                                        href = abs.ToString();
                                }

                                var summarySnippet = GetSnippetAround(html, m.Index, 150);

                                Console.WriteLine($"HTML-CANDIDATE: Title='{text}' | Href={href}");
                                if (!string.IsNullOrWhiteSpace(summarySnippet))
                                    Console.WriteLine($"  Snippet: {summarySnippet}");

                                var dedupeKey = !string.IsNullOrWhiteSpace(href) ? href : text;
                                if (seenLinks.Contains(dedupeKey)) continue;
                                seenLinks.Add(dedupeKey);

                                matched.Add((text, href, Truncate(summarySnippet, 500)));
                                fallbackMatches++;
                            }
                            catch (Exception inner) { Console.Error.WriteLine($"Anchor parse skip: {inner.Message}"); }
                        }

                        // update or append feed status (replace last entry for this URL if existed)
                        var idx = feedStatuses.FindIndex(f => f.url == feedUrl);
                        if (idx >= 0) feedStatuses[idx] = (feedUrl, $"HTML fallback checked", fallbackMatches);
                        else feedStatuses.Add((feedUrl, "HTML fallback checked", fallbackMatches));
                    }
                    catch (HttpRequestException hre)
                    {
                        Console.Error.WriteLine($"HTML fallback failed for {feedUrl}: {hre.Message}");
                        // mark blocked/forbidden in status
                        var idx = feedStatuses.FindIndex(f => f.url == feedUrl);
                        if (idx >= 0) feedStatuses[idx] = (feedUrl, $"Blocked / HTTP error: {ParseShortHttpError(hre.Message)}", 0);
                        else feedStatuses.Add((feedUrl, $"Blocked / HTTP error: {ParseShortHttpError(hre.Message)}", 0));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"HTML fallback failed for {feedUrl}: {ex.Message}");
                        var idx = feedStatuses.FindIndex(f => f.url == feedUrl);
                        if (idx >= 0) feedStatuses[idx] = (feedUrl, $"HTML fallback error: {ex.Message}", 0);
                        else feedStatuses.Add((feedUrl, $"HTML fallback error: {ex.Message}", 0));
                    }
                }
            } // end feed loop

            Console.WriteLine($"Total matched items: {matched.Count}");

            // Compose subject and bodies
            var subject = matched.Count == 0
                ? $"[Jobs Alert] No matches — {DateTime.UtcNow:yyyy-MM-dd}"
                : $"[Jobs Alert] {matched.Count} matches for Full Stack .NET (Angular/Azure) - {DateTime.UtcNow:yyyy-MM-dd}";

            var plainBody = ComposePlainBodyWithFeedStatus(matched, feedStatuses);
            var htmlBody = ComposeHtmlBodyWithFeedStatus(matched, feedStatuses);

            var client = new SendGridClient(Environment.GetEnvironmentVariable("SENDGRID_API_KEY"));
            var from = new EmailAddress(Environment.GetEnvironmentVariable("EMAIL_FROM"));
            var tos = Environment.GetEnvironmentVariable("EMAIL_TO")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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

    // Helper: short parse for common HTTP message inside HttpRequestException
    static string ParseShortHttpError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        // Example messages include "Response status code does not indicate success: 403 (Forbidden)."
        var m = Regex.Match(message, @"\b(\d{3})\b");
        return m.Success ? $"HTTP {m.Groups[1].Value}" : message.Length > 80 ? message.Substring(0, 80) + "..." : message;
    }

    static string ComposePlainBodyWithFeedStatus(List<(string title, string link, string summary)> jobs, List<(string url, string status, int itemsFound)> feedStatuses)
    {
        using var sw = new StringWriter();
        sw.WriteLine("Feed status summary:");
        foreach (var s in feedStatuses)
            sw.WriteLine($"- {s.url} => {s.status} (matches: {s.itemsFound})");

        sw.WriteLine();
        if (jobs == null || jobs.Count == 0)
        {
            sw.WriteLine("No new matching jobs found in the monitored feeds.");
            return sw.ToString();
        }

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

    static string ComposeHtmlBodyWithFeedStatus(List<(string title, string link, string summary)> jobs, List<(string url, string status, int itemsFound)> feedStatuses)
    {
        var sb = new StringBuilder();
        sb.Append("<html><body style='font-family:Segoe UI,Arial,sans-serif;'>");

        // Feed status
        sb.Append("<h3>Feed status summary</h3>");
        sb.Append("<ul>");
        foreach (var s in feedStatuses)
        {
            sb.AppendFormat("<li><strong>{0}</strong> — {1} (matches: {2})</li>", WebUtility.HtmlEncode(s.url), WebUtility.HtmlEncode(s.status), s.itemsFound);
        }
        sb.Append("</ul>");
        sb.Append("<hr/>");

        if (jobs == null || jobs.Count == 0)
        {
            sb.Append("<p>No new matching jobs found in the monitored feeds.</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        sb.AppendFormat("<h2>{0} new job(s)</h2>", WebUtility.HtmlEncode(jobs.Count.ToString()));
        sb.Append("<ul style='list-style-type:none;padding-left:0;'>");

        foreach (var j in jobs)
        {
            var titleHtml = WebUtility.HtmlEncode(j.title);
            var linkHtml = string.IsNullOrWhiteSpace(j.link) ? "" : WebUtility.HtmlEncode(j.link);
            var summaryHtml = WebUtility.HtmlEncode(j.summary);

            sb.Append("<li style='margin-bottom:16px;border-bottom:1px solid #eee;padding-bottom:8px;'>");

            if (!string.IsNullOrWhiteSpace(linkHtml))
                sb.AppendFormat("<a href=\"{0}\" target=\"_blank\" style='font-weight:600;color:#0078D4;text-decoration:none'>{1}</a><br/>", linkHtml, titleHtml);
            else
                sb.AppendFormat("<span style='font-weight:600;color:#0078D4'>{0}</span><br/>", titleHtml);

            if (!string.IsNullOrWhiteSpace(summaryHtml))
                sb.AppendFormat("<div style='color:#444;margin-top:4px;font-size:14px;line-height:1.4;'>{0}</div>", summaryHtml);

            sb.Append("</li>");
        }

        sb.Append("</ul>");
        sb.Append("<hr style='margin-top:20px;border:none;border-top:1px solid #ddd;'/>");
        sb.Append("<div style='font-size:12px;color:#888;margin-top:10px;'>Sent automatically by your <strong>GitHub Actions Job Alerts</strong> workflow.</div>");
        sb.Append("</body></html>");

        return sb.ToString();
    }

    static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = Regex.Replace(text, "<.*?>", " ");
        cleaned = WebUtility.HtmlDecode(cleaned);
        if (cleaned.Length <= maxLen) return cleaned;
        return cleaned.Substring(0, maxLen) + "...";
    }

    static string GetSnippetAround(string html, int index, int radius)
    {
        try
        {
            var start = Math.Max(0, index - radius);
            var len = Math.Min(radius * 2, Math.Max(0, Math.Min(html.Length - start, radius * 2)));
            var snippet = html.Substring(start, len);
            snippet = Regex.Replace(snippet, "<.*?>", " ");
            snippet = WebUtility.HtmlDecode(snippet);
            snippet = Regex.Replace(snippet, @"\s+", " ").Trim();
            return snippet;
        }
        catch
        {
            return "";
        }
    }
}
