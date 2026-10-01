using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Resenz.Core.Services;

namespace Resenz.Elevated;

internal static class Program
{
    private static Process? _coreProcess;
    private static readonly object Gate = new();
    private static string? _lastError;

    // 상승 호스트는 메인 프로그램 로그를 볼 수 없으므로 코어 출력을 반드시 파일로 남겨야 함. 그러지 않으면 TUN 등 실패 원인 추적 불가.
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Resenz", "elevated.log");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static async Task<int> Main(string[] args)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Resenz.Elevated 관리자 권한 필요.");
            return 5;
        }

        var ownerSid = GetOwnerSid();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 메인 프로그램 생존 감시: 메인 프로그램 종료 후 이 호스트를 자동 종료해 남은 프로세스가 실행 파일을 잠그지 않게 함.
        var ownerPid = ParseOwnerPid(args);
        if (ownerPid > 0)
        {
            _ = Task.Run(() => WatchOwnerAsync(ownerPid, cts.Token));
        }

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer(ownerSid);
                await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                await HandleClientAsync(server, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
            }
        }

        lock (Gate)
        {
            StopCore();
        }
        return 0;
    }

    private static int ParseOwnerPid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--owner", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid))
            {
                return pid;
            }
        }
        return -1;
    }

    private static async Task WatchOwnerAsync(int ownerPid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!IsProcessAlive(ownerPid))
            {
                lock (Gate)
                {
                    StopCore();
                }
                Environment.Exit(0);
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static NamedPipeServerStream CreateServer(string ownerSid)
    {
        var security = new PipeSecurity();

        // 상승 프로세스의 Owner는 BUILTIN이 됨\Administrators,따라서 Owner만으로 승인 판단 불가,
        // 그렇지 않으면 일반 권한 메인 프로그램이 파이프에 연결 불가(Access denied).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(ownerSid),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ElevatedProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            65536,
            65536,
            security);
    }

    private static string GetOwnerSid()
    {
        try
        {
            // 현재 로그인 사용자 SID 우선 사용(권한 상승 후 Owner는 Administrators가 되어 사용 불가).
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) return identity.User.Value;
        }
        catch
        {
        }
        return new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value;
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;

        ElevatedRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ElevatedRequest>(line, ElevatedProtocol.JsonOptions);
        }
        catch
        {
            await WriteAsync(writer, new ElevatedResponse(false, "요청 해석 불가")).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteAsync(writer, new ElevatedResponse(false, "빈 요청")).ConfigureAwait(false);
            return;
        }

        var response = request.Command switch
        {
            ElevatedProtocol.Start => StartCore(request),
            ElevatedProtocol.Stop => StopCoreCommand(),
            ElevatedProtocol.Status => StatusCommand(),
            ElevatedProtocol.Shutdown => ShutdownCommand(),
            _ => new ElevatedResponse(false, $"알 수 없는 명령: {{request.Command}"),
        };

        await WriteAsync(writer, response).ConfigureAwait(false);
    }

    private static async Task WriteAsync(StreamWriter writer, ElevatedResponse response)
    {
        var json = JsonSerializer.Serialize(response, ElevatedProtocol.JsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private static ElevatedResponse StartCore(ElevatedRequest request)
    {
        if (string.IsNullOrEmpty(request.BinaryPath) || string.IsNullOrEmpty(request.ConfigPath))
            return new ElevatedResponse(false, "코어 경로 또는 설정 경로 없음");

        lock (Gate)
        {
            if (_coreProcess is { HasExited: false })
                return new ElevatedResponse(true, "코어 이미 실행 중", _coreProcess.Id);

            if (!File.Exists(request.BinaryPath))
                return new ElevatedResponse(false, $"코어를 찾을 수 없음: {{request.BinaryPath}");
            if (!File.Exists(request.ConfigPath))
                return new ElevatedResponse(false, $"설정을 찾을 수 없음: {{request.ConfigPath}");

            var workingDir = request.WorkingDirectory ?? Path.GetDirectoryName(request.BinaryPath)!;

            var psi = new ProcessStartInfo
            {
                FileName = request.BinaryPath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(workingDir);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(request.ConfigPath);
            if (!string.IsNullOrEmpty(request.Secret))
            {
                psi.ArgumentList.Add("-secret");
                psi.ArgumentList.Add(request.Secret);
            }

            try
            {
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => Capture(e.Data);
                process.ErrorDataReceived += (_, e) => Capture(e.Data);
                process.Exited += (_, _) => _lastError = $"코어 종료됨(코드 {process.ExitCode})";

                if (!process.Start())
                    return new ElevatedResponse(false, "코어 프로세스 기동 불가");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _coreProcess = process;
                _lastError = null;
                return new ElevatedResponse(true, "코어 시작됨", process.Id);
            }
            catch (Exception ex)
            {
                return new ElevatedResponse(false, ex.Message);
            }
        }
    }

    private static void Capture(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _lastError = line;
        Log(line);
    }

    private static ElevatedResponse StopCoreCommand()
    {
        lock (Gate)
        {
            StopCore();
            return new ElevatedResponse(true, "코어 정지됨");
        }
    }

    private static void StopCore()
    {
        var process = _coreProcess;
        _coreProcess = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static ElevatedResponse StatusCommand()
    {
        lock (Gate)
        {
            if (_coreProcess is { HasExited: false })
                return new ElevatedResponse(true, _lastError, _coreProcess.Id);
            return new ElevatedResponse(false, _lastError ?? "코어 미실행");
        }
    }

    private static ElevatedResponse ShutdownCommand()
    {
        lock (Gate)
        {
            StopCore();
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            Environment.Exit(0);
        });
        return new ElevatedResponse(true, "상승 호스트 종료 중");
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
