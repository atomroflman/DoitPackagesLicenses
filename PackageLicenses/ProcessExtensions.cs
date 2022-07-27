using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PackageLicenses
{
    public static class ProcessExtensions
    {
        public static IEnumerable<string> ReadOutputStream(this Process proc, StreamReader reader)
        {
            var buff = string.Empty;
            while ((buff = reader.ReadLine()) != null || !proc.HasExited)
            {
                if (buff == null)
                {
                    Thread.Sleep(10);
                    continue;
                }
                yield return buff;
            }
        }
    }
}
