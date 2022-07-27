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
    internal class Program
    {
        private const int ERROR_PACKAGE_PATH = 0xA0;
        private const int ERROR_ARGUMENTS = 0xA1;
        private static IConfigurationRoot _configuration = null;
        private static string _outputPath = "";

        private static void Main(string[] args)
        {
            var commandLineArgs = new Tuple<string, string, string>[]
            {
                new Tuple<string, string, string>("-f", "folder", null),
                new Tuple<string, string, string>("-h", "help", ":true"),
                new Tuple<string, string, string>("-ip", "includeproject", ":true"),
                new Tuple<string, string, string>("-j", "json", ":true"),
                new Tuple<string, string, string>("-o", "out", null),
                new Tuple<string, string, string>("-p", "project", null),
                new Tuple<string, string, string>("-r", "recursive", ":true"),
            };

            var builder = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
              .AddCommandLine(configureSource =>
              {
                  configureSource.SwitchMappings = commandLineArgs.ToDictionary(v => v.Item1, v => v.Item2);
                  configureSource.Args = args.Select(a => a.Contains(":") ? a : $"{a}{commandLineArgs.FirstOrDefault(p => p.Item1 == a || p.Item2 == a)?.Item3}");
              });

            string env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (string.IsNullOrWhiteSpace(env))
            {
                env = "Development";
            }

            if (env == "Development")
            {
                builder.AddUserSecrets<Program>();
                Console.WriteLine("Running in DEV environment.");
            }
            _configuration = builder.Build();

            if (_configuration.GetSection("help").Get<bool>())
            {
                using var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream(typeof(Program), "Mat.txt"));
                Console.WriteLine(reader.ReadToEnd());
                return;
            }

            if (_configuration.GetSection("folder").Get<string>() == null && _configuration.GetSection("project").Get<string>() == null)
            {
                Console.WriteLine("You need to specify 'folder' (-f) or 'project' (-p).");
                Environment.ExitCode = ERROR_ARGUMENTS;
                return;
            }

            if (_configuration.GetSection("folder").Get<string>() != null && _configuration.GetSection("project").Get<string>() != null)
            {
                Console.WriteLine("You can only specify 'folder' (-f) or 'project' (-p).");
                Environment.ExitCode = ERROR_ARGUMENTS;
                return;
            }

            var projectPath = _configuration.GetSection("project").Get<string>();
            var recursive = _configuration.GetSection("recursive").Get<bool?>() ?? false;
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

            var path = _configuration["Path"];

            if (!Directory.Exists(path))
            {
                Console.Write("Path not Found: " + path);
                Environment.ExitCode = ERROR_PACKAGE_PATH;
                return;
            }

            _outputPath = _configuration["OutputPath"];
            // GitHub Client ID and Client Secret
            const string LicenseUtilityClientId = "LicenseUtility.ClientId";
            // Add user-secretes with command line:  dotnet user-secrets set LicenseUtility.ClientId XXXX-XX-ID...
            Console.WriteLine($"The Secret Id is {_configuration[LicenseUtilityClientId]}");

            LicenseUtility.ClientId = _configuration[LicenseUtilityClientId];
            const string LicenseUtilityClientSecret = "LicenseUtility.ClientSecret";
            // Add user-secretes with command line:  dotnet user-secrets set LicenseUtility.ClientSecret XYZ...
            LicenseUtility.ClientSecret = _configuration[LicenseUtilityClientSecret];
            Console.WriteLine($"The Client secret is {_configuration[LicenseUtilityClientId]}");

            var projects = _configuration.GetSection("folder").Get<string>() != null
                ? new DirectoryInfo(_configuration.GetSection("folder").Get<string>()).GetFiles("*.csproj", new EnumerationOptions() { RecurseSubdirectories = true })
                    .Concat(new DirectoryInfo(_configuration.GetSection("folder").Get<string>()).GetFiles("package.json", new EnumerationOptions() { RecurseSubdirectories = true }))
                    .Select(p => p.FullName)
                : new[] { Path.IsPathFullyQualified(_configuration.GetSection("project").Get<string>())
                    ? _configuration.GetSection("project").Get<string>()
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _configuration.GetSection("project").Get<string>())};

            var packageFolders = new[] { PackageLicensesUtility.GetPackages(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".nuget\packages"), new Logger()) }
                .Concat(projects.Where(p => p.EndsWith(".csproj", StringComparison.InvariantCultureIgnoreCase)).Select(p => PackageLicensesUtility.GetPackages(Path.Combine(Path.GetDirectoryName(p), "packages"))))
                .SelectMany(p => p)
                .ToList();

            var typeMap = new[]
            {
                new { nameReg = new Regex(@".*\.csproj"), type = (ILicenseReader)new CsprojLicenseReader(packageFolders) },
                new { nameReg = new Regex(@"^(?:(?!node_modules).)+\\package.json$"), type = (ILicenseReader)new NodeJsLicenseReader() },
            };
            IEnumerable<LicenseResult> foundVersions = new LicenseResult[0];

            foreach (var project in projects)
            {
                Console.WriteLine($"Reading project dependencies for: {project}");
                var reader = typeMap.FirstOrDefault(m => m.nameReg.IsMatch(project));
                if (reader == null)
                    continue;
                foundVersions = foundVersions.Concat(reader.type.ReadLicenses(project));
            }

            try
            {
                var remainingPackages = foundVersions.Where(v => !v.Project.ToLower().Contains("test") && v.IgnoredReason == null).ToList();

                var printedVersions =
                    includeProject
                        ? remainingPackages
                            .GroupBy(v => new { v.Project, v.Package, v.Version })
                            .Select(v => new LicenseResult()
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
                            .Select(v => new LicenseResult()
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

        private static void CreateWorkbook(List<LicenseResult> list, bool includeProject)
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