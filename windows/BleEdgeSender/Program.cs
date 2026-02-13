using System.Threading.Channels;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BleEdgeSender;

internal sealed class SenderApp : IAsyncDisposable
{
    private const int LeftEdgeThresholdPx = 0;
    private const int ExitKeyVk = 0x7B; // F12

    private readonly BlePeripheralServer _bleServer = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly object _stateLock = new();

    private InputCapture? _capture;
    private Task? _sendLoopTask;
    private int _lastX;
    private int _lastY;
    private bool _hasLastPoint;
    private int _anchorX;
    private int _anchorY;
    private int _pendingDx;
    private int _pendingDy;
    private long _lastMouseFlushTimestamp;
    private bool _needsRecenter;

    private volatile bool _remoteMode;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _bleServer.SubscriberStateChanged += OnSubscriberStateChanged;
        await _bleServer.StartAsync();

        Console.WriteLine("BLE service started.");
        Console.WriteLine($"Service UUID: {BlePeripheralServer.ServiceUuid}");
        Console.WriteLine($"Char UUID:    {BlePeripheralServer.InputCharacteristicUuid}");
        Console.WriteLine("Move cursor to the left edge to enter remote mode.");
        Console.WriteLine("Press F12 to exit remote mode.");

        _sendLoopTask = Task.Run(() => SendLoopAsync(cancellationToken), cancellationToken);

        _capture = new InputCapture(OnMouseMove, OnMouseButton, OnMouseWheel, OnKey);
        _capture.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void OnSubscriberStateChanged(bool connected)
    {
        Console.WriteLine(connected ? "Mac receiver subscribed." : "Mac receiver disconnected.");

        if (!connected)
        {
            DisableRemoteMode("Receiver disconnected");
        }
    }

    private bool OnMouseMove(MouseMoveEvent ev)
    {
        if (ev.IsInjected)
        {
            return _remoteMode;
        }

        if (!_remoteMode)
        {
            if (ev.X <= LeftEdgeThresholdPx && _bleServer.HasSubscriber)
            {
                lock (_stateLock)
                {
                    _remoteMode = true;
                    _anchorX = Math.Max(2, ProgramNative.GetSystemMetrics(0) / 2);
                    _anchorY = ev.Y;
                    _lastX = _anchorX;
                    _lastY = _anchorY;
                    _hasLastPoint = true;
                    _pendingDx = 0;
                    _pendingDy = 0;
                    _lastMouseFlushTimestamp = Stopwatch.GetTimestamp();
                    _needsRecenter = true;
                }

                Console.WriteLine("Remote mode ON");
            }

            return false;
        }

        short dx = 0;
        short dy = 0;

        lock (_stateLock)
        {
            if (!_hasLastPoint)
            {
                _lastX = ev.X;
                _lastY = ev.Y;
                _hasLastPoint = true;
                return false;
            }

            var rawDx = ev.X - _lastX;
            var rawDy = ev.Y - _lastY;

            _lastX = ev.X;
            _lastY = ev.Y;

            // Ignore obvious coordinate spikes from warp/reposition jitter.
            if (Math.Abs(rawDx) > 1200 || Math.Abs(rawDy) > 1200)
            {
                return true;
            }

            if (rawDx > short.MaxValue) rawDx = short.MaxValue;
            if (rawDx < short.MinValue) rawDx = short.MinValue;
            if (rawDy > short.MaxValue) rawDy = short.MaxValue;
            if (rawDy < short.MinValue) rawDy = short.MinValue;

            dx = (short)rawDx;
            dy = (short)rawDy;
        }

        if (dx != 0 || dy != 0)
        {
            QueueMouseDelta(dx, dy);
        }

        MaybeRecenter();
        return true;
    }

    private bool OnMouseButton(MouseButtonEvent ev)
    {
        if (!_remoteMode)
        {
            return false;
        }

        FlushMouseDelta(force: true);
        _outbound.Writer.TryWrite(InputProtocol.MouseButton(ev.Button, ev.IsDown));
        return true;
    }

    private bool OnMouseWheel(short delta)
    {
        if (!_remoteMode)
        {
            return false;
        }

        FlushMouseDelta(force: true);
        _outbound.Writer.TryWrite(InputProtocol.Wheel(delta));
        return true;
    }

    private bool OnKey(KeyEvent ev)
    {
        if (_remoteMode && ev.VirtualKey == ExitKeyVk && ev.IsDown)
        {
            DisableRemoteMode("Remote mode OFF (F12)");
            return true;
        }

        if (!_remoteMode)
        {
            return false;
        }

        FlushMouseDelta(force: true);
        if (HidUsageMap.TryMapVkToUsage(ev.VirtualKey, out var usage))
        {
            _outbound.Writer.TryWrite(InputProtocol.Key(usage, ev.IsDown));
        }

        return true;
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var packet in _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await _bleServer.SendPacketAsync(packet, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Send failed: {ex.Message}");
            }
        }
    }

    private void DisableRemoteMode(string reason)
    {
        if (!_remoteMode)
        {
            return;
        }

        lock (_stateLock)
        {
            _remoteMode = false;
            _hasLastPoint = false;
            _pendingDx = 0;
            _pendingDy = 0;
            _needsRecenter = false;
        }

        Console.WriteLine(reason);
    }

    private void QueueMouseDelta(short dx, short dy)
    {
        var shouldFlush = false;
        lock (_stateLock)
        {
            _pendingDx += dx;
            _pendingDy += dy;

            var elapsedMs = (Stopwatch.GetTimestamp() - _lastMouseFlushTimestamp) * 1000.0 / Stopwatch.Frequency;
            shouldFlush = elapsedMs >= 4.0 || Math.Abs(_pendingDx) >= 20 || Math.Abs(_pendingDy) >= 20;
        }

        if (shouldFlush)
        {
            FlushMouseDelta(force: false);
        }
    }

    private void FlushMouseDelta(bool force)
    {
        int dx;
        int dy;
        lock (_stateLock)
        {
            if (!force && _pendingDx == 0 && _pendingDy == 0)
            {
                return;
            }

            dx = _pendingDx;
            dy = _pendingDy;
            _pendingDx = 0;
            _pendingDy = 0;
            _lastMouseFlushTimestamp = Stopwatch.GetTimestamp();
        }

        if (dx == 0 && dy == 0)
        {
            return;
        }

        dx = Math.Clamp(dx, short.MinValue, short.MaxValue);
        dy = Math.Clamp(dy, short.MinValue, short.MaxValue);
        _outbound.Writer.TryWrite(InputProtocol.MouseMove((short)dx, (short)dy));
    }

    private void MaybeRecenter()
    {
        int currentX;
        int currentY;
        bool shouldWarp = false;

        lock (_stateLock)
        {
            if (!_remoteMode)
            {
                return;
            }

            currentX = _lastX;
            currentY = _lastY;
            shouldWarp = _needsRecenter ||
                         Math.Abs(currentX - _anchorX) > 120 ||
                         Math.Abs(currentY - _anchorY) > 120;
            _needsRecenter = false;
        }

        if (!shouldWarp)
        {
            return;
        }

        if (ProgramNative.SetCursorPos(_anchorX, _anchorY))
        {
            lock (_stateLock)
            {
                _lastX = _anchorX;
                _lastY = _anchorY;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _capture?.Dispose();

        _outbound.Writer.TryComplete();
        if (_sendLoopTask != null)
        {
            try
            {
                await _sendLoopTask;
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        await _bleServer.StopAsync();
    }
}

internal static partial class ProgramNative
{
    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);
}

internal static class Program
{
    public static async Task Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await using var app = new SenderApp();
        await app.RunAsync(cts.Token);
    }
}
