namespace KJWebsite.IntegrationTests;

[CollectionDefinition("LogScan", DisableParallelization = true)]
public sealed class LogScanDefinition;

[Collection("LogScan")]
public sealed class PrivacyLogScanTests
{
    [Fact]
    public void T23LastGuardAllFactoriesHaveWrittenNoPrivateValuesToLogs()
    {
        var violations = GlobalLogSink.Default.Violations;
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine,
            violations.Select(v => $"category '{v.Category}', label '{v.Label}', value '{v.Value}'")));
        GlobalLogSink.Default.AssertNone();
    }
}
