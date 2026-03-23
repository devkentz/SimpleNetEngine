using DFrame;
using DFrame.Controller;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StressTest.Controller;

namespace StressTest.Controller;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var controllerPort = config.GetValue("DFrame:ControllerPort", 7312);
        var workerConnectPort = config.GetValue("DFrame:WorkerPort", 7313);

        Console.WriteLine($"DFrame Controller starting on :{controllerPort} (worker connect: :{workerConnectPort})");

        var builder = DFrameApp.CreateBuilder(controllerPort, workerConnectPort);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IExecutionResultHistoryProvider>(
                new JsonFileResultHistoryProvider("results"));
        });

        await builder.RunControllerAsync();
    }
}
