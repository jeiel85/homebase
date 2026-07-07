# 07. Monitoring Collectors

## 1. Collector 설계 원칙

모든 collector는 실패해도 전체 Agent를 죽이면 안 된다.

공통 규칙:

- `CollectAsync`는 timeout을 가져야 한다.
- 실패 시 exception을 밖으로 던지지 않고 `CollectorResult.Failed`로 반환한다.
- collector별 마지막 성공/실패 시간을 health에 기록한다.
- metric 수집과 alert 판단을 분리한다.

```csharp
public interface ICollector<TSnapshot>
{
    string Name { get; }
    Task<CollectorResult<TSnapshot>> CollectAsync(CancellationToken ct);
}
```

## 2. System Metrics Collector

### 수집 항목

```csharp
public sealed record SystemMetricSnapshot(
    DateTimeOffset CollectedAt,
    double? CpuUsagePercent,
    long? TotalMemoryBytes,
    long? AvailableMemoryBytes,
    double? MemoryUsagePercent,
    TimeSpan Uptime,
    string HostName,
    string? OsVersion);
```

### 구현 후보

- CPU: PerformanceCounter 또는 OS API 기반
- Memory: `GC`가 아니라 OS 전체 메모리 기준. WMI/CIM 또는 Win32 API 사용
- Uptime: `Win32_OperatingSystem.LastBootUpTime` 또는 `Environment.TickCount64`

### 주의

PerformanceCounter CPU 값은 첫 호출에서 정확하지 않을 수 있다. 초기 샘플은 버리고 두 번째 샘플부터 사용한다.

## 3. Disk Collector

### 수집 항목

```csharp
public sealed record DiskSnapshot(
    string Name,
    string DriveType,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double UsedPercent,
    bool IsReady);
```

### 구현

`System.IO.DriveInfo.GetDrives()` 사용.

### Alert threshold

Default:

```json
{
  "disk": {
    "warnFreeBelowGb": 20,
    "criticalFreeBelowGb": 10,
    "warnUsedPercentAbove": 85,
    "criticalUsedPercentAbove": 95
  }
}
```

## 4. Network Status Checker

### 수집 항목

```csharp
public sealed record NetworkStatusSnapshot(
    bool IsOnline,
    string? PrimaryIPv4,
    string? PrimaryIPv6,
    IReadOnlyList<string> ActiveAdapters,
    long? PingLatencyMs,
    string? FailureReason);
```

### 구현

- 네트워크 인터페이스 확인
- DNS lookup 또는 HTTP HEAD probe
- optional ping

Default probe URLs:

- `https://api.telegram.org`
- `https://www.microsoft.com`

## 5. Process Collector

### 수집 항목

```csharp
public sealed record ProcessWatchStatus(
    string WatchName,
    IReadOnlyList<string> ProcessNames,
    bool IsRunning,
    int InstanceCount,
    IReadOnlyList<ProcessInstanceInfo> Instances);
```

```csharp
public sealed record ProcessInstanceInfo(
    int ProcessId,
    string ProcessName,
    string? MainModulePath,
    DateTimeOffset? StartedAt,
    long? WorkingSetBytes);
```

### 구현

- `Process.GetProcessesByName`
- `.exe` suffix normalization
- MainModule 접근 실패는 무시 가능. 권한 이슈가 흔하다.

### Watch config

```json
{
  "processWatches": [
    {
      "name": "Ollama",
      "processNames": ["ollama", "ollama.exe"],
      "alertWhenMissing": true,
      "alertWhenRunning": false,
      "minInstances": 1,
      "severity": "Warning"
    }
  ]
}
```

## 6. Windows Service Collector

### 수집 항목

```csharp
public sealed record WindowsServiceWatchStatus(
    string WatchName,
    string ServiceName,
    string? DisplayName,
    string? Status,
    bool MatchesExpectedStatus,
    string? FailureReason);
```

### 구현

- `System.ServiceProcess.ServiceController`
- service not found → warning
- access denied → error log, status unknown

## 7. Event Log Watcher

### 대상 로그

Default:

- Application
- System

Levels:

- Critical
- Error

Optional:

- Warning은 기본 비활성화. 너무 많을 수 있음.

### Event model

```csharp
public sealed record WindowsEventLogItem(
    string LogName,
    long RecordId,
    int EventId,
    string? ProviderName,
    string Level,
    DateTimeOffset TimeCreated,
    string? MachineName,
    string? Message);
```

### Dedup key

```text
logName + eventId + providerName + normalizedMessageHash
```

### Alert formatting

```text
🚨 Windows Event Error
Log: Application
Provider: Application Error
EventId: 1000
Time: 2026-07-07 17:25:00
Message: xxx.exe crashed...
```

Message는 최대 500자까지만 보낸다.

## 8. HTTP Endpoint Monitor

개발 환경 감시용.

```csharp
public sealed record HttpEndpointStatus(
    string Name,
    string Url,
    bool IsHealthy,
    int? StatusCode,
    long? LatencyMs,
    string? Error);
```

Config:

```json
{
  "httpEndpoints": [
    {
      "name": "Kunsarang Local Dev",
      "url": "http://localhost:3000",
      "method": "GET",
      "timeoutSeconds": 5,
      "expectedStatusCodes": [200, 302]
    }
  ]
}
```

## 9. TCP Port Monitor

```csharp
public sealed record TcpPortStatus(
    string Name,
    string Host,
    int Port,
    bool IsOpen,
    long? LatencyMs,
    string? Error);
```

Example:

```json
{
  "tcpPorts": [
    { "name": "PostgreSQL", "host": "127.0.0.1", "port": 5432 },
    { "name": "Ollama", "host": "127.0.0.1", "port": 11434 }
  ]
}
```

## 10. Alert evaluator

Collector는 상태만 수집하고, alert 판단은 evaluator가 한다.

```csharp
public interface IAlertEvaluator<TSnapshot>
{
    IReadOnlyList<AlertEvent> Evaluate(TSnapshot snapshot, AlertEvaluationContext context);
}
```

Alert severity:

```csharp
public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
    Recovery
}
```

## 11. Recovery alert

장애 상태가 정상으로 돌아왔을 때 recovery alert를 보낼 수 있다.

Example:

```text
✅ PostgreSQL recovered
Service postgresql-x64-16 is Running again.
```

기본값:

- critical 대상: recovery alert on
- warning 대상: recovery alert off
