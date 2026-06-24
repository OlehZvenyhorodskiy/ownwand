using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace OwnWand.Payload;

public class IpcServer
{
    private static NamedPipeServerStream? _serverStream;
    private static StreamReader? _reader;
    private static StreamWriter? _writer;
    private static CancellationTokenSource? _cts;
    private static bool _isRunning;

    public static event Action<string>? CommandReceived;

    public static void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();

        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var pipeName = $"OwnWand_{pid}";

        // Start listening thread
        new Thread(() => ListenLoop(pipeName, _cts.Token))
        {
            IsBackground = true
        }.Start();
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
        CloseConnection();
    }

    public static void SendMessage(string json)
    {
        if (!_isRunning || _writer == null) return;
        try
        {
            lock (_writer)
            {
                _writer.WriteLine(json);
                _writer.Flush();
            }
        }
        catch
        {
            // Ignore write failures
        }
    }

    private static void ListenLoop(string pipeName, CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isRunning)
        {
            try
            {
                _serverStream = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );

                // Wait for client to connect
                var asyncResult = _serverStream.BeginWaitForConnection(null, null);
                while (!asyncResult.IsCompleted)
                {
                    if (token.IsCancellationRequested || !_isRunning)
                    {
                        _serverStream.Close();
                        return;
                    }
                    Thread.Sleep(100);
                }

                _serverStream.EndWaitForConnection(asyncResult);

                _reader = new StreamReader(_serverStream, Encoding.UTF8);
                _writer = new StreamWriter(_serverStream, Encoding.UTF8) { AutoFlush = true };

                // Connection successful, read messages
                while (!token.IsCancellationRequested && _isRunning && _serverStream.IsConnected)
                {
                    var line = _reader.ReadLine();
                    if (line == null) break; // Client disconnected

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        CommandReceived?.Invoke(line);
                    }
                }
            }
            catch
            {
                // Reset connection on exception
            }
            finally
            {
                CloseConnection();
                Thread.Sleep(500); // Wait before retrying
            }
        }
    }

    private static void CloseConnection()
    {
        _writer?.Dispose();
        _writer = null;

        _reader?.Dispose();
        _reader = null;

        _serverStream?.Dispose();
        _serverStream = null;
    }
}
