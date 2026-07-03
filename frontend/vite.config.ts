import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

// ─────────────────────────────────────────────────────────────────────────────
// Vite 설정
//   · dev: :5173 + proxy(/api → http://localhost:5080) — 프론트/백 동시 기동 개발.
//     하드코딩 금지 원칙상 백엔드 dev URL만 명시(절대규칙 #7은 백엔드 appsettings 대상;
//     dev proxy 타깃은 여기 vite config에 명시하는 것이 계약 §0/§4 규정).
//   · build: 산출물을 ../backend/src/Wcs.Api/wwwroot 로 직접 배치 → Wcs.Api 단일 서버가 SPA+API 서빙.
//     MSBuild-npm 결합 없이 vite build.outDir 하나로 산출(계약 Q6=자동, 수동 복사 단계 0).
//     outDir가 프로젝트 루트 밖이라 emptyOutDir:true로 명시 허용.
// ─────────────────────────────────────────────────────────────────────────────
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
        target: 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: '../backend/src/Wcs.Api/wwwroot',
    emptyOutDir: true,
  },
})
