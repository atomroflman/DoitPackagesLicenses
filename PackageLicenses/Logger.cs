using NuGet.Common;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace PackageLicenses
{
    public class Logger : ILogger
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
}
