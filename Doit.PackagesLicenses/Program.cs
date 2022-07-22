using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using NuGet.Common;
using NuGet.Protocol;
using PackageLicenses;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Doit.PackagesLicenses
{
    internal class Logger : ILogger
    {
        public void Log(LogLevel level, string data) => $"{level.ToString().ToUpper()}: {data}".Dump();

        public void Log(ILogMessage message) => Task.FromResult(0);

        public Task LogAsync(LogLevel level, string data) => Task.FromResult(0);

        public Task LogAsync(ILogMessage message) => throw new NotImplementedException();

        public void LogDebug(string data) => $"DEBUG: {data}".Dump();

        public void LogError(string data) => $"ERROR: {data}".Dump();

        public void LogInformation(string data) => $"INFORMATION: {data}".Dump();

        public void LogInformationSummary(string data) => $"SUMMARY: {data}".Dump();

        public void LogMinimal(string data) => $"MINIMAL: {data}".Dump();

        public void LogVerbose(string data) => $"VERBOSE: {data}".Dump();

        public void LogWarning(string data) => $"WARNING: {data}".Dump();
    }

    internal static class LogExtension
    {
        public static void Dump(this string value) => Console.WriteLine(value);
    }

    internal class Program
    {
        private static Regex reg = new Regex(@"\'(?<name>.*)\'", RegexOptions.Compiled);
        private static Regex entryReg = new Regex(@"> ?(?<name>[^\s]+)\s+(?<version>[0-9a-zA-Z\.-_]+)", RegexOptions.Compiled);
        private const int ERROR_PACKAGE_PATH = 0xA0;
        private static IConfigurationRoot _configuration = null;
        private static string _outputPath = "";

        private static void Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
              .AddCommandLine(args, new Dictionary<string, string>()
              {
                  {"-p", "project"},
                  {"-r", "recursive"},
                  {"-ip", "includeproject"},
              });

            string env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            if (string.IsNullOrWhiteSpace(env))
            {
                env = "Development";
            }

            if (env == "Development")
            {
                builder.AddUserSecrets<Program>();
            }
            _configuration = builder.Build();

            var projectPath = _configuration.GetSection("project").Get<string>();
            var recursive = _configuration.GetSection("recursive").Get<bool?>() ?? true;
            var includeProject = _configuration.GetSection("includeproject").Get<bool?>() ?? false;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                if (!projectPath.StartsWith("\""))
                {
                    projectPath = "\"" + projectPath;
                }
                if (!projectPath.EndsWith("\""))
                {
                    projectPath = projectPath + "\"";
                }
            }

            Console.WriteLine($"Starting dotnet list {projectPath} package --include-transitive");
            var dotnetProc = new Process();
            dotnetProc.StartInfo = new ProcessStartInfo("dotnet", $"list {projectPath} package --include-transitive");
            dotnetProc.StartInfo.RedirectStandardOutput = true;
            dotnetProc.Start();
            var foundVersions = ReadAllLines(dotnetProc.StandardOutput, dotnetProc).ToList();
            var checkList = foundVersions
                .GroupBy(f => f.Package)
                .Select(g => new { k = g.Key.ToLower(), d = g.GroupBy(e => e.Version).ToDictionary(k => k.Key, v => v.ToList()) })
                .ToDictionary(g => g.k, g => g.d);

            Console.Write("Change product and company name in the applicationSettings.json");
            Console.WriteLine("----");
            var path = _configuration["Path"];

            if (!Directory.Exists(path))
            {
                Console.Write("Path not Found: " + path);
                Environment.ExitCode = ERROR_PACKAGE_PATH;
                return;
            }

            _outputPath = _configuration["OutputPath"];

            var log = new Logger();

            // GitHub Client ID and Client Secret
            const string LicenseUtilityClientId = "LicenseUtility.ClientId";
            // Add user-secretes with command line:  dotnet user-secrets set LicenseUtility.ClientId XXXX-XX-ID...
            Console.WriteLine($"The Secret Id is {_configuration[LicenseUtilityClientId]}");

            LicenseUtility.ClientId = _configuration[LicenseUtilityClientId];
            const string LicenseUtilityClientSecret = "LicenseUtility.ClientSecret";
            // Add user-secretes with command line:  dotnet user-secrets set LicenseUtility.ClientSecret XYZ...
            LicenseUtility.ClientSecret = _configuration[LicenseUtilityClientSecret];
            Console.WriteLine($"The Client secret is {_configuration[LicenseUtilityClientId]}");

            var packages = PackageLicensesUtility.GetPackages(path, log);
            var list = new List<(LocalPackageInfo, License)>();
            var t = Task.Run(async () =>
            {
                foreach (var p in packages)
                {
                    if (!checkList.TryGetValue(p.Identity.Id, out Dictionary<string, List<Result>> usedPackageVersions))
                        continue;
                    if (!usedPackageVersions.TryGetValue(p.Identity.Version.ToString(), out List<Result> packageProjects))
                        continue;
                    if (p.Identity.Id.StartsWith("r4."))
                    {
                        foreach (var project in packageProjects)
                            project.IgnoredReason = "is R4 Lib";
                        Console.WriteLine("Ignore R4 Libs");
                        continue;
                    }


                    Console.WriteLine($"{p.Nuspec.GetId()}.{p.Nuspec.GetVersion()}");
                    if (p.Nuspec.GetAuthors().ToLower().StartsWith("microsoft"))
                    {
                        foreach (var project in packageProjects)
                            project.IgnoredReason = "is Microsoft Package";
                        Console.WriteLine("Ignore Microsoft");
                        continue;
                    }

                    if (p.Nuspec.GetAuthors().ToLower().StartsWith("jetbrain"))
                    {
                        foreach (var project in packageProjects)
                            project.IgnoredReason = "is authored by jetbrains";
                        Console.WriteLine("Ignore Jetbrain");
                        continue;
                    }

                    if (p.Nuspec.GetAuthors().ToLower().StartsWith("xunit"))
                    {
                        foreach (var project in packageProjects)
                            project.IgnoredReason = "is authored by XUnit";
                        Console.WriteLine("Ignore xUnit.net [Testing Framework]");
                        continue;
                    }

                    var license = await p.GetLicenseAsync(log);
                    //list.Add((p, license));
                    foreach (var project in packageProjects)
                    {
                        project.Licence = license?.Name;
                        project.LicenceUrl = license?.DownloadUri?.AbsoluteUri;
                    }
                }
            });
            t.Wait();

            try
            {
                var remainingPackages = foundVersions.Where(v => !v.Project.ToLower().Contains("test") && v.IgnoredReason == null);

                var printedVersions =
                    includeProject
                        ? remainingPackages
                            .GroupBy(v => new { v.Project, v.Package, v.Version })
                            .Select(v => new Result()
                            {
                                Project = v.Key.Project,
                                Package = v.Key.Package,
                                Version = v.Key.Version,
                                Licence = v.First().Licence,
                                LicenceUrl = v.First().LicenceUrl
                            })
                            .ToList()
                        : remainingPackages
                            .GroupBy(v => new { v.Package, v.Version })
                            .Select(v => new Result()
                            {
                                Project = string.Empty,
                                Package = v.Key.Package,
                                Version = v.Key.Version,
                                Licence = v.First().Licence,
                                LicenceUrl = v.First().LicenceUrl
                            })
                            .ToList();
                CreateWorkbook(printedVersions, includeProject);
            }
            catch (Exception)
            {
                throw;
            }

            Console.WriteLine("Completed.");
        }

        private static IEnumerable<Result> ReadAllLines(StreamReader reader, Process proc)
        {
            var buff = string.Empty;
            var proj = string.Empty;
            while ((buff = reader.ReadLine()) != null || !proc.HasExited)
            {
                if (buff == null)
                {
                    Thread.Sleep(10);
                    continue;
                }
                var m = reg.Match(buff);
                if (m.Success)
                {
                    proj = m.Value;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(buff))
                {
                    var entryMatch = entryReg.Match(buff);
                    if (entryMatch.Success)
                        yield return new Result() { Project = proj, Package = entryMatch.Groups["name"].Value, Version = entryMatch.Groups["version"].Value };
                }
            }
        }

        private class Result
        {
            public string Project { get; set; }
            public string Package { get; set; }
            public string Version { get; set; }
            public string Licence { get; set; }
            public string LicenceUrl { get; set; }
            public string IgnoredReason { get; set; }

            public override string ToString() => $"{Project}->{Package} ({Version}) with '{Licence}' from '{LicenceUrl}'{(IgnoredReason != null ? $" Ignored because {IgnoredReason}" : "")}";
        }
        private static string GetTitle(NuGet.Packaging.NuspecReader nuspec)
        {
            if (nuspec.GetTitle()?.Length > 0)
                return nuspec.GetTitle();

            return nuspec.GetId();
        }

        private static string GetLicence(License license)
        {
            if (license?.Name?.Length > 0)
                return license.Name;

            if (license?.Text?.Length > 0)
                return ParseLicence(license.Text);

            return null;
        }

        private static string GetLicence(NuGet.Packaging.NuspecReader nuspec)
        {
            using var webClient = new WebClient();
            var url = nuspec.GetLicenseUrl();

            if (string.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                var text = webClient.DownloadString(url);
                return ParseLicence(text);
            }
            catch
            {
                return null;
            }
        }

        private static string ParseLicence(string text)
        {
            if (text == null)
                return null;

            text = text.ToLower();

            if (text.Contains("apache licence"))
            {
                if (text.Contains("version 2.0"))
                    return "Apache License 2.0";

                return "Apache";
            }

            if (text.Contains("mit license"))
                return "MIT License";

            if (text.Contains("unlicence"))
                return "The Unlicense";

            if (text.Contains("new bsd license"))
                return "New BSD License";

            if (text.Contains("gpl licence"))
                return "GPL";

            return null;
        }

        private static void CreateWorkbook(List<Result> list, bool includeProject)
        {
            var book = new XLWorkbook();
            var sheet = book.Worksheets.Add("Packages");

            // header
            var headers = new[] {
                includeProject ? "Project" : null,
                "Title", 
                "Licence", 
                "LicenceUrl", 
                "ProjectUrl" 
            }.Where(h => h != null);

            var cell = 1;
            foreach (var h in headers)
            {
                sheet.Cell(1, cell++).SetValue(h).Style.Font.SetBold();
            }

            // values
            var row = 2;
            foreach (var result in list)
            {
                cell = 1;
                //var nuspec = package.Nuspec;
                if (includeProject)
                    sheet.Cell(row, cell++).SetValue(result.Project);
                sheet.Cell(row, cell++).SetValue(result.Package);
                sheet.Cell(row, cell++).SetValue(result.Version);
                sheet.Cell(row, cell++).SetValue(result.Licence);
                sheet.Cell(row, cell++).SetValue(result.LicenceUrl);

                row++;
            }

            var filePath = _outputPath + "Licenses.xlsx";

            if (File.Exists(filePath))
                File.Delete(filePath);

            book.SaveAs(filePath);
        }
    }
}