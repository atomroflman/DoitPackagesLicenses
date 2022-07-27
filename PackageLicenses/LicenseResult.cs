namespace PackageLicenses
{
    public class LicenseResult
    {
        public string Project { get; set; }
        public string Package { get; set; }
        public string Version { get; set; }
        public string Licence { get; set; }
        public string LicenceUrl { get; set; }
        public string IgnoredReason { get; set; }

        public override string ToString() => $"{Project}->{Package} ({Version}) with '{Licence}' from '{LicenceUrl}'{(IgnoredReason != null ? $" Ignored because {IgnoredReason}" : "")}";
    }
}
