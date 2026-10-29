#:property TargetFramework=net10.0
#:property PublishAot=false

// Tenant-free report parser and CSV checks, using the production sources.
// dotnet run --file tests/DeviceStatusUnitTests.cs -c Release
internal static partial class DeviceStatusTests
{
    public static int Main()
    {
        foreach (var (name, check) in PortableChecks)
            Run(name, () => check().GetAwaiter().GetResult());
        return ReportResult();
    }
}
