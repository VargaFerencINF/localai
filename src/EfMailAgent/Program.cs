using System.Text;
using EfMailAgent.Cli;

// Hungarian text must survive the Windows console (default code page 852/1250).
if (OperatingSystem.IsWindows())
    Console.OutputEncoding = Encoding.UTF8;

return await CliApp.RunAsync(args);
