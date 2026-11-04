// Does Windows discard data the client hasn't read yet when the server side goes away?
// Named pipes: server writes 100 bytes, then (a) DisconnectNamedPipe or (b) CloseHandle.
// TCP:         server sends 100 bytes, then (a) abortive close (RST, linger 0) or (b) graceful close (FIN).
// The client reads only after a delay, so the data is sitting unread when the teardown happens.
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

int delayMs = args.Length > 0 ? int.Parse(args[0]) : 50;
byte[] payload = new byte[100];
new Thread(() => { Thread.Sleep(20000); Console.WriteLine("WATCHDOG: hung in setup, exiting"); Environment.Exit(2); }) { IsBackground = true }.Start();

foreach (bool disconnect in new[] { true, false })
{
    string name = "UnreadDataTest_" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 65536, outBufferSize: 65536); // buffer 0 makes writes block until read
    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None); // sync handle so raw ReadFile works
    var connect = server.WaitForConnectionAsync();
    client.Connect(5000);
    connect.Wait();

    server.Write(payload, 0, payload.Length);
    // no FlushFileBuffers: it blocks until the client has read everything
    if (disconnect) server.Disconnect(); else server.Close();   // Disconnect() = DisconnectNamedPipe; Close() = CloseHandle
    Thread.Sleep(delayMs);

    Report($"named pipe, server {(disconnect ? "DisconnectNamedPipe" : "CloseHandle")}, client reads after {delayMs} ms",
        () => RawReadFile(client.SafePipeHandle),
        () => RawReadFile(client.SafePipeHandle));
}

foreach (bool abortive in new[] { true, false })
{
    using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    listener.Listen(1);
    using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    client.Connect(listener.LocalEndPoint);
    using var server = listener.Accept();

    server.Send(payload);
    if (abortive) server.LingerState = new LingerOption(true, 0);  // close sends RST
    server.Close();
    Thread.Sleep(delayMs);

    Report($"TCP, server {(abortive ? "abortive close (RST)" : "graceful close (FIN)")}, client reads after {delayMs} ms",
        () => { var buf = new byte[4096]; return client.Receive(buf); },
        () => { var buf = new byte[4096]; return client.Receive(buf); });
}

static void Report(string label, Func<int> firstRead, Func<int> secondRead)
{
    Console.WriteLine($"== {label}");
    Console.WriteLine("   first read:  " + Timed(firstRead));
    Console.WriteLine("   second read: " + Timed(secondRead));
}

static string Timed(Func<int> read)
{
    var t = Task.Run(read);
    try
    {
        if (!t.Wait(3000)) return "BLOCKED (no data, no error after 3 s)";
        return $"{t.Result} bytes";
    }
    catch (AggregateException ae) { return Describe(ae.InnerException); }
}

// Raw ReadFile so the real Win32 error surfaces (.NET's PipeStream maps 109 and 233 to "0 bytes").
static int RawReadFile(Microsoft.Win32.SafeHandles.SafePipeHandle h)
{
    var buf = new byte[4096];
    if (!ReadFile(h, buf, buf.Length, out int n, IntPtr.Zero))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    return n;
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool ReadFile(Microsoft.Win32.SafeHandles.SafePipeHandle hFile, byte[] buffer, int nBytesToRead, out int nBytesRead, IntPtr overlapped);

static string Describe(Exception e)
{
    int code = e is SocketException se ? se.NativeErrorCode : e is System.ComponentModel.Win32Exception w ? w.NativeErrorCode : (e.HResult & 0xFFFF);
    return $"{e.GetType().Name} Win32={code} \"{e.Message}\"";
}
