using System.IO.Pipes;
using System.Text;

namespace WorkBuddyRemote.WinUI;

/// <summary>
/// 单实例：命名 Mutex 判断归属，命名管道把“把窗口叫到前面”的请求转给第一个实例。
/// Mutex 用 Local\，多用户各自一个实例即可，避免互相抢锁。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\WorkBuddyRemote.Console";
    private const string PipeName = "workbuddy-remote-console-activate";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool IsFirstInstance { get; private set; }

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return createdNew;
    }

    public void StartListening(Action onActivate)
    {
        if (!IsFirstInstance) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (string.Equals(line, "activate", StringComparison.OrdinalIgnoreCase))
                    {
                        onActivate();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <summary>第二个实例调用：通知首个实例把窗口置前。</summary>
    public static bool SignalFirstInstance(int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
        }
        _cts = null;

        if (_mutex is null) return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
        }
        _mutex.Dispose();
        _mutex = null;
    }
}
