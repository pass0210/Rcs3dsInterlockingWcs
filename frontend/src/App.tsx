import { Navigate, Route, Routes } from 'react-router-dom'
import { Layout } from './components/Layout'
import { MonitorPage } from './pages/MonitorPage'
import { SortersPage } from './pages/SortersPage'
import { OpsPage } from './pages/OpsPage'
import { B2cDataGenPage } from './pages/B2cDataGenPage'
import { DataGeneratorPage } from './pages/DataGeneratorPage'
import { LogsPage } from './pages/LogsPage'
import { ComparisonPage } from './pages/ComparisonPage'
import { BoxesPage } from './pages/BoxesPage'
import { SettingsPage } from './pages/SettingsPage'
import { homePathFor, useUiMode } from '@/lib/uiMode'

// 활성 모드의 기본 진입 경로로 리다이렉트(b2c→/monitor, b2b→/data-generator).
// "/"·미매칭(*) 공용. 모드 변경 시 이 컴포넌트가 재평가돼 올바른 랜딩으로 보낸다.
function ModeHome() {
  const { mode } = useUiMode()
  return <Navigate to={homePathFor(mode)} replace />
}

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<ModeHome />} />
        {/* 페이지 ① 모니터링(B2C·F1) */}
        <Route path="/monitor" element={<MonitorPage />} />
        {/* 페이지 ② 3DS 워드(B2C·읽기 전용·F2) */}
        <Route path="/sorters" element={<SortersPage />} />
        {/* 페이지 ③ 운영 제어(B2C·소터 제어·F3b) */}
        <Route path="/ops" element={<OpsPage />} />
        {/* 페이지 ④ B2C 테스트 데이터 생성·초기화(S-B2C-DATAGEN) */}
        <Route path="/b2c/test-data" element={<B2cDataGenPage />} />
        {/* B2B 데이터 생성/관리(S-B2B-2b) */}
        <Route path="/data-generator" element={<DataGeneratorPage />} />
        {/* B2B 조회 3화면(S-B2B-3b): 로그 조회 · 결과 비교 · 박스 조회 */}
        <Route path="/logs" element={<LogsPage />} />
        <Route path="/comparison" element={<ComparisonPage />} />
        <Route path="/boxes" element={<BoxesPage />} />
        {/* B2B 설정(S-B2B-2c): 인쇄 설정(심볼로지·값표시·프리셋) */}
        <Route path="/settings" element={<SettingsPage />} />
        {/* 미매칭 SPA 경로 → 활성 모드 기본 페이지(백엔드 fallback이 index.html 반환 후 여기서 라우팅). */}
        <Route path="*" element={<ModeHome />} />
      </Route>
    </Routes>
  )
}
