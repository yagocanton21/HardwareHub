using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Vortice.Mathematics;

namespace HardwareHub.Services
{
    public class GpuRenderService
    {
        private volatile bool _isRunning;
        private CancellationTokenSource? _cts;
        private readonly Stopwatch _stopwatch = new();
        
        public bool IsRunning => _isRunning;
        public int CurrentFps { get; private set; }
        public TimeSpan ElapsedTime => _stopwatch.Elapsed;
        public long TotalFramesRendered { get; private set; }
        public string ActiveGpuName { get; private set; } = "";

        public event Action? StateChanged;

        public void Start(Action<string>? log = null)
        {
            if (_isRunning) return;

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _stopwatch.Restart();
            CurrentFps = 0;
            TotalFramesRendered = 0;
            StateChanged?.Invoke();

            var token = _cts.Token;
            Task.Factory.StartNew(() => RunDirect3DStressLoop(token, log), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Stop(Action<string>? log = null)
        {
            if (!_isRunning) return;

            _isRunning = false;
            try
            {
                _cts?.Cancel();
            }
            catch { }

            _stopwatch.Stop();
            CurrentFps = 0;
            StateChanged?.Invoke();

            if (log != null && TotalFramesRendered > 0)
            {
                double totalSec = Math.Max(0.1, _stopwatch.Elapsed.TotalSeconds);
                double avgFps = Math.Round(TotalFramesRendered / totalSec, 1);
                log($"[GPU 3D RENDER] Teste finalizado. Total: {TotalFramesRendered:N0} frames renderizados em {_stopwatch.Elapsed:mm\\:ss} (Média: {avgFps} FPS).");
            }
        }

        private void RunDirect3DStressLoop(CancellationToken token, Action<string>? log)
        {
            try
            {
                using var dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                IDXGIAdapter1? bestAdapter = null;
                long maxVram = -1;

                // Prioriza adaptador com maior memória de vídeo dedicada (GPU dedicada NVIDIA / AMD)
                for (uint i = 0; ; i++)
                {
                    var res = dxgiFactory.EnumAdapters1(i, out var adapter);
                    if (res.Failure || adapter == null) break;

                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0) continue;

                    if ((long)desc.DedicatedVideoMemory > maxVram)
                    {
                        maxVram = (long)desc.DedicatedVideoMemory;
                        bestAdapter = adapter;
                    }
                }

                ActiveGpuName = bestAdapter?.Description1.Description ?? "GPU Primária";
                log?.Invoke($"[GPU 3D RENDER] Iniciando estresse Direct3D 11 em: {ActiveGpuName} (VRAM: {maxVram / (1024 * 1024)} MB).");

                var featureLevels = new[]
                {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                };

                var creationFlags = DeviceCreationFlags.BgraSupport;
                var createResult = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    bestAdapter,
                    bestAdapter == null ? DriverType.Hardware : DriverType.Unknown,
                    creationFlags,
                    featureLevels,
                    out var device,
                    out var featureLevel,
                    out var context
                );

                // Fallback 1: tenta criar com adaptador padrão do sistema
                if (createResult.Failure || device == null || context == null)
                {
                    createResult = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Hardware,
                        creationFlags,
                        featureLevels,
                        out device,
                        out featureLevel,
                        out context
                    );
                }

                // Fallback 2: rasterizador WARP oficial da Microsoft (garante execução mesmo sem GPU dedicada)
                if (createResult.Failure || device == null || context == null)
                {
                    createResult = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Warp,
                        creationFlags,
                        featureLevels,
                        out device,
                        out featureLevel,
                        out context
                    );
                }

                if (createResult.Failure || device == null || context == null)
                {
                    log?.Invoke($"[GPU 3D RENDER] Falha ao criar dispositivo Direct3D 11 ({createResult.Code}).");
                    Stop();
                    return;
                }

                using (device)
                using (context)
                {
                    // Resolução do Render Target: 1920x1080 para dedicadas ou 1280x720 para integradas
                    uint width = maxVram > 512 * 1024 * 1024 ? 1920u : 1280u;
                    uint height = maxVram > 512 * 1024 * 1024 ? 1080u : 720u;

                    var texDesc = new Texture2DDescription
                    {
                        Width = width,
                        Height = height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.R8G8B8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget
                    };

                    using var renderTexture = device.CreateTexture2D(texDesc);
                    using var rtv = device.CreateRenderTargetView(renderTexture);

                    // Vertex Shader tela cheia com 1 triângulo gigante
                    string vsCode = @"
struct VSOut {
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};
VSOut VSMain(uint id : SV_VertexID) {
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
";

                    // Pixel Shader: Raymarching pesado com iluminação dinâmica para estressar CUs/SMs da GPU
                    string psCode = @"
cbuffer Constants : register(b0) {
    float time;
    float resX;
    float resY;
    float padding;
};

struct VSOut {
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

float map(float3 p) {
    float d = length(p) - 1.1;
    [unroll(8)]
    for (int i = 1; i <= 6; i++) {
        float fi = float(i);
        d += sin(p.x * 2.5 * fi + time * 1.5) * sin(p.y * 2.5 * fi + time) * sin(p.z * 2.5 * fi) * (0.22 / fi);
    }
    return d;
}

float4 PSMain(VSOut input) : SV_Target {
    float2 uv = (input.uv - 0.5) * float2(resX / resY, 1.0);
    float3 ro = float3(0.0, 0.0, -2.8);
    float3 rd = normalize(float3(uv, 1.1));
    
    float t = 0.0;
    float3 p = ro;
    float hit = 0.0;
    
    [loop]
    for (int i = 0; i < 72; i++) {
        p = ro + rd * t;
        float d = map(p);
        if (d < 0.002) {
            hit = 1.0 - float(i) / 72.0;
            break;
        }
        t += d * 0.45;
        if (t > 8.0) break;
    }
    
    float3 col = float3(hit, hit * 0.6 + sin(time)*0.2, 1.0 - hit);
    return float4(col, 1.0);
}
";

                    Compiler.Compile(vsCode, "VSMain", "vs", "vs_4_0", out var vsBlob, out _).CheckError();
                    Compiler.Compile(psCode, "PSMain", "ps", "ps_4_0", out var psBlob, out _).CheckError();

                    using (vsBlob)
                    using (psBlob)
                    {
                        using var vs = device.CreateVertexShader(vsBlob!.AsBytes());
                        using var ps = device.CreatePixelShader(psBlob!.AsBytes());

                        using var cbuffer = device.CreateBuffer(new BufferDescription
                        {
                            ByteWidth = 16,
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.ConstantBuffer
                        });

                        using var query = device.CreateQuery(new QueryDescription(QueryType.Event));

                        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        context.VSSetShader(vs);
                        context.PSSetShader(ps);
                        context.PSSetConstantBuffer(0, cbuffer);
                        context.RSSetViewport(new Viewport(0, 0, width, height));
                        context.OMSetRenderTargets(rtv);

                        int frameCount = 0;
                        long lastFpsTick = Stopwatch.GetTimestamp();
                        int lastFrameCount = 0;
                        float[] cbData = new float[4] { 0, width, height, 0 };

                        log?.Invoke($"[GPU 3D RENDER] Pipeline Direct3D 11 engajado com sucesso ({width}x{height}, Raymarching 72 steps). Carga ativa!");

                        while (!token.IsCancellationRequested && _isRunning)
                        {
                            cbData[0] = (float)_stopwatch.Elapsed.TotalSeconds;
                            context.UpdateSubresource(cbData, cbuffer);
                            context.Draw(3, 0);

                            // Sincronização de hardware: garante que a GPU processe a fila
                            context.End(query);
                            context.Flush();

                            while (!token.IsCancellationRequested && _isRunning && context.GetData(query, out int _) == false)
                            {
                                Thread.SpinWait(10);
                            }

                            frameCount++;
                            TotalFramesRendered = frameCount;

                            long now = Stopwatch.GetTimestamp();
                            double elapsedSec = (double)(now - lastFpsTick) / Stopwatch.Frequency;
                            if (elapsedSec >= 1.0)
                            {
                                CurrentFps = (int)Math.Round((frameCount - lastFrameCount) / elapsedSec);
                                lastFrameCount = frameCount;
                                lastFpsTick = now;
                                StateChanged?.Invoke();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    log?.Invoke($"[GPU 3D RENDER] Exceção durante teste: {ex.Message}");
                }
            }
            finally
            {
                _isRunning = false;
                CurrentFps = 0;
                StateChanged?.Invoke();
            }
        }

        public static MeshGeometry3D CreateTorusMesh(double majorRadius = 2.0, double minorRadius = 0.75, int majorSegments = 48, int minorSegments = 32)
        {
            var mesh = new MeshGeometry3D();

            for (int i = 0; i <= majorSegments; i++)
            {
                double u = (double)i / majorSegments * 2.0 * Math.PI;
                double cosU = Math.Cos(u);
                double sinU = Math.Sin(u);

                for (int j = 0; j <= minorSegments; j++)
                {
                    double v = (double)j / minorSegments * 2.0 * Math.PI;
                    double cosV = Math.Cos(v);
                    double sinV = Math.Sin(v);

                    double x = (majorRadius + minorRadius * cosV) * cosU;
                    double y = (majorRadius + minorRadius * cosV) * sinU;
                    double z = minorRadius * sinV;

                    mesh.Positions.Add(new Point3D(x, y, z));

                    var normal = new Vector3D(cosV * cosU, cosV * sinU, sinV);
                    normal.Normalize();
                    mesh.Normals.Add(normal);

                    mesh.TextureCoordinates.Add(new System.Windows.Point((double)i / majorSegments, (double)j / minorSegments));
                }
            }

            for (int i = 0; i < majorSegments; i++)
            {
                for (int j = 0; j < minorSegments; j++)
                {
                    int first = i * (minorSegments + 1) + j;
                    int second = first + minorSegments + 1;

                    mesh.TriangleIndices.Add(first);
                    mesh.TriangleIndices.Add(second);
                    mesh.TriangleIndices.Add(first + 1);

                    mesh.TriangleIndices.Add(second);
                    mesh.TriangleIndices.Add(second + 1);
                    mesh.TriangleIndices.Add(first + 1);
                }
            }

            mesh.Freeze();
            return mesh;
        }
    }
}
