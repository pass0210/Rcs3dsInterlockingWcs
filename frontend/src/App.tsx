import { Navigate, Route, Routes } from 'react-router-dom'
import { Layout } from './components/Layout'
import { MonitorPage } from './pages/MonitorPage'
import { SortersPage } from './pages/SortersPage'

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<Navigate to="/monitor" replace />} />
        <Route path="/monitor" element={<MonitorPage />} />
        {/* 페이지 ② 3DS 워드(읽기 전용·F2) */}
        <Route path="/sorters" element={<SortersPage />} />
        {/* 미매칭 SPA 경로 → 모니터링으로(백엔드 fallback이 index.html 반환 후 여기서 라우팅). */}
        <Route path="*" element={<Navigate to="/monitor" replace />} />
      </Route>
    </Routes>
  )
}
