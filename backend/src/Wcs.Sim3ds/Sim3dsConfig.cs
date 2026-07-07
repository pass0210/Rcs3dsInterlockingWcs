using Microsoft.Extensions.Configuration;

namespace Wcs.Sim3ds;

// ════════════════════════════════════════════════════════════════════════════
// Sim3dsConfig — 설정 해석 (S-SIM3DS-RTU · Q1 확정 C안)
//
//   우선순위(낮음 → 높음):
//     코드 기본값(Options record)  <  appsettings.Sim3ds.json  <  환경변수(SIM3DS_*)  <  CLI(--*)
//
//   - 기본(json 없음·env 없음·CLI 없음) = Transport=Tcp 127.0.0.1:1502 → 기존 dotnet run 바이트 동일.
//   - 목요일 현장: `dotnet run -- --transport rtu --port COMx` 한 줄로 RTU 전환(파일 편집 불요).
//   - 하드코딩 타이밍/포트 금지(절대규칙 #7) — 전부 이 해석기를 통해 주입.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// json + 환경변수 + CLI 인자를 병합해 <see cref="SimServer.Options"/>를 산출한다.
/// 순수 함수(정적)로 테스트에서 직접 파싱 결과를 단언할 수 있다(계약 (a)).
/// </summary>
public static class Sim3dsConfig
{
    public const string FileName         = "appsettings.Sim3ds.json";
    public const string EnvPrefix        = "SIM3DS_";
    public const string DefaultTransport = "Tcp";

    /// <summary>
    /// 실행 인자·환경·설정파일을 병합해 Options를 산출한다.
    /// <paramref name="basePath"/> 미지정 시 <see cref="AppContext.BaseDirectory"/>(출력 폴더).
    /// </summary>
    public static SimServer.Options Resolve(string[] args, string? basePath = null)
    {
        basePath ??= AppContext.BaseDirectory;

        // 1) 파일(기본값) + 환경변수. CLI 라우팅(--port)의 "유효 Transport" 판정에도 사용.
        var fileEnv = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: EnvPrefix)
            .Build();

        // 2) CLI 친화 스위치 → 설정 키(최우선). --port는 유효 Transport에 따라 라우팅.
        var effectiveTransport = ResolveEffectiveTransport(args, fileEnv);
        var cli                = ParseCli(args, effectiveTransport);

        // 3) 병합(CLI > env > json > 기본값)
        var config = new ConfigurationBuilder()
            .AddConfiguration(fileEnv)
            .AddInMemoryCollection(cli)
            .Build();

        return ToOptions(config);
    }

    /// <summary>병합된 <see cref="IConfiguration"/> → Options 바인딩(루트 레벨 키).</summary>
    public static SimServer.Options ToOptions(IConfiguration config) =>
        config.Get<SimServer.Options>() ?? new SimServer.Options();

    // ─── CLI 파싱 ──────────────────────────────────────────────────────────────

    /// <summary>CLI --transport가 있으면 그것을, 없으면 파일/환경값(없으면 Tcp)을 유효 전송으로 본다.</summary>
    private static string ResolveEffectiveTransport(string[] args, IConfiguration fileEnv)
    {
        var cli = GetSwitchValue(args, "--transport");
        if (!string.IsNullOrWhiteSpace(cli)) return cli!;
        return fileEnv["Transport"] ?? DefaultTransport;
    }

    /// <summary>
    /// 친화 스위치(`--transport`, `--port` 등)를 설정 키-값으로 변환한다.
    /// `--key value`·`--key=value` 둘 다 지원. 알 수 없는 스위치·값 누락은 fail-loud(오타 방지).
    /// </summary>
    private static Dictionary<string, string?> ParseCli(string[] args, string effectiveTransport)
    {
        bool isRtu = string.Equals(effectiveTransport.Trim(), "Rtu", StringComparison.OrdinalIgnoreCase);
        var  map   = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        int i = 0;
        while (i < args.Length)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Sim3ds: 알 수 없는 인자 '{token}'. 스위치는 '--이름 값' 형식입니다.");

            string  key;
            string? val;
            int     eq = token.IndexOf('=');
            if (eq >= 0)
            {
                key = token[..eq];
                val = token[(eq + 1)..];
                i  += 1;
            }
            else
            {
                key = token;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Sim3ds: 스위치 '{key}'에 값이 없습니다.");
                val = args[i + 1];
                i  += 2;
            }

            MapSwitch(map, key, val, isRtu);
        }
        return map;
    }

    private static void MapSwitch(Dictionary<string, string?> map, string key, string? val, bool isRtu)
    {
        switch (key.ToLowerInvariant())
        {
            case "--transport":      map["Transport"]      = val; break;
            case "--host":           map["Host"]           = val; break;
            case "--tcp-port":       map["Port"]           = val; break;
            case "--com":
            case "--portname":       map["PortName"]       = val; break;
            case "--port":
                // 유효 Transport=Rtu면 COM 포트명, 아니면 TCP 포트 번호.
                if (isRtu) map["PortName"] = val;
                else       map["Port"]     = val;
                break;
            case "--baud":           map["BaudRate"]       = val; break;
            case "--parity":         map["Parity"]         = val; break;
            case "--stopbits":       map["StopBits"]       = val; break;
            case "--unit":           map["UnitId"]         = val; break;
            case "--read-timeout":   map["ReadTimeoutMs"]  = val; break;
            case "--write-timeout":  map["WriteTimeoutMs"] = val; break;
            // 시뮬레이션 타이밍(선택)
            case "--tilt":           map["TiltDelayMs"]    = val; break;
            case "--sort":           map["SortDurationMs"] = val; break;
            case "--move":           map["MoveDurationMs"] = val; break;
            case "--curfloor":       map["InitialCurFloor"]= val; break;
            case "--simloop":        map["SimLoopMs"]      = val; break;
            default:
                throw new InvalidOperationException(
                    $"Sim3ds: 알 수 없는 스위치 '{key}'. " +
                    "지원: --transport --host --port(tcp번호|COM명) --tcp-port --com --baud " +
                    "--parity --stopbits --unit --read-timeout --write-timeout --tilt --sort --move --curfloor --simloop.");
        }
    }

    /// <summary>args에서 특정 스위치의 값을 찾는다(`--k v`·`--k=v`). 없으면 null.</summary>
    private static string? GetSwitchValue(string[] args, string switchName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals(switchName, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;
            if (a.StartsWith(switchName + "=", StringComparison.OrdinalIgnoreCase))
                return a[(switchName.Length + 1)..];
        }
        return null;
    }
}
