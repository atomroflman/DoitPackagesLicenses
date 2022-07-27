using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PackageLicenses
{
    public class NodeJsLicenseReader : ILicenseReader
    {
        private static Regex projectReg = new Regex(@"(?<project>[└├]─\s*((@(?<name>[a-z0-9-/]+))|((?<name>[a-z0-9-/]+)@(?<version>[0-9\.a-z-]+))))|(?<param>(│  |   )├─\s*(?<name>(licenses|repository|publisher|email|url|path|licenseFile)): (?<value>.*))", RegexOptions.Compiled);

        public bool LicenseToolInstalled { get; set; }
        public IEnumerable<LicenseResult> ReadLicenses(string projectPath)
        {
            if (!LicenseToolInstalled)
            {
                var instProc = new Process();
                instProc.StartInfo = new ProcessStartInfo("CMD.exe", $"/c npm install -g license-checker");
                instProc.StartInfo.RedirectStandardOutput = true;
                instProc.StartInfo.UseShellExecute = false;
                instProc.Start();

                Console.WriteLine("Installing license-checker...");
                foreach (var output in instProc.ReadOutputStream(instProc.StandardOutput).ToList())
                {
                    Console.WriteLine(output);
                }
                LicenseToolInstalled = true;
            }
            var licProc = new Process();
            licProc.StartInfo = new ProcessStartInfo("CMD.exe", $"/c license-checker");
            licProc.StartInfo.RedirectStandardOutput = true;
            licProc.StartInfo.RedirectStandardError = true;
            licProc.StartInfo.UseShellExecute = false;
            licProc.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            licProc.StartInfo.WorkingDirectory = Path.GetDirectoryName(projectPath);
            licProc.Start();

            LicenseResult current = null;
            var lines = licProc.ReadOutputStream(licProc.StandardOutput)
                .Select(e => projectReg.Match(e));

            foreach (var line in lines)
            {
                if (line.Groups["project"].Success)
                {
                    if (current != null)
                        yield return current;
                    current = new LicenseResult()
                    {
                        Project = licProc.StartInfo.WorkingDirectory,
                        Package = line.Groups["name"].Value,
                        Version = line.Groups["version"].Value
                    };
                }
                else if (line.Groups["param"].Success)
                {
                    if (current == null)
                    {
                        Console.WriteLine("Ignore param.");
                        continue;
                    }
                    switch (line.Groups["name"].Value)
                    {
                        case "licenses":
                            current.Licence = line.Groups["value"].Value;
                            break;
                        case "path":
                            current.LicenceUrl = line.Groups["value"].Value;
                            break;
                    }
                }
            }
        }
    }
}
