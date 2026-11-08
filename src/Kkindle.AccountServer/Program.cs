using Kkindle.AccountServer;

await using var application = await AccountServerHost.BuildAsync(args);
await application.RunAsync();
