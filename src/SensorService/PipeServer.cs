using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SensorService
{
    public class PipeServer
    {
        private const string PipeName = "TurzxSensorBridge";
        private readonly SensorPollingLoop _pollingLoop;
        private CancellationTokenSource? _cts;

        public PipeServer(SensorPollingLoop pollingLoop)
        {
            _pollingLoop = pollingLoop;
        }

        public void Start(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task.Run(() => RunServerLoopAsync(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private async Task RunServerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for client connection...");
                    await server.WaitForConnectionAsync(ct);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Client connected");

                    while (!ct.IsCancellationRequested && server.IsConnected)
                    {
                        try
                        {
                            string json = _pollingLoop.BuildCurrentSensorJson() + "\n";
                            byte[] buffer = Encoding.UTF8.GetBytes(json);
                            await server.WriteAsync(buffer, 0, buffer.Length, ct);
                            await server.FlushAsync(ct);
                            await Task.Delay(1000, ct);
                        }
                        catch (IOException)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Client disconnected");
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Pipe error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }
    }
}
