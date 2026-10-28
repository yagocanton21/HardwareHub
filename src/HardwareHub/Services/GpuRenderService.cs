using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace HardwareHub.Services
{
    public class GpuRenderService
    {
        private bool _isRunning;
        private readonly Stopwatch _stopwatch = new();
        private int _frameCount = 0;
        private long _lastFpsTick = 0;

        public bool IsRunning => _isRunning;
        public int CurrentFps { get; private set; }
        public TimeSpan ElapsedTime => _stopwatch.Elapsed;

        public event Action? StateChanged;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _frameCount = 0;
            _lastFpsTick = Stopwatch.GetTimestamp();
            _stopwatch.Restart();
            CompositionTarget.Rendering += OnRendering;
            StateChanged?.Invoke();
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            CompositionTarget.Rendering -= OnRendering;
            _stopwatch.Stop();
            CurrentFps = 0;
            StateChanged?.Invoke();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isRunning) return;

            _frameCount++;
            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (double)(now - _lastFpsTick) / Stopwatch.Frequency;

            if (elapsedSec >= 1.0)
            {
                CurrentFps = (int)Math.Round(_frameCount / elapsedSec);
                _frameCount = 0;
                _lastFpsTick = now;
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

                    // Vetor normal
                    var normal = new Vector3D(cosV * cosU, cosV * sinU, sinV);
                    normal.Normalize();
                    mesh.Normals.Add(normal);

                    // Coordenadas de textura
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
