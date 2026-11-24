using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PenguLOD.Core.Pipeline;
using PenguLOD.Textures.Bc;

namespace PenguLOD.Textures;

public sealed record ComputeBenchmark(bool Available, string Device, bool Software, bool QualityPassed,
    double CpuMilliseconds, double VulkanMilliseconds, double MeanSquaredDifference, string Recommendation);

/// <summary>Multi-slot strip-batched Vulkan BC7 acceleration for opaque and alpha textures up to 8192x8192, with automatic CPU overflow/fallback.</summary>
public sealed class TextureCompute : IDisposable
{
    private static readonly AsyncLocal<TextureCompute?> Active = new();
    private readonly TextureCompute? _previous;
    private readonly object _initGate = new();
    private readonly IProgress<string>? _progress;
    private readonly string _mode;
    private IntPtr _context;
    private volatile bool _attempted, _enabled, _disposed;
    private int _inFlight;
    private long _gpuImages, _cpuImages;
    public ComputeBenchmark? Measurement { get; private set; }

    private TextureCompute(string mode, IProgress<string>? progress)
    {
        ValidateMode(mode);
        _mode = mode;
        _progress = progress;
        _previous = Active.Value;
        Active.Value = this;
    }

    public static TextureCompute Enter(string mode = "Auto", IProgress<string>? progress = null) => new(mode, progress);

    public static void ValidateMode(string mode)
    {
        if (mode is not ("Cpu" or "Auto" or "Vulkan"))
            throw new ArgumentException("Texture compute must be Cpu, Auto or Vulkan.");
    }

    internal static bool TryEncode(byte[] rgba, int width, int height, out byte[] output)
    {
        output = [];
        var active = Active.Value;
        if (active is null || active._mode == "Cpu" || active._disposed) return false;
        if (width * height < 128 * 128 || width > 8192 || height > 8192)
        {
            Interlocked.Increment(ref active._cpuImages);
            return false;
        }
        if (!active._attempted)
        {
            lock (active._initGate)
            {
                if (active._disposed) return false;
                if (!active._attempted)
                {
                    active.Measurement = Measure(allowSoftware: false, out active._context);
                    active._enabled = active.Measurement.QualityPassed
                        && (active._mode == "Vulkan" || active.Measurement.VulkanMilliseconds < active.Measurement.CpuMilliseconds * 0.85);
                    active._progress?.Report("Texture compute: " + active.Measurement.Device + "; " + active.Measurement.Recommendation +
                        (active._enabled ? " Using multi-slot Vulkan for BC7 textures." : " Using CPU."));
                    active._attempted = true;
                }
            }
        }
        if (!active._enabled)
        {
            Interlocked.Increment(ref active._cpuImages);
            return false;
        }

        RunLimits.Check();
        Interlocked.Increment(ref active._inFlight);
        try
        {
            if (active._disposed || active._context == IntPtr.Zero)
            {
                Interlocked.Increment(ref active._cpuImages);
                return false;
            }
            var message = new StringBuilder(1024);
            var buf = new byte[checked(((width + 3) / 4) * ((height + 3) / 4) * 16)];
            int rc = pgl_encode(active._context, rgba, (uint)width, (uint)height, buf, message, message.Capacity);
            if (rc == 1)
            {
                RunLimits.Check();
                Interlocked.Increment(ref active._gpuImages);
                output = buf;
                return true;
            }
            if (rc == -1)
            {
                // All 4 persistent Vulkan slots are busy; overflow seamlessly onto CPU threads without disabling GPU!
                Interlocked.Increment(ref active._cpuImages);
                return false;
            }
            active._enabled = false;
            active._progress?.Report("Vulkan encode failed; continuing on CPU: " + message);
            Interlocked.Increment(ref active._cpuImages);
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref active._inFlight);
        }
    }

    public static ComputeBenchmark Benchmark(bool allowSoftware = false)
    {
        IntPtr context = IntPtr.Zero;
        try { return Measure(allowSoftware, out context); }
        finally { if (context != IntPtr.Zero) pgl_destroy(context); }
    }

    private static ComputeBenchmark Measure(bool allowSoftware, out IntPtr context)
    {
        context = IntPtr.Zero;
        var message = new StringBuilder(1024);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return new(false, "CPU", false, false, 0, 0, 0, "The optional Vulkan backend is packaged for Linux and Windows.");
        try
        {
            context = pgl_create(message, message.Capacity, out int software);
            if (context == IntPtr.Zero)
                return new(false, message.ToString(), false, false, 0, 0, 0, "Vulkan unavailable; CPU fallback.");
            string device = message.ToString();
            if (software != 0 && !allowSoftware)
                return new(true, device, true, false, 0, 0, 0, "Software Vulkan is not a GPU acceleration device.");
            const int size = 512;
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    pixels[i] = (byte)(x / 2);
                    pixels[i + 1] = (byte)(y / 2);
                    pixels[i + 2] = (byte)((x ^ y) & 255);
                    pixels[i + 3] = (byte)((x + y) % 17 == 0 ? 0 : 255); // exercise both opaque and alpha blocks
                }
            _ = Bc7Encoder.EncodeImageCpu(pixels, 32, 32);
            var encoded = new byte[size * size];
            if (pgl_encode(context, pixels, size, size, encoded, message, message.Capacity) != 1)
                return new(true, device, software != 0, false, 0, 0, 0, message.ToString());
            var watch = Stopwatch.StartNew();
            var cpu = Bc7Encoder.EncodeImageCpu(pixels, size, size);
            double cpuMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            if (pgl_encode(context, pixels, size, size, encoded, message, message.Capacity) != 1)
                return new(true, device, software != 0, false, cpuMs, 0, 0, message.ToString());
            double gpuMs = watch.Elapsed.TotalMilliseconds, error = 0;
            Span<byte> a = stackalloc byte[64], b = stackalloc byte[64];
            for (int i = 0; i < encoded.Length; i += 16)
            {
                Bc7Decoder.DecodeBlock(cpu.AsSpan(i, 16), a);
                Bc7Decoder.DecodeBlock(encoded.AsSpan(i, 16), b);
                for (int j = 0; j < 64; j += 4)
                {
                    double da = a[j + 3] - b[j + 3];
                    error += da * da;
                    double w = pixels[(i / 16) * 4] >= 0 ? (a[j + 3] / 255.0) : 1.0;
                    for (int c = 0; c < 3; c++) { double delta = a[j + c] - b[j + c]; error += w * delta * delta; }
                }
            }
            error /= pixels.Length;
            bool passed = error <= 16;
            string recommendation = software != 0 && passed
                ? "Software Vulkan quality check passed; timings are validation only, and automatic mode keeps CPU."
                : !passed
                    ? "GPU quality check failed; CPU fallback."
                    : gpuMs < cpuMs * 0.85
                        ? "Measured Vulkan speedup; automatic mode may use it."
                        : "No measured speedup on this workload; automatic mode keeps CPU.";
            return new(true, device, software != 0, passed, cpuMs, gpuMs, error, recommendation);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(false, "CPU", false, false, 0, 0, 0, "Optional Vulkan library is unavailable: " + e.GetType().Name);
        }
    }

    public void Dispose()
    {
        lock (_initGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        SpinWait spin = default;
        while (Volatile.Read(ref _inFlight) > 0) spin.SpinOnce();
        if (_context != IntPtr.Zero)
        {
            pgl_destroy(_context);
            _context = IntPtr.Zero;
        }
        if (_attempted)
            _progress?.Report($"Texture compute finished: {_gpuImages:N0} Vulkan images, {_cpuImages:N0} CPU images.");
        Active.Value = _previous;
    }

    [DllImport("pengulod_compute", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pgl_create(StringBuilder message, int capacity, out int software);

    [DllImport("pengulod_compute", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pgl_destroy(IntPtr context);

    [DllImport("pengulod_compute", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pgl_encode(IntPtr context, byte[] pixels, uint width, uint height, byte[] output, StringBuilder message, int capacity);
}
