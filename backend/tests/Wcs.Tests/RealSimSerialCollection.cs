using Xunit;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// RealSimSerial — 실 Sim3ds(Modbus TCP) 통합·E2E 테스트 비병렬 컬렉션.
//
// 배경(교훈 e2e-parallel-load-surfaces-integration-flakes / s9-flake-under-e2e-load):
//   실 Modbus TCP Sim을 띄우는 통합·E2E 테스트가 xUnit 기본 병렬로 동시에 다수 실행되면
//   소켓/CPU 경합으로 타이밍-취약 테스트(S9·MultiSorter Online·핸드셰이크 R_Seq 등)의 저빈도
//   flake가 발현한다(baseline도 ~1/5 발현 실측). 단위 테스트가 아니라 **실 소켓 I/O 타이밍**이
//   근본이므로, 이 컬렉션(DisableParallelization)으로 실 Sim TCP 테스트를 서로/타 컬렉션과
//   동시 실행되지 않게 격리한다. 순수/Fake/인메모리 단위 테스트는 병렬 유지(속도 보존 — MonitorHubSerial 동형).
//
//   S-TWO-FLOOR-CONTROL A가 실 Sim E2E(E2EGroupK)를 추가하며 경합 부하가 늘어 이 격리를 정식화한다
//   (TODO의 옵션 B "실-Sim 통합+E2E만 직렬 컬렉션" 착수 — 나머지 단위 병렬 보존).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>실 Sim3ds(Modbus TCP) 통합·E2E 테스트 비병렬 컬렉션 정의(마커).</summary>
[CollectionDefinition("RealSimSerial", DisableParallelization = true)]
public sealed class RealSimSerialCollection { }
