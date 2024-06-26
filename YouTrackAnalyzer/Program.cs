﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using CommandLine;
using Humanizer;
using JetBrains.TeamCity.ServiceMessages.Write.Special;
using YouTrackSharp;
using YouTrackSharp.Generated;
using YouTrackSharp.Issues;
using Issue = YouTrackSharp.Issues.Issue;

namespace YouTrackAnalyzer
{
    public static class Program
    {
        private const string SearchFiler = "#Unresolved Assignee: Unassigned order by: updated";
        private static readonly TimeSpan TimeThreshold = TimeSpan.FromDays(7);
        private static Config ourConfig;

        public static async Task Main(string[] args)
        {
            try
            {
                await Task.Run(() => Parser.Default.ParseArguments<Config>(args)
                    .WithParsed(c => { ourConfig = c; })
                    .WithNotParsed(HandleParseError));
                
                if (ourConfig == null)
                    return;

                var textBuilder = new TextBuilder();
                var connection = new BearerTokenConnection(ourConfig.HostUrl, ourConfig.Token);
                var commentThreshold = ourConfig.CommentThreshold;

                var sw = Stopwatch.StartNew();
                
                var issuesService = connection.CreateIssuesService();
                
                var taggedIssues = new List<Issue>();
                if (!string.IsNullOrEmpty(ourConfig.TagForHotIssues))
                {
                    taggedIssues.AddRange( await issuesService.GetIssues($"tag: {ourConfig.TagForHotIssues}", take:100));
                }

                string filter = $"{SearchFiler} {ourConfig.SearchCondition}";
                
                var list = new List<Issue>();
                for (int i = 0; i < 20; i++)
                {
                    var dexpIssues = await issuesService.GetIssuesInProject(
                        "DEXP", filter, skip: i * 100, take: 100,
                        updatedAfter: DateTime.Now - TimeThreshold);
                    list.AddRange(dexpIssues);
                }

                await RemoveTags(taggedIssues, list, issuesService);

                var dexpHotIssues = list
                    .Where(it => it.Comments.Count > commentThreshold || it.GetField("created").AsDateTime() > DateTime.Now - TimeSpan.FromDays(15) &&  it.Comments.Count > commentThreshold / 2)
                    .OrderByDescending(it => it.Comments.Count)
                    .ToList();

                if (!string.IsNullOrEmpty(ourConfig.TagForHotIssues))
                {
                    var tasks = dexpHotIssues.Select(issue => issuesService.SetTag(issue, ourConfig.TagForHotIssues));
                    //await Task.WhenAll(tasks); // too many network requests simultaneously would fail
                    Console.WriteLine($"Setting tags {ourConfig.TagForHotIssues}");
                    foreach (var task in tasks)
                    {
                        Console.Write(".");
                        await task;
                    }
                    Console.WriteLine("Finished.");
                }

                var topHotTextBuilder = new TextBuilder();
                var dexpTopHotIssues = dexpHotIssues.Take(ourConfig.HotIssuesAmount);

                var dexpHotAggregated = Aggregate(dexpHotIssues);
                var dexpTopAggregated = AggregateTop(dexpTopHotIssues);
                sw.Stop();
                textBuilder.AppendHeader("DEXP HOT (" + dexpHotIssues.Count + ")");
                var maxCount = dexpHotIssues.Count >= ourConfig.HotIssuesAmount ? ourConfig.HotIssuesAmount : dexpHotIssues.Count;
                topHotTextBuilder.AppendHeader($"Top {maxCount} of {dexpHotIssues.Count} hot issues");

                textBuilder.AppendLine(dexpHotAggregated.ToPlainText(), dexpHotAggregated.ToHtml());
                textBuilder.AppendHeader("Statistics");
                textBuilder.AppendKeyValue("Time", $"{sw.Elapsed.TotalSeconds:0.00} sec");
                textBuilder.AppendKeyValue("dexpIssues.Count", list.Count.ToString());
                textBuilder.AppendKeyValue("dexpHotIssues.Count", dexpHotIssues.Count.ToString());
                topHotTextBuilder.AppendLine(dexpTopAggregated, dexpTopAggregated);

                await File.WriteAllTextAsync("report.html", textBuilder.ToHtml());
                await File.WriteAllTextAsync("report.txt", textBuilder.ToPlainText());

                Console.WriteLine(topHotTextBuilder.ToPlainText());
                using (var writer = new TeamCityServiceMessages().CreateWriter(Console.WriteLine))
                {
                    writer.WriteBuildParameter("env.short_report", topHotTextBuilder.ToPlainText());
                }
            }
            catch (UnauthorizedConnectionException e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Can't establish a connection to YouTrack");
                Console.WriteLine(e.Demystify());
                Console.ResetColor();
            }
        }

        private static async Task RemoveTags(List<Issue> taggedIssues, List<Issue> list, IIssuesService issuesService)
        {
            Console.WriteLine($"Removing tags {ourConfig.TagForHotIssues} from {taggedIssues.Count} issues");
            foreach (var issue in taggedIssues.Where(issue1 => !list.Contains(issue1)))
            {
                Console.Write(".");
                try
                {
                    await issuesService.RemoveTag(issue, ourConfig.TagForHotIssues);
                }
                catch (YouTrackErrorException)
                {
                    Console.WriteLine($"Failed to remove tag silently ${ourConfig.TagForHotIssues} from ${issue.Id}");
                    await issuesService.RemoveTag(issue, ourConfig.TagForHotIssues, false);
                }
            }

            Console.WriteLine("Finished.");
        }

        private static Task RemoveTag(this IIssuesService issuesService, Issue issue, string tagForHotIssues, bool disableNotifications = true)
        {
            return issuesService.ApplyCommand(issue.Id, $"remove tag {tagForHotIssues}", disableNotifications : disableNotifications);
        }

        private static Task SetTag(this IIssuesService issuesService, Issue issue, string tagForHotIssues)
        {
            return issuesService.ApplyCommand(issue.Id, $"tag {tagForHotIssues}", disableNotifications : true);
        }

        private static TextBuilder Aggregate(IEnumerable<Issue> dexpHotIssues)
        {
            var sb = new TextBuilder();
            foreach (var issue in dexpHotIssues)
            {
                var id = issue.Id;
                var url = ourConfig.HostUrl + "issue/" + id;

                var title = issue.Summary.Truncate(80, "...").Replace("<", "&lt;").Replace(">", "&gt;");
                var comments = "comment".ToQuantity(issue.Comments.Count);
                sb.AppendLine(
                    $"{id} {title} / {comments}",
                    $"<a target=\"_blank\" href=\"{url}\">{id}</a> {title} / <b>{comments}</b>");
            }

            return sb;
        }
        
        private static string AggregateTop(IEnumerable<Issue> dexpHotIssues)
        {
            var sb = new StringBuilder();
            foreach (var issue in dexpHotIssues)
            {
                var id = issue.Id;
                var url = ourConfig.HostUrl + "issue/" + id;

                var title = issue.Summary.Truncate(80, "...").Replace("<", "&lt;").Replace(">", "&gt;")
                  .Replace("“", "'").Replace("”", "'").Replace("\"", "'").Replace("\"", "'")
                  .Replace("\'", String.Empty)
                  .Replace(@"\", "/")
                  .Replace("$", "'$'");
                title = HttpUtility.JavaScriptStringEncode(title);
                title = Regex.Replace(title, @"[^\u0000-\u007F]+", string.Empty);
                var comments = "comment".ToQuantity(issue.Comments.Count);
                sb.AppendLine($"<{url}|{id}> {title} / {comments}");
            }

            return sb.ToString();
        }

        private static void HandleParseError(IEnumerable<Error> errs)
        {
            foreach (var error in errs)
            {
                Console.Error.WriteLine(error);
            }
        }
    }
}