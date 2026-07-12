using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PatchModule
{
    public class PipeClient : IDisposable
    {
        private const string PipeName = "TurzxSensorBridge";
        private NamedPipeClientStream? _pipeClient;
        private CancellationTokenSource? _cts;
        private Thread? _workerThread;

        public event Action<string>? OnMessageReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public bool IsConnected => _pipeClient?.IsConnected ?? false;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _workerThread = new Thread(() => RunAsync(_cts.Token))
            {
                IsBackground = true,
                Name = "PipeClientWorker"
            };
            _workerThread.Start();
        }

        public void Stop()
        {
            _cts?.Cancel();
            _workerThread?.Join(3000);
            Dispose();
        }

        private void RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);

                    Console.WriteLine("[TurzxSensorBridge] Connecting to SensorService...");
                    client.Connect(2000);
                    _pipeClient = client;
                    Console.WriteLine("[TurzxSensorBridge] Connected to SensorService");
                    OnConnected?.Invoke();

                    using var reader = new StreamReader(client, Encoding.UTF8);

                    while (!ct.IsCancellationRequested && client.IsConnected)
                    {
                        string? line = reader.ReadLine();
                        if (line == null) break;

                        OnMessageReceived?.Invoke(line);
                    }
                }
                catch (TimeoutException)
                {
                    // No service available yet, retry
                }
                catch (IOException)
                {
                    Console.WriteLine("[TurzxSensorBridge] Pipe disconnected");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TurzxSensorBridge] Pipe error: {ex.Message}");
                }
                finally
                {
                    _pipeClient = null;
                    OnDisconnected?.Invoke();
                }

                if (!ct.IsCancellationRequested)
                {
                    Thread.Sleep(5000);
                }
            }
        }

        public void Dispose()
        {
            _pipeClient?.Dispose();
            _pipeClient = null;
        }
    }
}
