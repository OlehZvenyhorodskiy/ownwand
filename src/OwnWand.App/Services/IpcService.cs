using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using OwnWand.Core.Models;

namespace OwnWand.App.Services;

public class IpcService
{
    private NamedPipeClientStream? _pipeClient;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private bool _isConnected;

    public event EventHandler<IpcMessage>? MessageReceived;
    public event EventHandler? Disconnected;

    public bool IsConnected => _isConnected;

    public async Task ConnectAsync(int pid)
    {
        await DisconnectAsync();

        var pipeName = $"OwnWand_{pid}";
        _pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _cts = new CancellationTokenSource();

        try
        {
            // Try to connect with a 5 second timeout
            await _pipeClient.ConnectAsync(5000, _cts.Token);
            _writer = new StreamWriter(_pipeClient) { AutoFlush = true };
            _reader = new StreamReader(_pipeClient);
            _isConnected = true;

            // Start listening in a background thread
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            await DisconnectAsync();
            throw new Exception($"Failed to connect to IPC pipe '{pipeName}': {ex.Message}", ex);
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        _cts = null;
        _isConnected = false;

        if (_writer != null)
        {
            try { await _writer.DisposeAsync(); } catch { }
            _writer = null;
        }

        if (_reader != null)
        {
            try { _reader.Dispose(); } catch { }
            _reader = null;
        }

        if (_pipeClient != null)
        {
            try { await _pipeClient.DisposeAsync(); } catch { }
            _pipeClient = null;
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendMessageAsync(IpcMessage message)
    {
        if (!_isConnected || _writer == null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(message);
            await _writer.WriteLineAsync(json);
        }
        catch
        {
            await DisconnectAsync();
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isConnected && _reader != null)
        {
            try
            {
                var line = await _reader.ReadLineAsync();
                if (line == null)
                {
                    // Pipe disconnected
                    break;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                var message = JsonSerializer.Deserialize<IpcMessage>(line);
                if (message != null)
                {
                    MessageReceived?.Invoke(this, message);
                }
            }
            catch
            {
                break;
            }
        }

        await DisconnectAsync();
    }
}
