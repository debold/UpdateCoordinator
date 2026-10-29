using SUSyphos.Agent;

if (args.Contains(AgentCleanup.Argument, StringComparer.OrdinalIgnoreCase))
{
    using var host = AgentHostBuilder.Create([]).Build();
    return AgentCleanup.Run(host.Services);
}

await AgentHostBuilder.Create(args).Build().RunAsync();
return 0;
