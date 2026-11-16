using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

/// <summary>The architecture from a program's name (lab, 2026-11-16: the x86 .NET runtime was installed beside the x64 one to update).</summary>
public sealed class SoftwareArchitectureTests
{
    [Theory]
    [InlineData("Microsoft Windows Desktop Runtime 10.0.12 (x64)", "x64")]
    [InlineData("Microsoft Windows Desktop Runtime 10.0.15 (x86)", "x86")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.44.35211", "x64")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x86)", "x86")]
    [InlineData("7-Zip 26.06 (x64 edition)", "x64")]
    [InlineData("Notepad++ (64-bit x64)", "x64")]
    [InlineData("Git version 2.50.0 (ARM64)", "arm64")]
    [InlineData("VMware Tools", null)]
    [InlineData("Paint.NET 5.1", null)]
    [InlineData(null, null)]
    public void Name_TellsTheArchitecture(string? name, string? expected) =>
        Assert.Equal(expected, SoftwareArchitecture.FromName(name));
}
