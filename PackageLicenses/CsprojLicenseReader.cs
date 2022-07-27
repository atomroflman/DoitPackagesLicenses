using NuGet.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PackageLicenses
{

    public class CsprojLicenseReader : ILicenseReader
    {
        private static Regex reg = new Regex(@"\'(?<name>.*)\'", RegexOptions.Compiled);
        private static Regex entryReg = new Regex(@"> ?(?<name>[^\s]+)\s+(?<version>[0-9a-zA-Z\.-_]+)", RegexOptions.Compiled);
        private IEnumerable<LocalPackageInfo> _localPackages;

        public CsprojLicenseReader(IEnumerable<LocalPackageInfo> localPackages)
        {
            _localPackages = localPackages;
        }

        private static IEnumerable<LicenseResult> ReadAllLines(StreamReader reader, Process proc)
        {
            string proj = string.Empty;
            foreach (var buff in proc.ReadOutputStream(reader)) 
            { 
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
                        yield return new LicenseResult() { Project = proj, Package = entryMatch.Groups["name"].Value, Version = entryMatch.Groups["version"].Value };
                }
            }
        }

        public IEnumerable<LicenseResult> ReadLicenses(string projectPath)
        {
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

            var log = new Logger();

            var packages = _localPackages;
            var t = Task.Run(async () =>
            {
                foreach (var p in packages)
                {
                    if (!checkList.TryGetValue(p.Identity.Id, out Dictionary<string, List<LicenseResult>> usedPackageVersions))
                        continue;
                    if (!usedPackageVersions.TryGetValue(p.Identity.Version.ToString(), out List<LicenseResult> packageProjects))
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
            return checkList
                .SelectMany(e => e.Value)
                .SelectMany(e => e.Value);
        }
    }
}
