# RTU 리허설 가이드 — 실 PLC 없이 WCS↔Sim3ds RS-485 사전 검증

> 목적: **목요일(7/9) 현장 방문 전에** WCS의 RTU 전송 계층(`ModbusRtuMaster`)을 실 3DS PLC 없이
> 리허설한다. 3DS PLC 시뮬레이터(`Wcs.Sim3ds`)를 **Modbus RTU 슬레이브**로 띄우고, WCS를
> **Modbus RTU 마스터**로 붙여 프레이밍·UnitId·타임아웃·폴링 스냅샷·C/R 핸드셰이크 전 구간을
> 왕복 검증한다.
>
> 관련 스펙: [SPEC.md §6 Sim3ds 동작](SPEC.md) · [§7-A 전송 방식 확정](SPEC.md).

```
 WCS(Transport=Rtu, COM-A) ──[가상/실 시리얼 페어]── Sim3ds(Transport=Rtu, COM-B)
       Modbus 마스터                                        Modbus 슬레이브
```

⚠️ **WCS와 Sim3ds는 시리얼 페어의 서로 다른(반대쪽) 포트를 쓴다.** 같은 COM 포트를 공유할 수 없다.
가상 페어(예: `COM5`↔`COM6`) 또는 물리 어댑터 2개를 크로스 결선해 마련한다.

---

## 0. 시리얼 신호 방향(핵심 개념)

- Sim3ds RTU 슬레이브는 마스터의 FC03(읽기)·FC06/FC16(쓰기) 요청에 응답한다.
- 레지스터 맵(D0~D6)·에코 지연·C_Flag 자체 클리어·ClearR까지 R 유지·잔류 프리셋·고장 주입은
  **TCP와 RTU에서 완전히 동일**하다(전송만 교체). 따라서 RTU 리허설이 통과하면 현장 배선/파라미터
  외의 로직은 그대로 신뢰할 수 있다.

---

## 1. 시리얼 페어 준비 — 두 가지 방법

### 방법 A — com0com 가상 시리얼 페어 (권장, PC 한 대로 완결)

가상 널모뎀 페어를 만드는 오픈소스 드라이버. 두 가상 COM 포트가 서로 크로스 연결된다.

> ⚠️ **관리자 권한 드라이버 설치는 이 프로젝트의 scope 밖**(사용자 결정/작업). 아래는 절차 안내일 뿐,
> 자동화하지 않는다. 서명 이슈가 있는 빌드는 테스트 서명 모드가 필요할 수 있다.

1. com0com 설치본을 내려받아 **관리자 권한으로** 설치한다(서명된 배포본 권장).
2. `Setup` GUI 또는 `setupc.exe`로 페어 하나를 만든다. 예:
   ```
   setupc.exe install PortName=COM5 PortName=COM6
   ```
   → `COM5`(WCS 마스터용)와 `COM6`(Sim3ds 슬레이브용)이 크로스 연결된다.
3. 장치 관리자에서 두 포트가 보이는지 확인한다.
4. 리허설이 끝나면 페어를 제거한다: `setupc.exe remove 0`.

가상 포트는 실제 baud/parity/stopbits 협상을 하지 않으므로(널모뎀), 양쪽 값이 달라도 왕복은 되지만,
**현장 파라미터(9600/Even/One 등)로 맞춰 리허설**하는 것을 권장한다(설정 실수 사전 발견).

### 방법 B — USB-시리얼 어댑터 2개 크로스 결선 (실 RS-232/485 스택 검증)

물리 어댑터 2개를 PC에 꽂고 서로 크로스로 결선한다. **실 OS 시리얼 스택**을 태우므로
프레이밍·타임아웃 실측 커버리지가 방법 A보다 넓다.

RS-232 레벨 어댑터 결선(널모뎀 크로스):
```
어댑터-A TX  ──▶  어댑터-B RX
어댑터-A RX  ◀──  어댑터-B TX
어댑터-A GND ───  어댑터-B GND
```
RS-485(2선식, 현장과 동일 계열)라면 `A(D+)↔A(D+)`, `B(D-)↔B(D-)`, `GND↔GND`로 결선하고
양 끝 종단저항(120Ω)을 고려한다. 어댑터가 half-duplex면 방향 전환(DE/RE 자동) 지원 제품을 쓴다.

장치 관리자에서 두 어댑터의 COM 번호(예: `COM3`, `COM4`)를 확인해 아래 예시의 포트명에 대입한다.

---

## 2. Sim3ds를 RTU 슬레이브로 기동

**파일 편집 없이 CLI 한 줄로** RTU 전환된다(기본은 TCP :1502 — 현행 보존).

```bash
# 슬레이브 = 페어의 한쪽(예: COM6). 현장 파라미터로 맞춤.
dotnet run --project backend/src/Wcs.Sim3ds -- \
  --transport rtu --port COM6 --baud 9600 --parity Even --stopbits One --unit 1
```

기동 콘솔에 리스닝 전송·포트가 명시된다:
```
[HH:mm:ss.fff] Sim3ds 서버 기동 RTU COM6 9600/Even/One unit=1
Sim3ds 기동 완료 (transport=rtu). Ctrl+C로 종료.
```

설정 우선순위: **CLI(`--*`) > 환경변수(`SIM3DS_*`) > `appsettings.Sim3ds.json` > 코드 기본값.**

| 항목 | CLI 스위치 | 환경변수 | 비고 |
|------|-----------|----------|------|
| 전송 | `--transport rtu\|tcp` | `SIM3DS_Transport` | 미지정 기본 = `Tcp` |
| COM 포트(RTU) | `--port COMx` 또는 `--com COMx` | `SIM3DS_PortName` | **RTU 필수**(미지정 시 fail-loud) |
| TCP 포트 | `--port 1502` 또는 `--tcp-port 1502` | `SIM3DS_Port` | RTU면 `--port`는 COM명으로 라우팅 |
| 보드레이트 | `--baud 9600` | `SIM3DS_BaudRate` | |
| 패리티 | `--parity Even\|Odd\|None` | `SIM3DS_Parity` | |
| 스톱비트 | `--stopbits One\|Two` | `SIM3DS_StopBits` | |
| 유닛 ID | `--unit 1` | `SIM3DS_UnitId` | 슬레이브 주소 |
| 타이밍 | `--tilt --sort --move --curfloor --simloop` | `SIM3DS_TiltDelayMs` 등 | ms |

기본값(`appsettings.Sim3ds.json`)은 WCS `Sorters[0]` placeholder와 정합한다:
`BaudRate=9600 · Parity=Even · StopBits=One · UnitId=1 · Timeout=1000`.
단 **PortName은 안전한 기본값이 없다** — RTU 모드에서 미지정 시 명확한 예외로 기동 거부한다
(우발적 COM1 점유 방지).

---

## 3. WCS를 RTU 마스터로 기동

WCS 측 전송 선택은 이미 구현되어 있다(S-RTU). 리허설 시 `Sorters[0]`를 RTU·페어의 반대쪽 포트로
설정한다.

> ⚠️ **아래는 리허설용 설정 예시 스니펫이다. 이 스프린트는 실제 `appsettings.json`을 변경하지 않는다.**
> 리허설 시 (a) 로컬에서 임시 편집하거나, (b) 환경변수 오버라이드(`Sorters__0__*`)로 적용한다.
> 리허설이 끝나면 원상 복구한다.

`backend/src/Wcs.Api/appsettings.json`의 `Sorters[0]` (리허설 값으로 조정):
```jsonc
"Sorters": [
  {
    "ChuteNo": 1,
    "Transport": "Rtu",       // 리허설: Rtu
    "PortName": "COM5",       // 페어의 WCS 쪽(Sim3ds COM6의 반대쪽)
    "BaudRate": 9600,
    "Parity": "Even",
    "StopBits": "One",
    "ReadTimeoutMs": 1000,
    "WriteTimeoutMs": 1000,
    "UnitId": 1,
    "PollIntervalMs": 150,
    "OfflineAfterFailures": 3,
    "Timing": {}
  }
]
```

또는 파일을 건드리지 않고 환경변수로:
```bash
# 예(PowerShell): $env:Sorters__0__Transport="Rtu"; $env:Sorters__0__PortName="COM5"
Sorters__0__Transport=Rtu Sorters__0__PortName=COM5 \
  dotnet run --project backend/src/Wcs.Api
```

WCS 기동 후 소터 폴이 **Online**이 되고, 한 건의 IF-05/IF-10 흐름에서 C/R 핸드셰이크가 왕복하면
RTU 경로가 검증된 것이다.

---

## 4. 리허설 체크리스트 (현장 방문 전)

1. [ ] 시리얼 페어 준비(방법 A com0com 또는 방법 B 어댑터 크로스) — 두 COM 번호 확인.
2. [ ] Sim3ds를 페어의 한쪽(예: COM6)에 RTU로 기동 → 콘솔에 `RTU COM6 9600/Even/One unit=1` 확인.
3. [ ] WCS `Sorters[0]`를 RTU·반대쪽 포트(예: COM5)로 설정 → WCS 기동.
4. [ ] WCS 로그에서 해당 소터 **Online** 전이 확인(폴 성공 = 프레이밍·UnitId·타임아웃 정합).
5. [ ] C/R 핸드셰이크 1건 왕복 확인:
       - Sim3ds 타임라인: `C 수신 … → 분류 시작(Ready=0, TgtFloor 클리어) → R 세팅(R_Seq==C_Seq) → ClearR 후 R_Flag=0`.
       - WCS: 핸드셰이크 결과 `Success`, `R_Seq==C_Seq` 대사 일치.
6. [ ] 파라미터 오정합 사전 발견: baud/parity/stopbits/unit을 일부러 틀리게 줘 OFFLINE·타임아웃이
       기대대로 나는지 관찰(선택).
7. [ ] 현장 실측값(VEICHI PLC RS-485의 실제 baud/parity/stop/unit)을 확인해 위 설정에 대입할 준비.
8. [ ] 리허설 종료 후 WCS `appsettings.json`·환경변수를 원상 복구(현장 값 반영은 별도 절차).

---

## 5. 물리 페어 없이도 되는 검증 (CI·개발 PC)

시리얼 페어를 마련하기 전이라도, 실 `SimServer`(RTU 모드) ↔ WCS `ModbusRtuMaster`의 왕복은
**in-memory fake serial**로 자동 검증된다(물리 COM 불요):

- `backend/tests/Wcs.Tests/Sim3dsRtuTests.cs`
  - `B1_RealSimServerRtu_FakeSerial_HandshakeRoundtrip` — 폴 Online + C/R 핸드셰이크(R_Seq==C_Seq) + ClearR.
  - `B2_RealSimServerRtu_FakeSerial_ResiduePreset_Identical` — 잔류 프리셋이 RTU에서도 동일.
- 실 OS 시리얼 스택 스모크(선택·환경 게이트):
  ```bash
  # client,server 순. 미설정 시 이 테스트만 스킵(사유 출력) — 전체 스위트는 GREEN.
  WCS_RTU_TEST_PORTS=COM5,COM6 dotnet test backend/Wcs.sln --filter "FullyQualifiedName~C1_LiveSerial"
  ```

```bash
# 전송·의미 회귀 없이 전체 검증
dotnet test backend/Wcs.sln
```

---

## 6. 자주 겪는 문제

| 증상 | 원인 후보 | 조치 |
|------|-----------|------|
| Sim3ds 기동 즉시 예외 "PortName이 지정되지 않았습니다" | RTU인데 `--port` 누락 | `--port COMx` 지정 |
| Sim3ds 기동 예외 "알 수 없는 스위치" | CLI 오타(예: `--tranport`) | 콘솔이 지원 스위치 목록을 출력 — 철자 확인 |
| WCS 소터가 계속 OFFLINE | 같은 포트 공유 / baud·parity 불일치 / 결선 오류 | 페어의 반대쪽 포트인지, 파라미터 정합인지 확인 |
| 포트 열기 실패(Access denied) | 다른 프로세스가 COM 점유 | 이전 Sim3ds/터미널 종료 후 재시도 |
| 핸드셰이크 RSEQ_MISMATCH | 잔류 R 또는 파라미터 불일치 | WCS 로그의 arming/잔류 대사 확인(SPEC §4-A) |
