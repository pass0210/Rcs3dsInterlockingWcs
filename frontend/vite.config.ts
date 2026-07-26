import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

// ─────────────────────────────────────────────────────────────────────────────
// Vite 설정
//   · dev: :5173 + proxy(/api → http://localhost:5205) — 프론트/백 동시 기동 개발.
//     하드코딩 금지 원칙상 백엔드 dev URL만 명시(절대규칙 #7은 백엔드 appsettings 대상;
//     dev proxy 타깃은 여기 vite config에 명시하는 것이 계약 §0/§4 규정).
//     ★ 격리 검증(3-Tier Generator/Evaluator 전용 포트)을 위해 proxy 타깃을 env `VITE_API_TARGET` 로
//       오버라이드 가능(미설정 시 기본 :5205). 운영 산출물(build)은 동일 출처 서빙이라 proxy 무관 —
//       이 env 는 dev-server 전용(프로덕션 동작 불변). 예: VITE_API_TARGET=http://localhost:5216 npm run dev.
//   · build: 산출물을 ../backend/src/Wcs.Api/wwwroot 로 직접 배치 → Wcs.Api 단일 서버가 SPA+API 서빙.
//     MSBuild-npm 결합 없이 vite build.outDir 하나로 산출(계약 Q6=자동, 수동 복사 단계 0).
//     outDir가 프로젝트 루트 밖이라 emptyOutDir:true로 명시 허용.
// ─────────────────────────────────────────────────────────────────────────────
const API_TARGET = process.env.VITE_API_TARGET ?? 'http://localhost:5205'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: API_TARGET,
        changeOrigin: true,
      },
      // SignalR 허브(/hubs/monitor) — ws:true 없으면 dev에서 WebSocket 업그레이드(101) 실패(함정 #3).
      // /api proxy와 별도 항목. 운영은 동일 출처라 proxy 불요(상대 경로 /hubs).
      '/hubs': {
        target: API_TARGET,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: '../backend/src/Wcs.Api/wwwroot',
    emptyOutDir: true,
  },
})
