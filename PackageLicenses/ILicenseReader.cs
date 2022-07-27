using System.Collections.Generic;

namespace PackageLicenses
{
    public interface ILicenseReader
    {
        IEnumerable<LicenseResult> ReadLicenses(string projectPath);
    }
}
