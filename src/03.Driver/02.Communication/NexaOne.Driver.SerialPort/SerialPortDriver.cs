using Microsoft.Extensions.Logging;
using System.IO.Ports;
using System.Text;

namespace NexaOne.Driver.SerialPort;

public sealed class SerialPortDriver : IDisposable
{
    private readonly ILogger<SerialPortDriver> _logger;
    private System.IO.Ports.SerialPort? _port;
    private bool _disposed;

    public event EventHandler<string>? DataReceived;
    public event EventHandler<Exception>? ErrorOccurred;

    public bool IsOpen => _port?.IsOpen ?? false;
    public string? PortName => _port?.PortName;

    public SerialPortDriver(ILogger<SerialPortDriver> logger)
    {
        _logger = logger;
    }

    public void Open(
        string portName,
        int baudRate = 9600,
        int dataBits = 8,
        Parity parity = Parity.None,
        StopBits stopBits = StopBits.One,
        int readTimeoutMs = 1000,
        int writeTimeoutMs = 1000,
        Encoding? encoding = null)
    {
        if (_port?.IsOpen == true)
            throw new InvalidOperationException($"Port {_port.PortName} is already open.");

        _port = new System.IO.Ports.SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            ReadTimeout  = readTimeoutMs,
            WriteTimeout = writeTimeoutMs,
            Encoding     = encoding ?? Encoding.ASCII,
            NewLine      = "\r\n"
        };

        _port.DataReceived  += OnDataReceived;
        _port.ErrorReceived += OnErrorReceived;
        _port.Open();

        _logger.LogInformation("SerialPort opened: {Port} {Baud}bps", portName, baudRate);
    }

    public void Close()
    {
        if (_port is null) return;
        _port.DataReceived  -= OnDataReceived;
        _port.ErrorReceived -= OnErrorReceived;
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
        _port = null;
        _logger.LogInformation("SerialPort closed");
    }

    public void Send(string data)
    {
        EnsureOpen();
        _port!.Write(data);
        _logger.LogDebug("SerialPort sent: {Data}", data);
    }

    public void SendLine(string data)
    {
        EnsureOpen();
        _port!.WriteLine(data);
        _logger.LogDebug("SerialPort sent line: {Data}", data);
    }

    public void SendBytes(byte[] data)
    {
        EnsureOpen();
        _port!.Write(data, 0, data.Length);
        _logger.LogDebug("SerialPort sent {Count} bytes", data.Length);
    }

    public string ReadLine()
    {
        EnsureOpen();
        return _port!.ReadLine();
    }

    public string ReadExisting()
    {
        EnsureOpen();
        return _port!.ReadExisting();
    }

    public async Task<string> ReadUntilAsync(string terminator, CancellationToken ct = default)
    {
        EnsureOpen();
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            if (_port!.BytesToRead > 0)
            {
                sb.Append(_port.ReadExisting());
                if (sb.ToString().Contains(terminator))
                    return sb.ToString();
            }
            await Task.Delay(10, ct);
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> GetAvailablePorts() =>
        System.IO.Ports.SerialPort.GetPortNames();

    private void EnsureOpen()
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException("SerialPort is not open.");
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var data = _port?.ReadExisting() ?? string.Empty;
            if (!string.IsNullOrEmpty(data)) DataReceived?.Invoke(this, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SerialPort read error in DataReceived");
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        var ex = new IOException($"SerialPort error: {e.EventType}");
        _logger.LogError(ex, "SerialPort error event: {Type}", e.EventType);
        ErrorOccurred?.Invoke(this, ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
    }
}
