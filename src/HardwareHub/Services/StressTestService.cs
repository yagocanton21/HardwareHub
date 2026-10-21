using System.Diagnostics;

namespace HardwareHub.Services
{
    public class StressTestService
    {
        private CancellationTokenSource? _cpuCts;
        private readonly Stopwatch _cpuStopwatch = new();

        public bool IsCpuStressRunning { get; private set; }
        public bool IsCpuBenchRunning { get; private set; }
        public bool IsGpuRenderRunning { get; private set; }

        public double SingleCoreScore { get; private set; }
        public double MultiCoreScore { get; private set; }
        public double MultiThreadRatio => SingleCoreScore > 0 ? Math.Round(MultiCoreScore / SingleCoreScore, 2) : 0;
        public double MaxObservedTemp { get; private set; }
        public double StartTemp { get; private set; }
        public TimeSpan ElapsedTime => _cpuStopwatch.Elapsed;

        public event Action? StateChanged;

        public async Task StartCpuStressAsync(Func<double> getCurrentCpuTemp, Action<string>? log = null)
        {
            if (IsCpuStressRunning || IsCpuBenchRunning) return;

            IsCpuStressRunning = true;
            _cpuCts = new CancellationTokenSource();
            _cpuStopwatch.Restart();

            double currentTemp = getCurrentCpuTemp();
            StartTemp = currentTemp;
            MaxObservedTemp = currentTemp;
            StateChanged?.Invoke();

            log?.Invoke($"[ESTRESSE CPU] Iniciado com {Environment.ProcessorCount} threads simultâneas a 100% de carga.");

            var token = _cpuCts.Token;
            int threadCount = Environment.ProcessorCount;

            // Inicia workers multi-thread em paralelo
            var tasks = new Task[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                tasks[i] = Task.Factory.StartNew(() => RunStressLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            // Monitora temperatura e tempo em background
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    double t = getCurrentCpuTemp();
                    if (t > MaxObservedTemp) MaxObservedTemp = t;
                    StateChanged?.Invoke();
                    await Task.Delay(500, CancellationToken.None);
                }
            }, CancellationToken.None);

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                IsCpuStressRunning = false;
                _cpuStopwatch.Stop();
                StateChanged?.Invoke();
                log?.Invoke($"[ESTRESSE CPU] Finalizado. Pico térmico atingido: {MaxObservedTemp:F0} °C (Tempo: {ElapsedTime:mm\\:ss}).");
            }
        }

        public async Task StartCpuBenchmarkAsync(Func<double> getCurrentCpuTemp, Action<string>? log = null)
        {
            if (IsCpuStressRunning || IsCpuBenchRunning) return;

            IsCpuBenchRunning = true;
            _cpuCts = new CancellationTokenSource();
            _cpuStopwatch.Restart();

            double currentTemp = getCurrentCpuTemp();
            StartTemp = currentTemp;
            MaxObservedTemp = currentTemp;
            SingleCoreScore = 0;
            MultiCoreScore = 0;
            StateChanged?.Invoke();

            log?.Invoke("[BENCHMARK CPU] Iniciando teste de desempenho Single-Core (5s)...");

            var token = _cpuCts.Token;

            try
            {
                // Etapa 1: Single-Thread (5 segundos) calibrado no padrão CPU-Z 17.01.64
                long singleOps = await Task.Run(() => RunTimedBenchmark(1, TimeSpan.FromSeconds(5), token), token);
                SingleCoreScore = Math.Round((double)singleOps / 347368.0, 1);
                StateChanged?.Invoke();
                log?.Invoke($"[BENCHMARK CPU] Single-Core concluído: {SingleCoreScore:F1} pts. Iniciando Multi-Core (5s)...");

                if (token.IsCancellationRequested) return;

                // Etapa 2: Multi-Thread (5 segundos em todos os núcleos) calibrado no padrão CPU-Z 17.01.64
                int threadCount = Environment.ProcessorCount;
                long multiOps = await Task.Run(() => RunTimedBenchmark(threadCount, TimeSpan.FromSeconds(5), token), token);
                MultiCoreScore = Math.Round((double)multiOps / 314457.0, 1);
                StateChanged?.Invoke();
                log?.Invoke($"[BENCHMARK CPU] Multi-Core concluído: {MultiCoreScore:F1} pts (Razão Multi-Thread: {MultiThreadRatio:F2}x). Pico térmico: {MaxObservedTemp:F0} °C.");
            }
            catch (OperationCanceledException)
            {
                log?.Invoke("[BENCHMARK CPU] Teste cancelado pelo usuário.");
            }
            finally
            {
                IsCpuBenchRunning = false;
                _cpuStopwatch.Stop();
                StateChanged?.Invoke();
            }
        }

        public void StopCpuTest()
        {
            if (_cpuCts != null && !_cpuCts.IsCancellationRequested)
            {
                _cpuCts.Cancel();
            }
            IsCpuStressRunning = false;
            IsCpuBenchRunning = false;
            _cpuStopwatch.Stop();
            StateChanged?.Invoke();
        }

        private static void RunStressLoop(CancellationToken token)
        {
            // Laço de estresse de alta densidade matemática (FPU + ALU)
            double x = 1.0000001;
            long iter = 0;
            while (!token.IsCancellationRequested)
            {
                for (int i = 0; i < 50000; i++)
                {
                    x = Math.Sin(x) * Math.Cos(x) + Math.Sqrt(Math.Abs(x)) + (iter & 0xFF);
                    iter++;
                }
            }
        }

        private static long RunTimedBenchmark(int threads, TimeSpan duration, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            long totalOps = 0;

            Parallel.For(0, threads, new ParallelOptions { CancellationToken = token }, _ =>
            {
                double x = 1.0000001;
                long localOps = 0;
                long iter = 0;

                while (sw.Elapsed < duration && !token.IsCancellationRequested)
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        x = Math.Sin(x) * Math.Cos(x) + Math.Sqrt(Math.Abs(x)) + (iter & 0xFF);
                        iter++;
                    }
                    localOps += 10000;
                }

                Interlocked.Add(ref totalOps, localOps);
            });

            return totalOps;
        }
    }
}
