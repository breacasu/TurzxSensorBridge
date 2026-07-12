using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SensorService
{
    class Program
    {
        private const string MutexName = @"Global\TurzxSensorServiceActive";

        static async Task Main(string[] args)
        {
            using var singleInstanceMutex = new Mutex(initiallyOwned: false, MutexName);
            if (!singleInstanceMutex.WaitOne(TimeSpan.Zero, false))
            {
                Console.WriteLine($"ERROR: Another instance of SensorService is already running.");
                Console.WriteLine($"       Mutex '{MutexName}' is held by another process.");
                return;
            }

            Console.WriteLine("TurzxSensorService starting...");

            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TurzxSensorBridge");
            string configPath = Path.Combine(configDir, "selected_sensors.json");

            Directory.CreateDirectory(configDir);

            var pollingLoop = new SensorPollingLoop(configPath);
            pollingLoop.Start();

            using var cts = new CancellationTokenSource();
            var pipeServer = new PipeServer(pollingLoop);
            pipeServer.Start(cts.Token);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service ready. Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Service stopped.");
            }
            finally
            {
                pipeServer.Stop();
            }
        }
    }
}
