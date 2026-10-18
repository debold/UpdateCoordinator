using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Inventory;

namespace UpdateCoordinator.Agent.Tests;

internal sealed class FakeSystemProbe : ISystemProbe
{
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<(string Path, string Name), string> Values { get; } = [];

    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RegistryKeyExists(string path) => Keys.Contains(path);

    public string? GetRegistryValue(string path, string name) =>
        Values.FirstOrDefault(v => string.Equals(v.Key.Path, path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Key.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool ServiceInstalled(string serviceName) => Services.Contains(serviceName);
}

public sealed class RoleDetectorTests
{
    private readonly FakeSystemProbe _probe = new();

    [Fact]
    public void PlainServer_HasNoRoles()
    {
        Assert.Empty(Detect());
    }

    [Fact]
    public void DomainController_IsDetectedByProductType()
    {
        _probe.Values[(@"SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType")] = "LanmanNT";

        Assert.Equal(["DomainController"], Detect());
    }

    [Fact]
    public void MemberServer_IsNotDomainController()
    {
        _probe.Values[(@"SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType")] = "ServerNT";

        Assert.Empty(Detect());
    }

    [Fact]
    public void BuiltInRoles_AreDetectedAndSorted()
    {
        _probe.Services.Add("vmms");
        _probe.Services.Add("ClusSvc");
        _probe.Keys.Add(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
        _probe.Services.Add("MSExchangeIS");

        Assert.Equal(["ClusterNode", "Exchange", "HyperVHost", "SqlServer"], Detect());
    }

    [Fact]
    public void ConfiguredRule_AddsNewRole()
    {
        _probe.Services.Add("VeeamBackupSvc");
        var custom = new RoleRule
        {
            Role = "Custom.BackupServer",
            AnyOf = [new RoleCondition { Kind = RoleConditionKind.ServiceInstalled, Path = "VeeamBackupSvc" }],
        };

        Assert.Equal(["Custom.BackupServer"], Detect(custom));
    }

    [Fact]
    public void ConfiguredRule_ExtendsBuiltInRole()
    {
        _probe.Keys.Add(@"SOFTWARE\Contoso\SqlCluster");
        var extension = new RoleRule
        {
            Role = "sqlserver",
            AnyOf = [new RoleCondition { Kind = RoleConditionKind.RegistryKeyExists, Path = @"SOFTWARE\Contoso\SqlCluster" }],
        };

        Assert.Equal(["SqlServer"], Detect(extension));
    }

    [Theory]
    [InlineData("Primary", true)]
    [InlineData("PRIMARY", true)]
    [InlineData("Secondary", false)]
    public void RegistryValueCondition_ComparesCaseInsensitive(string actual, bool expected)
    {
        _probe.Values[(@"SOFTWARE\Contoso", "Mode")] = actual;
        var rule = new RoleRule
        {
            Role = "Custom.Primary",
            AnyOf = [new RoleCondition { Kind = RoleConditionKind.RegistryValue, Path = @"SOFTWARE\Contoso", ValueName = "Mode", EqualTo = "primary" }],
        };

        Assert.Equal(expected, Detect(rule).Contains("Custom.Primary"));
    }

    [Fact]
    public void RegistryValueCondition_WithoutExpectedValue_ChecksExistence()
    {
        _probe.Values[(@"SOFTWARE\Contoso", "Installed")] = "1";
        var rule = new RoleRule
        {
            Role = "Custom.Contoso",
            AnyOf = [new RoleCondition { Kind = RoleConditionKind.RegistryValue, Path = @"SOFTWARE\Contoso", ValueName = "Installed" }],
        };

        Assert.Equal(["Custom.Contoso"], Detect(rule));
    }

    [Fact]
    public void RulesFromConfiguration_AreBound()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Inventory:RoleRules:0:Role"] = "Custom.PrintServer",
            ["Inventory:RoleRules:0:AnyOf:0:Kind"] = "ServiceInstalled",
            ["Inventory:RoleRules:0:AnyOf:0:Path"] = "Spooler",
        }).Build();
        var options = configuration.GetSection(InventoryOptions.SectionName).Get<InventoryOptions>()!;
        _probe.Services.Add("Spooler");

        Assert.Equal(["Custom.PrintServer"], Detect([.. options.RoleRules]));
    }

    private IReadOnlyList<string> Detect(params RoleRule[] configured) =>
        new RoleDetector(_probe, Options.Create(new InventoryOptions { RoleRules = [.. configured] })).Detect();
}

public sealed class InventoryOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(Validate(new InventoryOptions()));
    }

    public static TheoryData<string, string, string, string?> InvalidRules() => new()
    {
        { "", "ServiceInstalled", "Spooler", null },
        { "1Role", "ServiceInstalled", "Spooler", null },
        { "Role With Space", "ServiceInstalled", "Spooler", null },
        { "Custom.Role", "ServiceInstalled", "", null },
        { "Custom.Role", "ServiceInstalled", @"SYSTEM\Spooler", null },
        { "Custom.Role", "RegistryKeyExists", @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso", null },
        { "Custom.Role", "RegistryKeyExists", @"HKLM\SOFTWARE\Contoso", null },
        { "Custom.Role", "RegistryValue", @"SOFTWARE\Contoso", null },
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void InvalidRule_IsRejected(string role, string kind, string path, string? valueName)
    {
        var options = new InventoryOptions
        {
            RoleRules = [new RoleRule { Role = role, AnyOf = [new RoleCondition { Kind = Enum.Parse<RoleConditionKind>(kind), Path = path, ValueName = valueName }] }],
        };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void RuleWithoutConditions_IsRejected()
    {
        Assert.NotEmpty(Validate(new InventoryOptions { RoleRules = [new RoleRule { Role = "Custom.Empty" }] }));
    }

    [Fact]
    public void OverlongPath_IsRejected()
    {
        var options = new InventoryOptions
        {
            RoleRules = [new RoleRule { Role = "Custom.Long", AnyOf = [new RoleCondition { Kind = RoleConditionKind.RegistryKeyExists, Path = new string('a', 513) }] }],
        };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void UnknownConditionKind_IsRejected()
    {
        var options = new InventoryOptions
        {
            RoleRules = [new RoleRule { Role = "Custom.Kind", AnyOf = [new RoleCondition { Kind = (RoleConditionKind)99, Path = "x" }] }],
        };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void SendIntervalShorterThanCollectInterval_IsRejected()
    {
        Assert.NotEmpty(Validate(new InventoryOptions { CollectInterval = TimeSpan.FromHours(12), MaxSendInterval = TimeSpan.FromHours(2) }));
    }

    [Theory]
    [InlineData("00:01:00")]
    [InlineData("2.00:00:00")]
    public void CollectIntervalOutOfRange_IsRejected(string interval)
    {
        Assert.NotEmpty(Validate(new InventoryOptions { CollectInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture), MaxSendInterval = TimeSpan.FromDays(7) }));
    }

    private static List<ValidationResult> Validate(InventoryOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
